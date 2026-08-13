using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PhoneLink.Application.Ports;
using PhoneLink.DeviceCommunication.Pairing;
using PhoneLink.Domain.Devices;
using PhoneLink.Protocol.Pairing;
using PhoneLink.Security.Pairing;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class LocalPairingServerTests
{
    [Fact]
    public async Task AuthenticatedPairingPersistsTrustedDevice()
    {
        var secretStore = new InMemorySecretStore();
        var certificate = await new LocalTlsCertificateProvider(secretStore)
            .LoadOrCreateAsync(CancellationToken.None);
        var serverIdentity = Ed25519Identity.Create();
        var trustedDevices = new InMemoryTrustedDeviceStore();
        var serverDeviceId = Guid.NewGuid();
        await using var server = new LocalPairingServer(
            certificate,
            serverIdentity,
            trustedDevices,
            serverDeviceId);
        server.PairingApprovalRequested += (_, args) => args.Submit(true);
        await server.StartAsync(CancellationToken.None);

        var display = server.CreateOffer(DateTimeOffset.UtcNow);
        using var artifact = PairingOfferCodec.Decode(display.QrPayload);
        var endpoint = artifact.Offer.Endpoints.First(candidate => candidate.Host.Contains('.'));

        using var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = (_, presented, _, _) =>
            CertificateMatchesPin(presented, artifact.Offer.TlsSpkiSha256.Span);

        var uri = new UriBuilder("wss", "127.0.0.1", endpoint.Port, "/pair/v1").Uri;
        await socket.ConnectAsync(uri, CancellationToken.None);

        using var clientIdentity = Ed25519Identity.Create();
        var clientDeviceId = Guid.NewGuid();
        var clientPublicKey = clientIdentity.ExportPublicKey();
        var clientNonce = RandomNumberGenerator.GetBytes(32);
        var digest = PairingTranscript.CreateClientDigest(
            artifact.RawJson.Span,
            clientDeviceId.ToString("D"),
            clientPublicKey,
            clientNonce);
        var proof = PairingTranscript.CreateClientProof(artifact.Offer.PairingSecret.Span, digest);

        await SendAsync(
            socket,
            new PairingClientHello
            {
                Kind = "pairing.clientHello",
                SessionId = artifact.Offer.SessionId,
                ClientDeviceId = clientDeviceId,
                ClientDisplayName = "Test iPhone",
                ClientIdentityPublicKey = Base64Url.Encode(clientPublicKey),
                ClientNonce = Base64Url.Encode(clientNonce),
                Proof = Base64Url.Encode(proof),
                AppVersion = "1.0",
            });

        var serverProof = PairingWireSerializer.Parse<PairingServerProof>(await ReceiveAsync(socket));
        var serverNonce = Base64Url.Decode(serverProof.ServerNonce);
        var signature = Base64Url.Decode(serverProof.Signature);
        var serverInput = PairingTranscript.CreateServerSignatureInput(
            artifact.RawJson.Span,
            clientDeviceId.ToString("D"),
            clientPublicKey,
            clientNonce,
            serverNonce);
        Assert.True(
            Ed25519Identity.Verify(
                artifact.Offer.ServerIdentityKey.Span,
                serverInput,
                signature));

        await SendAsync(
            socket,
            new PairingConfirmation
            {
                Kind = "pairing.confirm",
                SessionId = artifact.Offer.SessionId,
                Approved = true,
            });

        var complete = PairingWireSerializer.Parse<PairingComplete>(await ReceiveAsync(socket));
        Assert.True(complete.Paired);
        Assert.Equal(serverDeviceId, complete.ServerDeviceId);
        await socket.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "Test complete",
            CancellationToken.None);
        var saved = await trustedDevices.FindAsync(new DeviceId(clientDeviceId), CancellationToken.None);
        Assert.NotNull(saved);
        Assert.Equal("Test iPhone", saved.DisplayName);
        Assert.Equal(clientPublicKey, saved.IdentityPublicKey.ToArray());
    }

    private static bool CertificateMatchesPin(X509Certificate? presented, ReadOnlySpan<byte> expected)
    {
        if (presented is null)
        {
            return false;
        }

        using var certificate = X509CertificateLoader.LoadCertificate(presented.GetRawCertData());
        var actual = LocalTlsCertificateProvider.GetSubjectPublicKeyInfoSha256(certificate);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static async Task SendAsync<T>(ClientWebSocket socket, T message)
    {
        var bytes = PairingWireSerializer.Serialize(message);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<byte[]> ReceiveAsync(ClientWebSocket socket)
    {
        var buffer = new byte[16_384];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        Assert.True(result.EndOfMessage);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        return buffer[..result.Count];
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly ConcurrentDictionary<SecretName, byte[]> _values = new();

        public Task StoreAsync(
            SecretName name,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values[name] = value.ToArray();
            return Task.CompletedTask;
        }

        public Task<SecretValue?> RetrieveAsync(SecretName name, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                _values.TryGetValue(name, out var value)
                    ? new SecretValue(value)
                    : null);
        }

        public Task DeleteAsync(SecretName name, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values.TryRemove(name, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryTrustedDeviceStore : ITrustedDeviceStore
    {
        private readonly ConcurrentDictionary<DeviceId, TrustedDevice> _devices = new();

        public Task<TrustedDevice?> FindAsync(DeviceId id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _devices.TryGetValue(id, out var value);
            return Task.FromResult(value);
        }

        public Task<IReadOnlyList<TrustedDevice>> ListAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<TrustedDevice>>(_devices.Values.ToArray());
        }

        public Task SaveAsync(TrustedDevice device, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _devices[device.Id] = device;
            return Task.CompletedTask;
        }

        public Task RevokeAsync(
            DeviceId id,
            DateTimeOffset revokedAt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_devices.TryGetValue(id, out var device))
            {
                _devices[id] = device.Revoke(revokedAt);
            }

            return Task.CompletedTask;
        }
    }
}

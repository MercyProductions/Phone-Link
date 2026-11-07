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
    [Theory]
    [InlineData("success")]
    [InlineData("expired")]
    [InlineData("invalid-proof")]
    public async Task PairingReportsOnlyObservedStagesAndRequiresBothConfirmations(string scenario)
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
        var requested = new TaskCompletionSource<PairingApprovalRequestedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<PairingAttemptProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<PairingAttemptStage> stages = new();
        server.PairingProgressChanged += (_, progress) =>
        {
            stages.Enqueue(progress.Stage);
            if (progress.Stage is not (PairingAttemptStage.RequestReceived or PairingAttemptStage.AwaitingConfirmation))
                finished.TrySetResult(progress);
        };
        server.PairingApprovalRequested += (_, args) =>
        {
            args.Submit(true);
            requested.TrySetResult(args);
        };
        await server.StartAsync(CancellationToken.None);

        var display = server.CreateOffer(scenario == "expired" ? DateTimeOffset.UtcNow.AddSeconds(-112) : DateTimeOffset.UtcNow);
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
        if (scenario == "invalid-proof") proof[0] ^= 1;

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

        if (scenario == "invalid-proof")
        {
            var failed = await finished.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(display.SessionId, failed.SessionId);
            Assert.Equal(PairingAttemptStage.Failed, failed.Stage);
            Assert.Equal([PairingAttemptStage.RequestReceived, PairingAttemptStage.Failed], stages.ToArray());
            Assert.False(requested.Task.IsCompleted);
            Assert.Null(await trustedDevices.FindAsync(new DeviceId(clientDeviceId), CancellationToken.None));
            return;
        }

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

        if (scenario == "expired")
        {
            var approval = await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                var terminal = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[64]), deadline.Token);
                Assert.Equal(WebSocketMessageType.Close, terminal.MessageType);
            }
            catch (WebSocketException)
            {
                // A cancelled server receive may abort the upgraded connection without a close frame.
            }
            Assert.False(deadline.IsCancellationRequested);
            Assert.True(approval.AttemptEnded.IsCancellationRequested);
            var expired = await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(display.SessionId, expired.SessionId);
            Assert.Equal([PairingAttemptStage.RequestReceived, PairingAttemptStage.AwaitingConfirmation, PairingAttemptStage.Expired], stages.ToArray());
            Assert.Null(await trustedDevices.FindAsync(new DeviceId(clientDeviceId), CancellationToken.None));
            return;
        }

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
        var trusted = await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(display.SessionId, trusted.SessionId);
        Assert.Equal([PairingAttemptStage.RequestReceived, PairingAttemptStage.AwaitingConfirmation, PairingAttemptStage.Trusted], stages.ToArray());
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

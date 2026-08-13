using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using PhoneLink.Application.Ports;
using PhoneLink.DeviceCommunication.Pairing;
using PhoneLink.Domain.Devices;
using PhoneLink.Protocol;
using PhoneLink.Protocol.Attachments;
using PhoneLink.Protocol.Commands;
using PhoneLink.Protocol.Messages;
using PhoneLink.Protocol.Pairing;
using PhoneLink.Protocol.Serialization;
using PhoneLink.Protocol.Session;
using PhoneLink.Security.Pairing;
using PhoneLink.Security.Session;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class AuthenticatedSessionTests
{
    [Fact]
    public async Task TrustedClientExchangesSessionBoundEnvelopes()
    {
        var secretStore = new InMemorySecretStore();
        var certificate = await new LocalTlsCertificateProvider(secretStore)
            .LoadOrCreateAsync(CancellationToken.None);
        var tlsPin = LocalTlsCertificateProvider.GetSubjectPublicKeyInfoSha256(certificate);
        using var clientIdentity = Ed25519Identity.Create();
        var clientDeviceId = new DeviceId(Guid.NewGuid());
        var trustedDevices = new InMemoryTrustedDeviceStore();
        await trustedDevices.SaveAsync(
            TrustedDevice.Create(
                clientDeviceId,
                "Test iPhone",
                DevicePlatform.Ios,
                clientIdentity.ExportPublicKey(),
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        var serverDeviceId = Guid.NewGuid();
        var serverIdentity = Ed25519Identity.Create();
        var serverPublicKey = serverIdentity.ExportPublicKey();
        await using var server = new LocalPairingServer(
            certificate,
            serverIdentity,
            trustedDevices,
            serverDeviceId);
        await server.StartAsync(CancellationToken.None);

        using var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = (_, presented, _, _) =>
            CertificateMatchesPin(presented, tlsPin);
        await socket.ConnectAsync(
            new Uri($"wss://127.0.0.1:{server.BoundPort}/sync/v1"),
            CancellationToken.None);

        var connectionId = Guid.NewGuid();
        var clientNonce = RandomNumberGenerator.GetBytes(32);
        const string appVersion = "1.0-test";
        await SendAuthenticationAsync(
            socket,
            new SessionClientHello
            {
                Kind = "session.clientHello",
                ConnectionId = connectionId,
                ClientDeviceId = clientDeviceId.Value,
                ClientNonce = Base64Url.Encode(clientNonce),
                ProtocolFloor = 1,
                ProtocolCeiling = 1,
                AppVersion = appVersion,
            });

        var challenge = SessionWireSerializer.Parse<SessionServerChallenge>(await ReceiveAsync(socket));
        Assert.Equal("session.serverChallenge", challenge.Kind);
        Assert.Equal(connectionId, challenge.ConnectionId);
        Assert.Equal(serverDeviceId, challenge.ServerDeviceId);
        var serverNonce = Base64Url.Decode(challenge.ServerNonce);
        var serverSignature = Base64Url.Decode(challenge.Signature);
        var serverProofInput = SessionTranscript.CreateServerProofInput(
            connectionId,
            challenge.SessionId,
            serverDeviceId,
            clientDeviceId.Value,
            clientNonce,
            serverNonce,
            challenge.SelectedProtocolVersion,
            appVersion);
        Assert.True(Ed25519Identity.Verify(serverPublicKey, serverProofInput, serverSignature));

        var clientProofInput = SessionTranscript.CreateClientProofInput(serverProofInput, serverSignature);
        var clientSignature = clientIdentity.Sign(clientProofInput);
        await SendAuthenticationAsync(
            socket,
            new SessionClientProof
            {
                Kind = "session.clientProof",
                ConnectionId = connectionId,
                SessionId = challenge.SessionId,
                Signature = Base64Url.Encode(clientSignature),
            });

        var ready = SessionWireSerializer.Parse<SessionReady>(await ReceiveAsync(socket));
        Assert.Equal(challenge.SessionId, ready.SessionId);
        Assert.Equal(ProtocolConstants.CurrentVersion, ready.ProtocolVersion);

        var ping = new ProtocolEnvelope
        {
            ProtocolVersion = ProtocolConstants.CurrentVersion,
            MessageId = Guid.NewGuid(),
            SessionId = challenge.SessionId,
            OriginDeviceId = clientDeviceId.Value,
            OriginSequence = 0,
            SentAt = DateTimeOffset.UtcNow,
            Kind = "ping",
            RequiresAck = false,
            Payload = JsonSerializer.SerializeToElement(new { sentAt = DateTimeOffset.UtcNow }),
        };
        await socket.SendAsync(
            ProtocolEnvelopeSerializer.Serialize(ping),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var received = server.ReadFramesAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await received.MoveNextAsync());
        var receivedEnvelope = Assert.IsType<PhoneLink.DeviceCommunication.Sessions.ReceivedDeviceEnvelope>(
            received.Current);
        Assert.Equal(clientDeviceId, receivedEnvelope.DeviceId);
        Assert.Equal(ping.MessageId, receivedEnvelope.Envelope.MessageId);

        var attachmentId = Guid.NewGuid();
        var binaryFrame = AttachmentBinaryFrameCodec.Encode(
            attachmentId,
            3,
            "encrypted-photo-chunk"u8);
        await socket.SendAsync(
            binaryFrame,
            WebSocketMessageType.Binary,
            true,
            CancellationToken.None);
        Assert.True(await received.MoveNextAsync());
        var receivedChunk = Assert.IsType<PhoneLink.DeviceCommunication.Sessions.ReceivedAttachmentChunk>(
            received.Current);
        Assert.Equal(attachmentId, receivedChunk.Chunk.AttachmentId);
        Assert.Equal((uint)3, receivedChunk.Chunk.ChunkIndex);

        Assert.True(server.TryGetSessionId(clientDeviceId, out var activeSessionId));
        Assert.Equal(challenge.SessionId, activeSessionId);
        var commandId = Guid.NewGuid();
        var command = new ProtocolEnvelope
        {
            ProtocolVersion = ProtocolConstants.CurrentVersion,
            MessageId = Guid.NewGuid(),
            SessionId = challenge.SessionId,
            OriginDeviceId = serverDeviceId,
            OriginSequence = 0,
            SentAt = DateTimeOffset.UtcNow,
            Kind = "command.request",
            RequiresAck = true,
            Payload = JsonSerializer.SerializeToElement(new CommandRequestPayload
            {
                CommandId = commandId,
                Name = "call.start",
                Arguments = new Dictionary<string, string> { ["phoneNumber"] = "+13125550198" },
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2),
                RequiresUserConfirmation = true,
            }),
        };
        await server.SendAsync(clientDeviceId, command, CancellationToken.None);
        var receivedCommand = ProtocolEnvelopeSerializer.Parse(await ReceiveAsync(socket));
        Assert.Equal(command.MessageId, receivedCommand.MessageId);

        var commandResult = new ProtocolEnvelope
        {
            ProtocolVersion = ProtocolConstants.CurrentVersion,
            MessageId = Guid.NewGuid(),
            CorrelationId = command.MessageId,
            SessionId = challenge.SessionId,
            OriginDeviceId = clientDeviceId.Value,
            OriginSequence = 0,
            SentAt = DateTimeOffset.UtcNow,
            Kind = "command.result",
            RequiresAck = false,
            Payload = JsonSerializer.SerializeToElement(new CommandResultPayload
            {
                CommandId = commandId,
                Status = "declined",
                ErrorCode = "user.declined",
                FinishedAt = DateTimeOffset.UtcNow,
            }),
        };
        await socket.SendAsync(
            ProtocolEnvelopeSerializer.Serialize(commandResult),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);
        Assert.True(await received.MoveNextAsync());
        var receivedResult = Assert.IsType<PhoneLink.DeviceCommunication.Sessions.ReceivedDeviceEnvelope>(
            received.Current);
        Assert.Equal(command.MessageId, receivedResult.Envelope.CorrelationId);

        var pong = new ProtocolEnvelope
        {
            ProtocolVersion = ProtocolConstants.CurrentVersion,
            MessageId = Guid.NewGuid(),
            CorrelationId = ping.MessageId,
            SessionId = challenge.SessionId,
            OriginDeviceId = serverDeviceId,
            OriginSequence = 0,
            SentAt = DateTimeOffset.UtcNow,
            Kind = "pong",
            RequiresAck = false,
            Payload = JsonSerializer.SerializeToElement(new { receivedAt = DateTimeOffset.UtcNow }),
        };
        await server.SendAsync(clientDeviceId, pong, CancellationToken.None);
        var returned = ProtocolEnvelopeSerializer.Parse(await ReceiveAsync(socket));
        Assert.Equal(pong.MessageId, returned.MessageId);
        Assert.Equal(ping.MessageId, returned.CorrelationId);

        await socket.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "Test complete",
            CancellationToken.None);
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

    private static async Task SendAuthenticationAsync<T>(ClientWebSocket socket, T message)
    {
        var bytes = SessionWireSerializer.Serialize(message);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<byte[]> ReceiveAsync(ClientWebSocket socket)
    {
        var output = new byte[ProtocolConstants.MaximumJsonFrameBytes];
        var result = await socket.ReceiveAsync(output, CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        Assert.True(result.EndOfMessage);
        return output[..result.Count];
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly ConcurrentDictionary<SecretName, byte[]> _values = new();

        public Task StoreAsync(SecretName name, ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values[name] = value.ToArray();
            return Task.CompletedTask;
        }

        public Task<SecretValue?> RetrieveAsync(SecretName name, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                _values.TryGetValue(name, out var value) ? new SecretValue(value) : null);
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
            _devices.TryGetValue(id, out var device);
            return Task.FromResult(device);
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

        public Task RevokeAsync(DeviceId id, DateTimeOffset revokedAt, CancellationToken cancellationToken)
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

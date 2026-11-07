using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text;
using PhoneLink.Application.Ports;
using PhoneLink.Application.Features.Diagnostics;
using PhoneLink.DeviceCommunication.Diagnostics;
using PhoneLink.DeviceCommunication.Sessions;
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
    [Theory]
    [InlineData("replay")]
    [InlineData("revoke")]
    [InlineData("forged-origin")]
    [InlineData("wrong-session")]
    [InlineData("duplicate-json")]
    public async Task TrustedClientExchangesSessionBoundEnvelopes(string termination)
    {
        var revokeInsteadOfReplay = termination == "revoke";
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
        Assert.Contains("diagnostics.health.v1", ready.Capabilities);
        Assert.Contains("videos.import.v1", ready.Capabilities);

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

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
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
        // A stale selection must fail closed rather than falling back to a live peer.
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.SendAsync(
            DeviceId.New(), command, timeout.Token).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.SendAsync(
            clientDeviceId, command with { SessionId = Guid.NewGuid() }, timeout.Token).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.SendAsync(
            clientDeviceId, command with { OriginDeviceId = Guid.NewGuid() }, timeout.Token).AsTask());
        await server.SendAsync(clientDeviceId, command, timeout.Token);
        var receivedCommand = ProtocolEnvelopeSerializer.Parse(await ReceiveAsync(socket));
        Assert.Equal(command.MessageId, receivedCommand.MessageId);
        Assert.Equal(challenge.SessionId, receivedCommand.SessionId);
        var dial = receivedCommand.Payload.Deserialize<CommandRequestPayload>();
        Assert.NotNull(dial);
        Assert.True(dial.RequiresUserConfirmation);
        Assert.Equal("+13125550198", dial.Arguments["phoneNumber"]);

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

        var healthInbox = new CompanionHealthInbox();
        var healthHandler = new CompanionHealthEnvelopeHandler(healthInbox);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.SessionDisconnected += (_, args) =>
        {
            healthInbox.Remove(args.DeviceId.Value);
            disconnected.TrySetResult();
        };
        var healthRequest = ping with
        {
            MessageId = Guid.NewGuid(), Kind = "diagnostics.health.snapshot", RequiresAck = true,
            Payload = JsonSerializer.SerializeToElement(new
            {
                schemaVersion = 1, capturedAt = DateTimeOffset.UtcNow, batteryPercent = (int?)42,
                batteryState = "charging", thermalState = "nominal", lowPowerMode = false,
                contactsPermission = "limited", photosPermission = "limited", cameraPermission = "not-determined",
                notificationPermission = "provisional",
            }),
        };
        ReceivedDeviceEnvelope? validQueuedHealth = null;
        for (var testCase = 0; testCase < 5; testCase++)
        {
            var request = healthRequest with { MessageId = Guid.NewGuid() };
            if (testCase == 1) request = request with { Payload = JsonSerializer.SerializeToElement(new { schemaVersion = 99 }) };
            if (testCase == 2) request = request with { RequiresAck = false };
            if (testCase == 3) request = request with { CorrelationId = Guid.NewGuid() };
            await socket.SendAsync(ProtocolEnvelopeSerializer.Serialize(request), WebSocketMessageType.Text, true, timeout.Token);
            Assert.True(await received.MoveNextAsync());
            var frame = Assert.IsType<ReceivedDeviceEnvelope>(received.Current);
            if (testCase == 0) { validQueuedHealth = frame; healthRequest = request; }
            var receipt = healthHandler.Handle(frame, serverDeviceId,
                (id, session) => server.TryGetSessionId(id, out var active) && active == session,
                privacyLocked: testCase == 4, DateTimeOffset.UtcNow);
            Assert.NotNull(receipt);
            await server.SendAsync(clientDeviceId, receipt, timeout.Token);
            var delivered = ProtocolEnvelopeSerializer.Parse(await ReceiveAsync(socket));
            Assert.Equal("diagnostics.health.receipt", delivered.Kind);
            Assert.Equal(request.MessageId, delivered.CorrelationId);
            Assert.Equal(challenge.SessionId, delivered.SessionId);
            Assert.Equal(serverDeviceId, delivered.OriginDeviceId);
            Assert.False(delivered.RequiresAck);
            Assert.Equal(testCase == 0, delivered.Payload.GetProperty("accepted").GetBoolean());
        }
        Assert.Equal(42, healthInbox.Get(clientDeviceId.Value, challenge.SessionId, DateTimeOffset.UtcNow)!.Snapshot.BatteryPercent);

        if (revokeInsteadOfReplay)
        {
            await trustedDevices.RevokeAsync(clientDeviceId, DateTimeOffset.UtcNow, timeout.Token);
            await server.DisconnectAsync(clientDeviceId, timeout.Token);
        }
        else
        {
            // These frames must be rejected by transport validation before reaching the feature handler.
            var rejected = termination switch
            {
                "forged-origin" => healthRequest with { MessageId = Guid.NewGuid(), OriginDeviceId = Guid.NewGuid() },
                "wrong-session" => healthRequest with { MessageId = Guid.NewGuid(), SessionId = Guid.NewGuid() },
                "duplicate-json" => healthRequest with { MessageId = Guid.NewGuid() },
                _ => healthRequest,
            };
            var rejectedBytes = ProtocolEnvelopeSerializer.Serialize(rejected);
            if (termination == "duplicate-json")
            {
                var json = Encoding.UTF8.GetString(rejectedBytes);
                var ambiguous = json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal);
                Assert.NotEqual(json, ambiguous);
                rejectedBytes = Encoding.UTF8.GetBytes(ambiguous);
            }
            await socket.SendAsync(rejectedBytes, WebSocketMessageType.Text, true, timeout.Token);
        }
        var close = await socket.ReceiveAsync(new byte[1], timeout.Token);
        Assert.Equal(WebSocketMessageType.Close, close.MessageType);
        Assert.Equal(revokeInsteadOfReplay ? WebSocketCloseStatus.NormalClosure : WebSocketCloseStatus.PolicyViolation, close.CloseStatus);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Test complete", timeout.Token);
        await disconnected.Task.WaitAsync(timeout.Token);
        Assert.False(server.IsConnected(clientDeviceId));
        Assert.Null(healthInbox.Get(clientDeviceId.Value, challenge.SessionId, DateTimeOffset.UtcNow));
        Assert.Null(healthHandler.Handle(validQueuedHealth!, serverDeviceId,
            (id, session) => server.TryGetSessionId(id, out var active) && active == session, false, DateTimeOffset.UtcNow));
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
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var output = new byte[ProtocolConstants.MaximumJsonFrameBytes];
        var result = await socket.ReceiveAsync(output, deadline.Token);
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

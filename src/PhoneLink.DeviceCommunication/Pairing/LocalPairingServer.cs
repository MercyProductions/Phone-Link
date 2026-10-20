using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using PhoneLink.Application.Ports;
using PhoneLink.DeviceCommunication.Discovery;
using PhoneLink.DeviceCommunication.Sessions;
using PhoneLink.Domain.Devices;
using PhoneLink.Protocol;
using PhoneLink.Protocol.Attachments;
using PhoneLink.Protocol.Messages;
using PhoneLink.Protocol.Pairing;
using PhoneLink.Protocol.Serialization;
using PhoneLink.Protocol.Session;
using PhoneLink.Security.Pairing;
using PhoneLink.Security.Session;

namespace PhoneLink.DeviceCommunication.Pairing;

public sealed class LocalPairingServer : IAsyncDisposable
{
    private const int MaximumPairingMessageBytes = 16_384;
    private readonly X509Certificate2 _certificate;
    private readonly Ed25519Identity _serverIdentity;
    private readonly ITrustedDeviceStore _deviceStore;
    private readonly Guid _serverDeviceId;
    private readonly SingleUsePairingOfferRegistry _offers = new();
    private readonly ConcurrentDictionary<DeviceId, LiveDeviceSession> _sessions = new();
    private readonly Channel<ReceivedDeviceFrame> _receivedFrames =
        Channel.CreateBounded<ReceivedDeviceFrame>(
            new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false,
            });
    private WebApplication? _application;
    private int _port;
    private bool _disposed;

    public LocalPairingServer(
        X509Certificate2 certificate,
        Ed25519Identity serverIdentity,
        ITrustedDeviceStore deviceStore,
        Guid serverDeviceId)
    {
        _certificate = certificate ?? throw new ArgumentNullException(nameof(certificate));
        _serverIdentity = serverIdentity ?? throw new ArgumentNullException(nameof(serverIdentity));
        _deviceStore = deviceStore ?? throw new ArgumentNullException(nameof(deviceStore));
        if (serverDeviceId == Guid.Empty)
        {
            throw new ArgumentException("The Windows device ID cannot be empty.", nameof(serverDeviceId));
        }

        _serverDeviceId = serverDeviceId;
    }

    public event EventHandler<PairingApprovalRequestedEventArgs>? PairingApprovalRequested;

    public event EventHandler<PairingCompletedEventArgs>? PairingCompleted;

    public event EventHandler<DeviceSessionChangedEventArgs>? SessionConnected;

    public event EventHandler<DeviceSessionChangedEventArgs>? SessionDisconnected;

    public int BoundPort => _port;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_application is not null)
        {
            return;
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options =>
            options.ListenAnyIP(
                0,
                listenOptions => listenOptions.UseHttps(httpsOptions =>
                {
                    httpsOptions.ServerCertificate = _certificate;
                    httpsOptions.SslProtocols = SslProtocols.Tls13;
                })));

        var application = builder.Build();
        application.UseWebSockets(
            new WebSocketOptions
            {
                KeepAliveInterval = TimeSpan.FromSeconds(20),
            });
        application.Map("/pair/v1", HandlePairingRequestAsync);
        application.Map("/sync/v1", HandleSessionRequestAsync);

        try
        {
            await application.StartAsync(cancellationToken).ConfigureAwait(false);
            var server = application.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses
                ?? throw new InvalidOperationException("The local pairing server did not publish a listening address.");
            var boundAddress = addresses.Select(address => new Uri(address)).First();
            _port = boundAddress.Port;
            _application = application;
        }
        catch
        {
            await application.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public PairingOfferDisplay CreateOffer(DateTimeOffset now)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_application is null || _port == 0)
        {
            throw new InvalidOperationException("The pairing server must be started before creating an offer.");
        }

        _offers.RemoveExpired(now);
        var endpoints = GetPrivateNetworkAddresses()
            .Select(address => new PairingEndpoint(address.ToString(), _port))
            .ToArray();
        if (endpoints.Length == 0)
        {
            throw new InvalidOperationException("No active private IPv4 or IPv6 address is available for local pairing.");
        }

        var tlsPin = LocalTlsCertificateProvider.GetSubjectPublicKeyInfoSha256(_certificate);
        try
        {
            var artifact = PairingOfferFactory.Create(endpoints, tlsPin, _serverIdentity, now);
            var display = new PairingOfferDisplay(
                artifact.Offer.SessionId,
                artifact.Offer.ExpiresAt,
                artifact.QrPayload);
            _offers.Register(artifact);
            return display;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tlsPin);
        }
    }

    public bool CancelOffer(Guid sessionId) => _offers.Cancel(sessionId);

    public bool IsConnected(DeviceId deviceId) => _sessions.ContainsKey(deviceId);

    public bool TryGetSessionId(DeviceId deviceId, out Guid sessionId)
    {
        if (_sessions.TryGetValue(deviceId, out var session))
        {
            sessionId = session.SessionId;
            return true;
        }

        sessionId = Guid.Empty;
        return false;
    }

    public IAsyncEnumerable<ReceivedDeviceFrame> ReadFramesAsync(
        CancellationToken cancellationToken) =>
        _receivedFrames.Reader.ReadAllAsync(cancellationToken);

    public async ValueTask SendAsync(
        DeviceId deviceId,
        ProtocolEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!_sessions.TryGetValue(deviceId, out var session))
        {
            throw new InvalidOperationException("The trusted device does not have an active session.");
        }

        if (envelope.SessionId != session.SessionId || envelope.OriginDeviceId != _serverDeviceId)
        {
            throw new InvalidOperationException("The outgoing envelope is not bound to this authenticated session.");
        }

        await session.SendAsync(envelope, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisconnectAsync(
        DeviceId deviceId,
        CancellationToken cancellationToken)
    {
        if (!_sessions.TryRemove(deviceId, out var session))
        {
            return;
        }

        await session.CloseAsync(cancellationToken).ConfigureAwait(false);
        SessionDisconnected?.Invoke(
            this,
            new DeviceSessionChangedEventArgs(session.DeviceId, session.SessionId));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _offers.Dispose();
        _receivedFrames.Writer.TryComplete();
        foreach (var session in _sessions.Values)
        {
            await session.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        _sessions.Clear();
        if (_application is not null)
        {
            await _application.StopAsync().ConfigureAwait(false);
            await _application.DisposeAsync().ConfigureAwait(false);
            _application = null;
        }

        _certificate.Dispose();
        _serverIdentity.Dispose();
    }

    private async Task HandleSessionRequestAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        LiveDeviceSession? liveSession = null;
        try
        {
            var helloBytes = await ReceiveRequiredTextMessageAsync(
                socket,
                MaximumPairingMessageBytes,
                context.RequestAborted).ConfigureAwait(false);
            var hello = SessionWireSerializer.Parse<SessionClientHello>(helloBytes);
            ValidateSessionHello(hello);

            var deviceId = new DeviceId(hello.ClientDeviceId);
            var trustedDevice = await _deviceStore.FindAsync(deviceId, context.RequestAborted).ConfigureAwait(false);
            if (trustedDevice is null || trustedDevice.TrustState != DeviceTrustState.Trusted)
            {
                throw new CryptographicException("The client is not a trusted device.");
            }

            var clientNonce = Base64Url.Decode(hello.ClientNonce);
            RequireLength(clientNonce, 32, nameof(hello.ClientNonce));
            var serverNonce = RandomNumberGenerator.GetBytes(32);
            var sessionId = Guid.NewGuid();
            var selectedVersion = ProtocolConstants.CurrentVersion;
            var serverProofInput = SessionTranscript.CreateServerProofInput(
                hello.ConnectionId,
                sessionId,
                _serverDeviceId,
                hello.ClientDeviceId,
                clientNonce,
                serverNonce,
                selectedVersion,
                hello.AppVersion);
            var serverSignature = _serverIdentity.Sign(serverProofInput);
            var clientProofInput = SessionTranscript.CreateClientProofInput(serverProofInput, serverSignature);

            try
            {
                await SendAuthenticationAsync(
                    socket,
                    new SessionServerChallenge
                    {
                        Kind = "session.serverChallenge",
                        ConnectionId = hello.ConnectionId,
                        SessionId = sessionId,
                        ServerDeviceId = _serverDeviceId,
                        ServerNonce = Base64Url.Encode(serverNonce),
                        SelectedProtocolVersion = selectedVersion,
                        ServerTime = DateTimeOffset.UtcNow,
                        Signature = Base64Url.Encode(serverSignature),
                    },
                    context.RequestAborted).ConfigureAwait(false);

                var proofBytes = await ReceiveRequiredTextMessageAsync(
                    socket,
                    MaximumPairingMessageBytes,
                    context.RequestAborted).ConfigureAwait(false);
                var proof = SessionWireSerializer.Parse<SessionClientProof>(proofBytes);
                var clientSignature = Base64Url.Decode(proof.Signature);
                RequireLength(clientSignature, 64, nameof(proof.Signature));
                try
                {
                    if (proof.Kind != "session.clientProof" ||
                        proof.ConnectionId != hello.ConnectionId ||
                        proof.SessionId != sessionId ||
                        !Ed25519Identity.Verify(
                            trustedDevice.IdentityPublicKey.Span,
                            clientProofInput,
                            clientSignature))
                    {
                        throw new CryptographicException("The client session proof is invalid.");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(clientSignature);
                }

                await SendAuthenticationAsync(
                    socket,
                    new SessionReady
                    {
                        Kind = "session.ready",
                        SessionId = sessionId,
                        ServerDeviceId = _serverDeviceId,
                        ProtocolVersion = selectedVersion,
                        Capabilities =
                        [
                            "contacts.sync",
                            "contacts.sync.v2",
                            "photos.import",
                            "command.call.start.confirmed",
                        ],
                        MaximumBatchEvents = 200,
                        MaximumAttachmentBytes = 25 * 1024 * 1024,
                    },
                    context.RequestAborted).ConfigureAwait(false);

                liveSession = new LiveDeviceSession(deviceId, sessionId, socket);
                if (_sessions.TryGetValue(deviceId, out var previous))
                {
                    await previous.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                }
                _sessions[deviceId] = liveSession;
                SessionConnected?.Invoke(this, new DeviceSessionChangedEventArgs(deviceId, sessionId));
                await ReceiveAuthenticatedEnvelopesAsync(liveSession, context.RequestAborted).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clientNonce);
                CryptographicOperations.ZeroMemory(serverNonce);
                CryptographicOperations.ZeroMemory(serverProofInput);
                CryptographicOperations.ZeroMemory(serverSignature);
                CryptographicOperations.ZeroMemory(clientProofInput);
            }
        }
        catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    "Session authentication failed",
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            if (liveSession is not null &&
                _sessions.TryGetValue(liveSession.DeviceId, out var current) &&
                ReferenceEquals(current, liveSession))
            {
                _sessions.TryRemove(liveSession.DeviceId, out _);
                SessionDisconnected?.Invoke(
                    this,
                    new DeviceSessionChangedEventArgs(liveSession.DeviceId, liveSession.SessionId));
            }
            liveSession?.Dispose();
        }
    }

    private async Task HandlePairingRequestAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        try
        {
            var helloBytes = await ReceiveTextMessageAsync(socket, context.RequestAborted).ConfigureAwait(false);
            var hello = PairingWireSerializer.Parse<PairingClientHello>(helloBytes);
            ValidateClientHello(hello);

            using var artifact = _offers.Consume(hello.SessionId, DateTimeOffset.UtcNow)
                ?? throw new InvalidOperationException("The pairing offer is unknown, expired, or already used.");
            PairingOfferValidator.ValidateForClient(artifact.Offer, DateTimeOffset.UtcNow);

            var clientIdentity = Base64Url.Decode(hello.ClientIdentityPublicKey);
            var clientNonce = Base64Url.Decode(hello.ClientNonce);
            var clientProof = Base64Url.Decode(hello.Proof);
            RequireLength(clientIdentity, 32, nameof(hello.ClientIdentityPublicKey));
            RequireLength(clientNonce, 32, nameof(hello.ClientNonce));
            RequireLength(clientProof, 32, nameof(hello.Proof));

            var clientDigest = PairingTranscript.CreateClientDigest(
                artifact.RawJson.Span,
                hello.ClientDeviceId.ToString("D"),
                clientIdentity,
                clientNonce);
            if (!PairingTranscript.VerifyClientProof(
                    artifact.Offer.PairingSecret.Span,
                    clientDigest,
                    clientProof))
            {
                throw new CryptographicException("The client pairing proof is invalid.");
            }

            var serverNonce = RandomNumberGenerator.GetBytes(32);
            var signatureInput = PairingTranscript.CreateServerSignatureInput(
                artifact.RawJson.Span,
                hello.ClientDeviceId.ToString("D"),
                clientIdentity,
                clientNonce,
                serverNonce);
            var signature = _serverIdentity.Sign(signatureInput);
            var comparisonDigest = PairingTranscript.CreateComparisonDigest(signatureInput, signature);
            var comparisonPhrase = ComparisonPhrase.Create(comparisonDigest);

            try
            {
                await SendAsync(
                    socket,
                    new PairingServerProof
                    {
                        Kind = "pairing.serverProof",
                        SessionId = hello.SessionId,
                        ServerNonce = Base64Url.Encode(serverNonce),
                        Signature = Base64Url.Encode(signature),
                    },
                    context.RequestAborted).ConfigureAwait(false);

                var approval = new PairingApprovalRequestedEventArgs(
                    hello.SessionId,
                    hello.ClientDeviceId,
                    hello.ClientDisplayName,
                    comparisonPhrase,
                    artifact.Offer.ExpiresAt);
                PairingApprovalRequested?.Invoke(this, approval);

                var confirmationBytes = await ReceiveTextMessageAsync(socket, context.RequestAborted).ConfigureAwait(false);
                var phoneConfirmation = PairingWireSerializer.Parse<PairingConfirmation>(confirmationBytes);
                if (phoneConfirmation.Kind != "pairing.confirm" ||
                    phoneConfirmation.SessionId != hello.SessionId)
                {
                    throw new InvalidDataException("The phone returned an invalid pairing confirmation.");
                }

                var windowsApproved = PairingApprovalRequested is not null &&
                    await approval.WaitAsync(context.RequestAborted).ConfigureAwait(false);
                var paired = phoneConfirmation.Approved && windowsApproved;
                if (paired)
                {
                    var device = TrustedDevice.Create(
                        new DeviceId(hello.ClientDeviceId),
                        hello.ClientDisplayName,
                        DevicePlatform.Ios,
                        clientIdentity,
                        DateTimeOffset.UtcNow);
                    await _deviceStore.SaveAsync(device, context.RequestAborted).ConfigureAwait(false);
                    PairingCompleted?.Invoke(
                        this,
                        new PairingCompletedEventArgs(hello.ClientDeviceId, hello.ClientDisplayName));
                }

                await SendAsync(
                    socket,
                    new PairingComplete
                    {
                        Kind = "pairing.complete",
                        SessionId = hello.SessionId,
                        Paired = paired,
                        ServerDeviceId = _serverDeviceId,
                        ErrorCode = paired ? null : "pairing.user-rejected",
                    },
                    context.RequestAborted).ConfigureAwait(false);
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    paired ? "Pairing complete" : "Pairing rejected",
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clientIdentity);
                CryptographicOperations.ZeroMemory(clientNonce);
                CryptographicOperations.ZeroMemory(clientProof);
                CryptographicOperations.ZeroMemory(clientDigest);
                CryptographicOperations.ZeroMemory(serverNonce);
                CryptographicOperations.ZeroMemory(signatureInput);
                CryptographicOperations.ZeroMemory(signature);
                CryptographicOperations.ZeroMemory(comparisonDigest);
            }
        }
        catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    "Pairing failed",
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static void ValidateClientHello(PairingClientHello hello)
    {
        if (hello.Kind != "pairing.clientHello" ||
            hello.SessionId == Guid.Empty ||
            hello.ClientDeviceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(hello.ClientDisplayName) ||
            hello.ClientDisplayName.Length > TrustedDevice.MaximumDisplayNameLength ||
            string.IsNullOrWhiteSpace(hello.AppVersion) ||
            hello.AppVersion.Length > 50)
        {
            throw new InvalidDataException("The client pairing hello has invalid metadata.");
        }
    }

    private static void ValidateSessionHello(SessionClientHello hello)
    {
        if (hello.Kind != "session.clientHello" ||
            hello.ConnectionId == Guid.Empty ||
            hello.ClientDeviceId == Guid.Empty ||
            hello.ProtocolFloor < 1 ||
            hello.ProtocolCeiling < hello.ProtocolFloor ||
            ProtocolConstants.CurrentVersion < hello.ProtocolFloor ||
            ProtocolConstants.CurrentVersion > hello.ProtocolCeiling ||
            string.IsNullOrWhiteSpace(hello.AppVersion) ||
            hello.AppVersion.Length > 50)
        {
            throw new InvalidDataException("The client session hello has invalid metadata.");
        }
    }

    private async Task ReceiveAuthenticatedEnvelopesAsync(
        LiveDeviceSession session,
        CancellationToken cancellationToken)
    {
        var seenMessageIds = new HashSet<Guid>();
        long highestPositiveSequence = 0;
        while (true)
        {
            var frame = await ReceiveSocketFrameOrCloseAsync(
                session.Socket,
                Math.Max(
                    ProtocolConstants.MaximumJsonFrameBytes,
                    AttachmentBinaryFrameCodec.HeaderLength + AttachmentBinaryFrameCodec.MaximumChunkBytes),
                cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                if (session.Socket.State == WebSocketState.CloseReceived)
                {
                    await session.Socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Session closed",
                        CancellationToken.None).ConfigureAwait(false);
                }
                return;
            }

            if (frame.MessageType == WebSocketMessageType.Binary)
            {
                var chunk = AttachmentBinaryFrameCodec.Parse(frame.Payload);
                await _receivedFrames.Writer.WriteAsync(
                    new ReceivedAttachmentChunk(session.DeviceId, session.SessionId, chunk),
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            var envelope = ProtocolEnvelopeSerializer.Parse(frame.Payload);
            if (envelope.SessionId != session.SessionId ||
                envelope.OriginDeviceId != session.DeviceId.Value ||
                !seenMessageIds.Add(envelope.MessageId))
            {
                throw new InvalidDataException("The envelope is not bound to this authenticated session.");
            }

            if (envelope.OriginSequence > 0)
            {
                if (envelope.OriginSequence <= highestPositiveSequence)
                {
                    throw new InvalidDataException("Durable envelope sequences must increase within a session.");
                }
                highestPositiveSequence = envelope.OriginSequence;
            }

            await _receivedFrames.Writer.WriteAsync(
                new ReceivedDeviceEnvelope(session.DeviceId, envelope),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<byte[]> ReceiveTextMessageAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
        => await ReceiveRequiredTextMessageAsync(
            socket,
            MaximumPairingMessageBytes,
            cancellationToken).ConfigureAwait(false);

    private static async Task<byte[]> ReceiveRequiredTextMessageAsync(
        WebSocket socket,
        int maximumBytes,
        CancellationToken cancellationToken)
        => await ReceiveTextMessageOrCloseAsync(socket, maximumBytes, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The peer closed before sending the required message.");

    private static async Task<byte[]?> ReceiveTextMessageOrCloseAsync(
        WebSocket socket,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var frame = await ReceiveSocketFrameOrCloseAsync(
            socket,
            maximumBytes,
            cancellationToken).ConfigureAwait(false);
        if (frame is null)
        {
            return null;
        }
        if (frame.MessageType != WebSocketMessageType.Text)
        {
            throw new InvalidDataException("This endpoint accepts only text messages.");
        }
        return frame.Payload;
    }

    private static async Task<SocketFrame?> ReceiveSocketFrameOrCloseAsync(
        WebSocket socket,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var output = new ArrayBufferWriter<byte>();
        WebSocketMessageType? messageType = null;
        while (true)
        {
            var memory = output.GetMemory(4_096);
            var result = await socket.ReceiveAsync(memory, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            if (messageType is not null && messageType != result.MessageType)
            {
                throw new InvalidDataException("A fragmented WebSocket message changed frame type.");
            }
            messageType = result.MessageType;

            output.Advance(result.Count);
            if (output.WrittenCount > maximumBytes)
            {
                throw new InvalidDataException("The message is larger than allowed.");
            }

            if (result.EndOfMessage)
            {
                return new SocketFrame(result.MessageType, output.WrittenSpan.ToArray());
            }
        }
    }

    private static async Task SendAuthenticationAsync<T>(
        WebSocket socket,
        T message,
        CancellationToken cancellationToken)
    {
        var bytes = SessionWireSerializer.Serialize(message);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SendAsync<T>(
        WebSocket socket,
        T message,
        CancellationToken cancellationToken)
    {
        var bytes = PairingWireSerializer.Serialize(message);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    private static IPAddress[] GetPrivateNetworkAddresses() =>
        LocalNetworkCatalog.SelectPairingAddresses(LocalNetworkCatalog.Capture());

    private static void RequireLength(byte[] value, int expected, string parameterName)
    {
        if (value.Length != expected)
        {
            throw new InvalidDataException($"Pairing field '{parameterName}' has an invalid length.");
        }
    }

    private sealed class LiveDeviceSession : IDisposable
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public LiveDeviceSession(DeviceId deviceId, Guid sessionId, WebSocket socket)
        {
            DeviceId = deviceId;
            SessionId = sessionId;
            Socket = socket;
        }

        public DeviceId DeviceId { get; }

        public Guid SessionId { get; }

        public WebSocket Socket { get; }

        public async ValueTask SendAsync(
            ProtocolEnvelope envelope,
            CancellationToken cancellationToken)
        {
            var bytes = ProtocolEnvelopeSerializer.Serialize(envelope);
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Socket.SendAsync(
                    bytes,
                    WebSocketMessageType.Text,
                    true,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async Task CloseAsync(CancellationToken cancellationToken)
        {
            if (Socket.State == WebSocketState.Open)
            {
                await Socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Server stopping",
                    cancellationToken).ConfigureAwait(false);
            }
        }

        public void Dispose() => _sendLock.Dispose();
    }

    private sealed record SocketFrame(WebSocketMessageType MessageType, byte[] Payload);
}

using PhoneLink.DeviceCommunication.Pairing;
using PhoneLink.DeviceCommunication.Discovery;
using PhoneLink.DeviceCommunication.Synchronization;
using PhoneLink.DeviceCommunication.Attachments;
using PhoneLink.DeviceCommunication.Sessions;
using PhoneLink.Application.Features.Attachments;
using PhoneLink.Application.Features.Calling;
using PhoneLink.Application.Features.Contacts;
using PhoneLink.Protocol.Commands;
using PhoneLink.Protocol;
using PhoneLink.Protocol.Messages;
using PhoneLink.Persistence.Sqlite;
using PhoneLink.Persistence.Sqlite.Configuration;
using PhoneLink.Security.Pairing;
using PhoneLink.Security.Storage;
using PhoneLink.Domain.Devices;
using PhoneLink.Integrations.AppleBluetooth.Capabilities;
using PhoneLink.Integrations.AppleBluetooth.Ancs;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Net.NetworkInformation;

namespace PhoneLink.Windows;

public sealed class AppServices : IAsyncDisposable
{
    private static readonly Lazy<AppServices> LazyCurrent = new(() => new AppServices());
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly SemaphoreSlim _discoveryLock = new(1, 1);
    private readonly SemaphoreSlim _ancsLock = new(1, 1);
    private readonly object _discoveryTaskGate = new();
    private readonly ConcurrentDictionary<Guid, PendingCommand> _pendingCommands = new();
    private SqliteDatabase? _database;
    private WindowsCredentialSecretStore? _secretStore;
    private LocalPairingServer? _pairingServer;
    private SqliteTrustedDeviceStore? _deviceStore;
    private SqliteContactStore? _contactStore;
    private SqliteAttachmentStore? _attachmentStore;
    private InboundAttachmentManager? _attachmentManager;
    private CancellationTokenSource? _backgroundCancellation;
    private Task? _syncProcessorTask;
    private WindowsDnsSdAdvertiser? _dnsSdAdvertiser;
    private Task? _discoveryStartTask;
    private bool _discoveryRunnerActive;
    private bool _discoveryUpdateRequested;
    private bool _discoveryRestartRequested;
    private AncsNotificationSession? _ancsSession;
    private Guid _windowsDeviceId;
    private volatile bool _disposed;

    private AppServices()
    {
    }

    public static AppServices Current => LazyCurrent.Value;

    public async Task<LocalPairingServer> GetPairingServerAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pairingServer is not null)
        {
            return _pairingServer;
        }

        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pairingServer is not null)
            {
                return _pairingServer;
            }

            var protectedSecrets = new WindowsCredentialSecretStore();
            _secretStore = protectedSecrets;
            var localFolder = LocalAppStorage.DirectoryPath;
            var storageOptions = new SqliteStorageOptions(
                Path.Combine(localFolder, "Data", "phone-link.db"),
                Path.Combine(localFolder, "Data", "Attachments"));
            _database = new SqliteDatabase(storageOptions, protectedSecrets);
            await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var identity = await new WindowsIdentityKeyProvider(protectedSecrets)
                .LoadOrCreateAsync(cancellationToken)
                .ConfigureAwait(false);
            var certificate = await new LocalTlsCertificateProvider(protectedSecrets)
                .LoadOrCreateAsync(cancellationToken)
                .ConfigureAwait(false);
            var deviceId = await new WindowsDeviceIdProvider(protectedSecrets)
                .LoadOrCreateAsync(cancellationToken)
                .ConfigureAwait(false);
            _windowsDeviceId = deviceId;
            _deviceStore = new SqliteTrustedDeviceStore(_database);
            _contactStore = new SqliteContactStore(_database);
            _attachmentStore = new SqliteAttachmentStore(_database);
            _attachmentManager = new InboundAttachmentManager(
                storageOptions.AttachmentDirectory,
                protectedSecrets,
                _attachmentStore);
            var server = new LocalPairingServer(certificate, identity, _deviceStore, deviceId);
            try
            {
                await server.StartAsync(cancellationToken).ConfigureAwait(false);
                _pairingServer = server;
                _backgroundCancellation = new CancellationTokenSource();
                _syncProcessorTask = ProcessInboundSyncAsync(
                    server,
                    new ContactSyncEnvelopeHandler(
                        _contactStore,
                        new SqliteInboundSyncReceiptStore(_database)),
                    _attachmentManager,
                    _backgroundCancellation.Token);
                server.PairingCompleted += PairingServer_PairingCompleted;
                server.SessionConnected += PairingServer_SessionChanged;
                server.SessionDisconnected += PairingServer_SessionChanged;
                NetworkChange.NetworkAddressChanged += Network_AddressChanged;
                if ((await _deviceStore.ListAsync(cancellationToken).ConfigureAwait(false))
                    .Any(device => device.TrustState == DeviceTrustState.Trusted))
                {
                    QueueDiscoveryStart(server);
                }
                return server;
            }
            catch
            {
                await server.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task<IReadOnlyList<SyncedContact>> ListContactsAsync(
        string? search,
        CancellationToken cancellationToken)
    {
        await GetPairingServerAsync(cancellationToken).ConfigureAwait(false);
        return await _contactStore!
            .ListAsync(ownerDeviceId: null, search, limit: 2_000, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TrustedDevice>> ListDevicesAsync(CancellationToken cancellationToken)
    {
        await GetPairingServerAsync(cancellationToken).ConfigureAwait(false);
        return await _deviceStore!.ListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var server = await GetPairingServerAsync(cancellationToken).ConfigureAwait(false);
        var id = new DeviceId(deviceId);
        await _deviceStore!
            .RevokeAsync(id, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        await server.DisconnectAsync(id, cancellationToken).ConfigureAwait(false);
        QueueDiscoveryStart(server, restart: true);
        foreach (var pending in _pendingCommands.Values.Where(item => item.DeviceId == id))
        {
            pending.Completion.TrySetException(
                new InvalidOperationException("The target iPhone was revoked."));
        }
    }

    public async Task<IReadOnlyList<ImportedAttachment>> ListAttachmentsAsync(
        CancellationToken cancellationToken)
    {
        await GetPairingServerAsync(cancellationToken).ConfigureAwait(false);
        return await _attachmentStore!.ListAsync(2_000, cancellationToken).ConfigureAwait(false);
    }

    public async Task ExportAttachmentAsync(
        Guid attachmentId,
        Stream destination,
        CancellationToken cancellationToken)
    {
        await GetPairingServerAsync(cancellationToken).ConfigureAwait(false);
        var attachment = (await _attachmentStore!
            .ListAsync(2_000, cancellationToken)
            .ConfigureAwait(false))
            .SingleOrDefault(item => item.AttachmentId == attachmentId)
            ?? throw new FileNotFoundException("The imported photo record was not found.");
        await _attachmentManager!
            .CopyDecryptedAsync(attachment, destination, cancellationToken)
            .ConfigureAwait(false);
    }

    public static Task<IReadOnlyList<AppleBluetoothCapabilityReport>> ProbeAppleBluetoothAsync(
        CancellationToken cancellationToken) =>
        AppleBluetoothCapabilityProbe.ProbeAsync(cancellationToken);

    public async Task<CommandResultPayload> RequestDialAsync(
        string phoneNumber,
        CancellationToken cancellationToken)
    {
        var normalized = TelephoneDialString.Normalize(phoneNumber);
        var server = await GetPairingServerAsync(cancellationToken).ConfigureAwait(false);
        var devices = await _deviceStore!.ListAsync(cancellationToken).ConfigureAwait(false);
        var target = devices.FirstOrDefault(device =>
            device.Platform == DevicePlatform.Ios &&
            device.TrustState == DeviceTrustState.Trusted &&
            server.TryGetSessionId(device.Id, out _));
        if (target is null || !server.TryGetSessionId(target.Id, out var sessionId))
        {
            throw new InvalidOperationException("Connect a trusted iPhone before requesting a call.");
        }

        var commandId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
        var completion = new TaskCompletionSource<CommandResultPayload>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingCommand(target.Id, commandId, completion);
        if (!_pendingCommands.TryAdd(messageId, pending))
        {
            throw new InvalidOperationException("A command correlation identifier was reused.");
        }

        try
        {
            var payload = new CommandRequestPayload
            {
                CommandId = commandId,
                Name = "call.start",
                Arguments = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["phoneNumber"] = normalized,
                },
                ExpiresAt = expiresAt,
                RequiresUserConfirmation = true,
            };
            var envelope = new ProtocolEnvelope
            {
                ProtocolVersion = ProtocolConstants.CurrentVersion,
                MessageId = messageId,
                SessionId = sessionId,
                OriginDeviceId = _windowsDeviceId,
                OriginSequence = 0,
                SentAt = DateTimeOffset.UtcNow,
                Kind = "command.request",
                RequiresAck = true,
                Payload = JsonSerializer.SerializeToElement(payload, WebJsonOptions),
            };
            await server.SendAsync(target.Id, envelope, cancellationToken).ConfigureAwait(false);
            return await completion.Task
                .WaitAsync(expiresAt - DateTimeOffset.UtcNow, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _pendingCommands.TryRemove(messageId, out _);
        }
    }

    public async Task<AncsNotificationSession> StartAncsSessionAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _ancsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ancsSession is { IsConnected: true })
            {
                return _ancsSession;
            }
            if (_ancsSession is not null)
            {
                _ancsSession.NotificationChanged -= AncsSession_NotificationChanged;
                _ancsSession.SessionEnded -= AncsSession_SessionEnded;
                await _ancsSession.DisposeAsync().ConfigureAwait(false);
            }
            _ancsSession = await AncsNotificationSession
                .StartAsync(cancellationToken)
                .ConfigureAwait(false);
            _ancsSession.NotificationChanged += AncsSession_NotificationChanged;
            _ancsSession.SessionEnded += AncsSession_SessionEnded;
            return _ancsSession;
        }
        finally
        {
            _ancsLock.Release();
        }
    }

    public AncsNotificationSession? GetAncsSession() => _ancsSession;

    public async Task StopAncsSessionAsync()
    {
        await _ancsLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_ancsSession is not null)
            {
                _ancsSession.NotificationChanged -= AncsSession_NotificationChanged;
                _ancsSession.SessionEnded -= AncsSession_SessionEnded;
                await _ancsSession.DisposeAsync().ConfigureAwait(false);
                _ancsSession = null;
                await App.ClearAncsNotificationsAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _ancsLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NetworkChange.NetworkAddressChanged -= Network_AddressChanged;
        if (_pairingServer is not null)
        {
            _pairingServer.PairingCompleted -= PairingServer_PairingCompleted;
        }
        Task? discoveryStart;
        lock (_discoveryTaskGate)
        {
            discoveryStart = _discoveryStartTask;
        }
        if (discoveryStart is not null)
        {
            await discoveryStart.ConfigureAwait(false);
        }
        foreach (var pending in _pendingCommands.Values)
        {
            pending.Completion.TrySetException(
                new ObjectDisposedException(nameof(AppServices)));
        }
        _pendingCommands.Clear();
        await StopAncsSessionAsync().ConfigureAwait(false);
        if (_dnsSdAdvertiser is not null)
        {
            await _dnsSdAdvertiser.DisposeAsync().ConfigureAwait(false);
            _dnsSdAdvertiser = null;
        }
        if (_backgroundCancellation is not null)
        {
            await _backgroundCancellation.CancelAsync().ConfigureAwait(false);
            if (_syncProcessorTask is not null)
            {
                try
                {
                    await _syncProcessorTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
            _backgroundCancellation.Dispose();
            _backgroundCancellation = null;
            _syncProcessorTask = null;
        }
        if (_pairingServer is not null)
        {
            _pairingServer.SessionConnected -= PairingServer_SessionChanged;
            _pairingServer.SessionDisconnected -= PairingServer_SessionChanged;
            await _pairingServer.DisposeAsync().ConfigureAwait(false);
            _pairingServer = null;
        }

        if (_attachmentManager is not null)
        {
            await _attachmentManager.DisposeAsync().ConfigureAwait(false);
            _attachmentManager = null;
        }

        _database?.Dispose();
        _database = null;
        _deviceStore = null;
        _contactStore = null;
        _attachmentStore = null;
        _secretStore?.Dispose();
        _secretStore = null;
        _initializationLock.Dispose();
        _discoveryLock.Dispose();
        _ancsLock.Dispose();
    }

    private async Task StartDiscoverySafelyAsync(LocalPairingServer server, bool restart)
    {
        try
        {
            await _discoveryLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) { return; }
                var trusted = (await _deviceStore!.ListAsync(CancellationToken.None).ConfigureAwait(false))
                    .Any(device => device.TrustState == DeviceTrustState.Trusted);
                var hasLocalAddress = LocalNetworkCatalog.SelectPairingAddresses(LocalNetworkCatalog.Capture()).Length > 0;
                if (_dnsSdAdvertiser is not null && (restart || !trusted || !hasLocalAddress))
                {
                    await _dnsSdAdvertiser.DisposeAsync().ConfigureAwait(false);
                    _dnsSdAdvertiser = null;
                }
                if (_dnsSdAdvertiser is null && trusted && hasLocalAddress && !_disposed)
                {
                    _dnsSdAdvertiser = await WindowsDnsSdAdvertiser.StartAsync(
                        $"Phone Link on {Environment.MachineName}",
                        server.BoundPort,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                _discoveryLock.Release();
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Bonjour advertisement could not start: {exception.Message}");
        }
    }

    private void PairingServer_PairingCompleted(object? sender, PairingCompletedEventArgs args)
    {
        if (sender is LocalPairingServer server)
        {
            QueueDiscoveryStart(server);
        }
    }

    private static void AncsSession_NotificationChanged(
        object? sender,
        AncsNotificationChangedEventArgs args)
    {
        if (sender is AncsNotificationSession session && args.Kind == AncsEventKind.Added && args.Notification is not null)
        {
            App.ShowAncsNotification(args.Notification, session);
        }
    }

    private static void AncsSession_SessionEnded(object? sender, EventArgs args) =>
        _ = App.ClearAncsNotificationsAsync();

    private void Network_AddressChanged(object? sender, EventArgs args)
    {
        if (_pairingServer is LocalPairingServer server)
        {
            QueueDiscoveryStart(server, restart: true);
        }
    }

    private void QueueDiscoveryStart(LocalPairingServer server, bool restart = false)
    {
        lock (_discoveryTaskGate)
        {
            if (_disposed) { return; }
            _discoveryUpdateRequested = true;
            _discoveryRestartRequested |= restart;
            if (_discoveryRunnerActive) { return; }
            _discoveryRunnerActive = true;
            _discoveryStartTask = ProcessDiscoveryUpdatesAsync(server);
        }
    }

    private async Task ProcessDiscoveryUpdatesAsync(LocalPairingServer server)
    {
        while (true)
        {
            bool restart;
            lock (_discoveryTaskGate)
            {
                if (_disposed || !_discoveryUpdateRequested)
                {
                    _discoveryRunnerActive = false;
                    return;
                }
                restart = _discoveryRestartRequested;
                _discoveryRestartRequested = false;
                _discoveryUpdateRequested = false;
            }
            await StartDiscoverySafelyAsync(server, restart).ConfigureAwait(false);
        }
    }

    private async void PairingServer_SessionChanged(object? sender, DeviceSessionChangedEventArgs args)
    {
        if (sender is not LocalPairingServer server || _attachmentManager is not { } manager) return;
        try
        {
            await manager.PruneSessionsAsync((deviceId, sessionId) =>
                server.TryGetSessionId(deviceId, out var active) && active == sessionId).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Incomplete attachment cleanup failed: {exception.GetType().Name}");
        }
    }

    private async Task ProcessInboundSyncAsync(
        LocalPairingServer server,
        ContactSyncEnvelopeHandler handler,
        InboundAttachmentManager attachmentManager,
        CancellationToken cancellationToken)
    {
        await foreach (var frame in server.ReadFramesAsync(cancellationToken))
        {
            try
            {
                if (!server.TryGetSessionId(frame.DeviceId, out var activeSessionId) || activeSessionId != frame.SessionId)
                {
                    // Discard queued frames from revoked or replaced authenticated sessions.
                    continue;
                }
                if (frame is ReceivedAttachmentChunk chunk)
                {
                    await attachmentManager.HandleChunkAsync(chunk, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (frame is not ReceivedDeviceEnvelope received)
                {
                    continue;
                }

                if (TryCompleteCommand(received))
                {
                    continue;
                }

                if (received.Envelope.Kind == "sync.checkpoint.request")
                {
                    await SendResponseAsync(server, received, "sync.checkpoint",
                        await handler.GetCheckpointAsync(received, cancellationToken).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (received.Envelope.Kind == "contacts.reconcile")
                {
                    await handler.ReconcileAuthorizationAsync(received, cancellationToken).ConfigureAwait(false);
                    await SendResponseAsync(server, received, "contacts.reconciled", new { },
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var acknowledgement = await handler
                    .HandleAsync(received, cancellationToken)
                    .ConfigureAwait(false);
                if (acknowledgement is not null)
                {
                    await SendResponseAsync(
                        server,
                        received,
                        "sync.ack",
                        acknowledgement,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var attachmentResponse = await attachmentManager
                    .HandleEnvelopeAsync(received, cancellationToken)
                    .ConfigureAwait(false);
                if (attachmentResponse is not null)
                {
                    await SendResponseAsync(
                        server,
                        received,
                        attachmentResponse.Kind,
                        attachmentResponse.Payload,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Inbound sync batch rejected: {exception.Message}");
            }
        }
    }

    private async Task SendResponseAsync(
        LocalPairingServer server,
        ReceivedDeviceEnvelope received,
        string kind,
        object payload,
        CancellationToken cancellationToken)
    {
        var envelope = new ProtocolEnvelope
        {
            ProtocolVersion = ProtocolConstants.CurrentVersion,
            MessageId = Guid.NewGuid(),
            CorrelationId = received.Envelope.MessageId,
            SessionId = received.Envelope.SessionId,
            OriginDeviceId = _windowsDeviceId,
            OriginSequence = 0,
            SentAt = DateTimeOffset.UtcNow,
            Kind = kind,
            RequiresAck = false,
            Payload = JsonSerializer.SerializeToElement(payload, payload.GetType(), WebJsonOptions),
        };
        await server.SendAsync(received.DeviceId, envelope, cancellationToken).ConfigureAwait(false);
    }

    private bool TryCompleteCommand(ReceivedDeviceEnvelope received)
    {
        if (received.Envelope.Kind != "command.result" ||
            received.Envelope.CorrelationId is not Guid correlationId ||
            !_pendingCommands.TryGetValue(correlationId, out var pending) ||
            pending.DeviceId != received.DeviceId)
        {
            return false;
        }

        var result = received.Envelope.Payload.Deserialize<CommandResultPayload>(WebJsonOptions);
        if (result is null || result.CommandId != pending.CommandId)
        {
            return false;
        }

        pending.Completion.TrySetResult(result);
        return true;
    }

    private sealed record PendingCommand(
        DeviceId DeviceId,
        Guid CommandId,
        TaskCompletionSource<CommandResultPayload> Completion);
}

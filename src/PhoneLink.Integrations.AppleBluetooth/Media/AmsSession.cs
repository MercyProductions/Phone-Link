using System.Security.Cryptography;
using PhoneLink.Integrations.AppleBluetooth.Capabilities;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Security.Cryptography;

namespace PhoneLink.Integrations.AppleBluetooth.Media;

public sealed class AmsDeviceCandidate
{
    internal AmsDeviceCandidate(string id, string name) { Id = id; DisplayName = string.IsNullOrWhiteSpace(name) ? "Paired Bluetooth device" : name; }
    internal string Id { get; }
    public string DisplayName { get; }
    public override string ToString() => DisplayName;
}
public sealed record AmsDiscoveryResult(IReadOnlyList<AmsDeviceCandidate> Candidates, int UnavailableProbes);
public enum AmsSessionEndCause { None, BluetoothDisconnected, ServicesChanged, Stopped }

// Creation/discovery must be invoked from the UI context required by FromIdAsync.
// The session belongs to one explicitly selected paired device, never a fallback.
public sealed class AmsSession : IAsyncDisposable
{
    internal static readonly Guid ServiceUuid = Guid.Parse("89d3502b-0f36-433a-8ef4-c502ad55f8dc");
    private static readonly Guid RemoteUuid = Guid.Parse("9b3c81d8-57b1-4a8a-b8df-0e56f7ca51c2");
    private static readonly Guid UpdateUuid = Guid.Parse("2f7cabce-808d-411f-9a0c-bb92ba96c102");
    private static readonly Guid AttributeUuid = Guid.Parse("c6b2f38c-23ab-46d8-a6ab-a3a870bbd5d7");
    private readonly AmsSessionState _state = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AmsCommandSender _sender;
    private readonly object _disposeGate = new();
    private readonly List<string> _issues = [];
    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattCharacteristic? _remote, _updates, _attribute;
    private Task? _disposeTask;
    private int _ended, _invalid;
    private bool _remoteSubscribed, _updatesSubscribed;
    private volatile bool _ready;
    private readonly AmsDeliveryObservation? _delivery;
    private AmsDeliveryOutcome? _deliverySetupFailure;

    private AmsSession(AmsDeliveryObservation? delivery = null)
    {
        _delivery = delivery;
        _sender = new(_gate, request => IsConnected && _state.IsCurrent(request), async (command, token) =>
        {
            token.ThrowIfCancellationRequested();
            var response = _remote!.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write);
            var result = await _remote.WriteValueWithResultAsync(CryptographicBuffer.CreateFromByteArray(command),
                response ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse).AsTask(token);
            return new(result.Status == GattCommunicationStatus.Success, response, result.ProtocolError);
        }, _lifetime.Token);
    }

    public event EventHandler? Changed;
    public string DeviceName => _device?.Name ?? "Selected paired device";
    public bool IsConnected => _ready && Volatile.Read(ref _ended) == 0 && _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;
    public bool CanReadExtended => IsConnected && _updatesSubscribed && _attribute is not null;
    public int InvalidUpdates => Volatile.Read(ref _invalid);
    public AmsSessionEndCause EndCause => (AmsSessionEndCause)Volatile.Read(ref _ended);
    public string EndReason => EndCause switch
    {
        AmsSessionEndCause.BluetoothDisconnected => "Bluetooth disconnected",
        AmsSessionEndCause.ServicesChanged => "GATT services changed; a fresh session is required",
        AmsSessionEndCause.Stopped => "Stopped",
        _ => "Not ended",
    };
    public IReadOnlyList<string> SetupIssues => _issues.AsReadOnly();
    public AmsSnapshot Capture() => _state.Capture();
    public bool CanSend(AmsRemoteCommand command) => IsConnected && _state.CanSend(command);
    public AmsCommandRequest CreateRequest(AmsRemoteCommand command) => _state.CreateRequest(command);
    public Task<AmsCommandResult> SendAsync(AmsCommandRequest request, Func<bool> isAuthorized, CancellationToken cancellationToken) =>
        _sender.SendAsync(request, isAuthorized, cancellationToken);

    public async Task<bool> ReadExtendedAsync(AmsAttributeObservation observation, long revision, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        lifetime.CancelAfter(TimeSpan.FromSeconds(8));
        var token = lifetime.Token;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!CanReadExtended || _attribute is not { } attribute || !_state.IsExpandable(observation, revision)) return false;
            var selector = CryptographicBuffer.CreateFromByteArray([(byte)observation.Entity, observation.Attribute]);
            var write = await attribute.WriteValueWithResultAsync(selector, GattWriteOption.WriteWithResponse).AsTask(token).ConfigureAwait(false);
            if (write.Status != GattCommunicationStatus.Success || !CanReadExtended || !_state.IsExpandable(observation, revision)) return false;
            var read = await attribute.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(token).ConfigureAwait(false);
            if (read.Status != GattCommunicationStatus.Success || read.Value is null || read.Value.Length > 4_096) return false;
            CryptographicBuffer.CopyToByteArray(read.Value, out var bytes);
            try
            {
                token.ThrowIfCancellationRequested();
                return CanReadExtended && _state.TryExpand(observation, revision, AmsProtocol.ParseExtendedValue(bytes), DateTimeOffset.UtcNow);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { _gate.Release(); }
    }

    public static async Task<AmsDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken)
    {
        List<AmsDeviceCandidate> candidates = [];
        var unavailable = 0;
        var paired = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)).AsTask(cancellationToken);
        foreach (var information in paired)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var device = await BluetoothLEDevice.FromIdAsync(information.Id).AsTask(cancellationToken);
                if (device is null) { unavailable++; continue; }
                var result = await device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached).AsTask(cancellationToken);
                try
                {
                    if (result.Status != GattCommunicationStatus.Success) unavailable++;
                    else if (result.Services.Count > 0) candidates.Add(new(information.Id, information.Name));
                }
                finally { foreach (var service in result.Services) service.Dispose(); }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is not OutOfMemoryException) { unavailable++; }
        }
        return new(candidates.AsReadOnly(), unavailable);
    }

    public static Task<AmsSession> StartAsync(AmsDeviceCandidate candidate, CancellationToken cancellationToken) =>
        StartAsync(candidate, existingAccessOnly: false, cancellationToken);

    public static async Task<AmsDeliveryResult> CheckDeliveryAsync(AmsDeviceCandidate candidate, CancellationToken cancellationToken)
    {
        var observation = new AmsDeliveryObservation();
        await using var session = await StartAsync(candidate, existingAccessOnly: true, cancellationToken, observation);
        cancellationToken.ThrowIfCancellationRequested();
        return session._deliverySetupFailure is { } failure ? observation.Report(failure) :
            await observation.WaitAsync(TimeSpan.FromSeconds(6), cancellationToken);
    }

    private static async Task<AmsSession> StartAsync(AmsDeviceCandidate candidate, bool existingAccessOnly,
        CancellationToken cancellationToken, AmsDeliveryObservation? delivery = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var session = new AmsSession(delivery);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session._lifetime.Token);
        try { await session.ConnectAsync(candidate, existingAccessOnly, lifetime.Token); return session; }
        catch { await session.DisposeAsync().ConfigureAwait(false); throw; }
    }

    internal static Task<AmsSession> ReopenWithExistingAccessAsync(AmsDeviceCandidate candidate, CancellationToken cancellationToken) =>
        StartAsync(candidate, existingAccessOnly: true, cancellationToken);

    private async Task ConnectAsync(AmsDeviceCandidate candidate, bool existingAccessOnly, CancellationToken cancellationToken)
    {
        var paired = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)).AsTask(cancellationToken);
        if (!paired.Any(device => device.Id == candidate.Id)) throw new InvalidOperationException("The selected device is no longer paired. No replacement was selected.");
        cancellationToken.ThrowIfCancellationRequested();
        if (existingAccessOnly)
            AmsReconnectAttempt.RequireExistingAccess(DeviceAccessInformation.CreateFromId(candidate.Id).CurrentStatus);
        _device = await BluetoothLEDevice.FromIdAsync(candidate.Id).AsTask(cancellationToken)
            ?? throw new InvalidOperationException("The selected paired device is unavailable.");
        cancellationToken.ThrowIfCancellationRequested();
        var services = await _device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached).AsTask(cancellationToken);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count != 1)
        {
            foreach (var service in services.Services) service.Dispose();
            throw new InvalidOperationException($"The selected device did not expose one usable AMS instance ({services.Status}).");
        }
        _service = services.Services[0];
        cancellationToken.ThrowIfCancellationRequested();
        if (existingAccessOnly)
            AmsReconnectAttempt.RequireExistingAccess(_service.DeviceAccessInformation.CurrentStatus);
        else
        {
            var access = await GattServiceAuthorization.ReadOrRequestForManualStartAsync(_service, cancellationToken);
            if (access != DeviceAccessStatus.Allowed) throw new InvalidOperationException($"Windows denied AMS service access ({access}). No permission was approved automatically.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var opened = await _service.OpenAsync(GattSharingMode.SharedReadAndWrite).AsTask(cancellationToken);
        if (!GattServiceAuthorization.IsSharedOpen(opened))
            throw new InvalidOperationException($"Windows did not open the AMS service in shared mode ({opened}). No characteristic write was attempted.");
        cancellationToken.ThrowIfCancellationRequested();
        var (characteristics, retried) = existingAccessOnly
            ? (await _service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken), false)
            : await AmsSetupRecovery.ReadCharacteristicsAsync(
                token => _service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(token),
                result => result.Status, cancellationToken);
        if (characteristics.Status != GattCommunicationStatus.Success)
        {
            var detail = characteristics.Status == GattCommunicationStatus.AccessDenied
                ? "Windows denied characteristic access. This is not proof of an iPhone fault; no permission was changed."
                : $"Characteristic enumeration failed ({characteristics.Status}).";
            throw new InvalidOperationException($"AMS service access was allowed. {detail} " +
                (retried ? "One read-only retry was attempted. " : "The characteristic read was not retried. ") +
                "No media command was sent.");
        }
        if (retried) _issues.Add("An initial unreachable characteristic read recovered on one read-only retry.");
        _updates = characteristics.Characteristics.FirstOrDefault(characteristic => characteristic.Uuid == UpdateUuid &&
            characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify | GattCharacteristicProperties.Write));
        if (_delivery is not null)
        {
            await PrepareDeliveryCheckAsync(cancellationToken);
            return;
        }
        _remote = characteristics.Characteristics.FirstOrDefault(characteristic => characteristic.Uuid == RemoteUuid &&
            characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify) &&
            (characteristic.CharacteristicProperties & (GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse)) != 0);
        _attribute = characteristics.Characteristics.SingleOrDefault(characteristic => characteristic.Uuid == AttributeUuid &&
            characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read | GattCharacteristicProperties.Write));
        _device.ConnectionStatusChanged += ConnectionChanged;
        _device.GattServicesChanged += ServicesChanged;
        if (_remote is not null)
        {
            _remote.ValueChanged += RemoteChanged;
            _remoteSubscribed = await SubscribeAsync(_remote, cancellationToken);
            if (!_remoteSubscribed) _issues.Add("Remote-command notifications could not be subscribed; commands remain unavailable.");
        }
        else _issues.Add("The required Remote Command notify/write properties were not exposed.");
        if (_updates is not null)
        {
            _updates.ValueChanged += UpdateChanged;
            _updatesSubscribed = await SubscribeAsync(_updates, cancellationToken);
            if (_updatesSubscribed)
            {
                foreach (var entity in Enum.GetValues<AmsEntity>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var status = await _updates.WriteValueAsync(CryptographicBuffer.CreateFromByteArray(AmsProtocol.CreateSubscription(entity)),
                        GattWriteOption.WriteWithResponse).AsTask(cancellationToken);
                    if (status != GattCommunicationStatus.Success) _issues.Add($"{entity} observation registration failed ({status}); unavailable fields remain unknown.");
                }
            }
            else _issues.Add("Entity Update notifications could not be subscribed; metadata remains unknown.");
        }
        else _issues.Add("The required Entity Update notify/write properties were not exposed.");
        if (!_remoteSubscribed && !_updatesSubscribed) throw new InvalidOperationException("AMS exposed no usable notification channel. No media command was sent.");
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _ended) != 0) throw new InvalidOperationException("The media session ended during setup.");
        _ready = true;
    }

    private async Task PrepareDeliveryCheckAsync(CancellationToken token)
    {
        if (_updates is not { } updates)
        {
            _deliverySetupFailure = AmsDeliveryOutcome.NotExposed;
            return;
        }
        _device!.ConnectionStatusChanged += ConnectionChanged;
        _device.GattServicesChanged += ServicesChanged;
        updates.ValueChanged += UpdateChanged;
        if (!await SubscribeAsync(updates, token))
        {
            _deliverySetupFailure = AmsDeliveryOutcome.SubscriptionRejected;
            return;
        }
        token.ThrowIfCancellationRequested();
        _delivery!.Arm();
        var status = await updates.WriteValueAsync(CryptographicBuffer.CreateFromByteArray(AmsDeliveryObservation.CreateRegistration()),
            GattWriteOption.WriteWithResponse).AsTask(token);
        if (status != GattCommunicationStatus.Success) _deliverySetupFailure = AmsDeliveryOutcome.RegistrationRejected;
    }

    private static async Task<bool> SubscribeAsync(GattCharacteristic characteristic, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify)
            .AsTask(cancellationToken) == GattCommunicationStatus.Success;
    }

    private void RemoteChanged(GattCharacteristic sender, GattValueChangedEventArgs args) => ProcessUpdate(args, commands: true);
    private void UpdateChanged(GattCharacteristic sender, GattValueChangedEventArgs args) => ProcessUpdate(args, commands: false);
    private void ProcessUpdate(GattValueChangedEventArgs args, bool commands)
    {
        if (Volatile.Read(ref _ended) != 0) return;
        byte[]? bytes = null;
        try
        {
            if (_delivery is not null && args.CharacteristicValue.Length > 4_099) { _delivery.Reject(); return; }
            CryptographicBuffer.CopyToByteArray(args.CharacteristicValue, out bytes);
            if (_delivery is not null) _delivery.Observe(bytes);
            else if (commands) _state.UpdateCommands(bytes);
            else _state.Update(AmsProtocol.ParseUpdate(bytes, DateTimeOffset.UtcNow));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (_delivery is not null) _delivery.Reject();
            else { Interlocked.Increment(ref _invalid); _state.Invalidate(); }
        }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ConnectionChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected) End(AmsSessionEndCause.BluetoothDisconnected);
        else Changed?.Invoke(this, EventArgs.Empty);
    }
    private void ServicesChanged(BluetoothLEDevice sender, object args) => End(AmsSessionEndCause.ServicesChanged);
    private void End(AmsSessionEndCause cause)
    {
        if (Interlocked.CompareExchange(ref _ended, (int)cause, 0) != 0) return;
        _ready = false;
        _state.End();
        _delivery?.End();
        _lifetime.Cancel();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask DisposeAsync() { lock (_disposeGate) return new(_disposeTask ??= FinishDisposalAsync()); }
    private async Task FinishDisposalAsync()
    {
        End(AmsSessionEndCause.Stopped);
        if (_remote is not null) _remote.ValueChanged -= RemoteChanged;
        if (_updates is not null) _updates.ValueChanged -= UpdateChanged;
        if (_device is not null)
        { _device.ConnectionStatusChanged -= ConnectionChanged; _device.GattServicesChanged -= ServicesChanged; }
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Release our handlers and owned WinRT objects. Do not issue a late
            // CCCD=None write that could race a fresh session or another client.
            try { _service?.Dispose(); }
            finally { _device?.Dispose(); }
        }
        finally { _gate.Release(); _gate.Dispose(); _lifetime.Dispose(); }
    }
}

using System.Collections.Concurrent;
using System.Threading.Channels;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Security.Cryptography;

namespace PhoneLink.Integrations.AppleBluetooth.Ancs;

public sealed class AncsNotificationSession : IAsyncDisposable
{
    private static readonly Guid ServiceUuid = Guid.Parse("7905f431-b5ce-4e99-a40f-4b1e122d00d0");
    private static readonly Guid NotificationSourceUuid = Guid.Parse("9fbf120d-6301-42d9-8c58-25e699a21dbd");
    private static readonly Guid ControlPointUuid = Guid.Parse("69d1d8f3-45e1-49a8-9821-9bbdfdaad9d9");
    private static readonly Guid DataSourceUuid = Guid.Parse("22eac6e9-24d6-4bb5-be44-b36ace7c7bfb");

    private readonly ConcurrentDictionary<uint, AncsNotificationEvent> _metadata = new();
    private readonly ConcurrentDictionary<uint, AncsNotification> _activeNotifications = new();
    private readonly Channel<AncsNotificationEvent> _attributeQueue =
        Channel.CreateUnbounded<AncsNotificationEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Lock _responseLock = new();
    private readonly CancellationTokenSource _cancellation = new();
    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattCharacteristic? _notificationSource;
    private GattCharacteristic? _controlPoint;
    private GattCharacteristic? _dataSource;
    private Task? _attributeProcessor;
    private MemoryStream? _responseBuffer;
    private TaskCompletionSource<AttributeResponse>? _responseCompletion;
    private uint _expectedResponseUid;
    private bool _disposed;

    private AncsNotificationSession()
    {
    }

    public event EventHandler<AncsNotificationChangedEventArgs>? NotificationChanged;

    public event EventHandler? SessionEnded;

    public string DeviceName => _device?.Name ?? "iPhone";

    public bool IsConnected =>
        !_disposed && _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;

    public IReadOnlyList<AncsNotification> ActiveNotifications =>
        _activeNotifications.Values.OrderByDescending(item => item.ReceivedAt).ToArray();

    public static async Task<AncsNotificationSession> StartAsync(CancellationToken cancellationToken)
    {
        var session = new AncsNotificationSession();
        try
        {
            await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await _cancellation.CancelAsync().ConfigureAwait(false);
        _attributeQueue.Writer.TryComplete();
        if (_attributeProcessor is not null)
        {
            try
            {
                await _attributeProcessor.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_notificationSource is not null)
        {
            _notificationSource.ValueChanged -= NotificationSource_ValueChanged;
        }
        if (_dataSource is not null)
        {
            _dataSource.ValueChanged -= DataSource_ValueChanged;
        }
        if (_device is not null)
        {
            _device.ConnectionStatusChanged -= Device_ConnectionStatusChanged;
        }

        _service?.Dispose();
        _device?.Dispose();
        _activeNotifications.Clear();
        _metadata.Clear();
        _responseBuffer?.Dispose();
        _responseBuffer = null;
        _cancellation.Dispose();
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var pairedDevices = await DeviceInformation.FindAllAsync(
            BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)).AsTask(cancellationToken);
        foreach (var deviceInformation in pairedDevices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = await BluetoothLEDevice.FromIdAsync(deviceInformation.Id).AsTask(cancellationToken);
            if (candidate is null)
            {
                continue;
            }

            var services = await candidate
                .GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached)
                .AsTask(cancellationToken);
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            {
                foreach (var unavailableService in services.Services)
                {
                    unavailableService.Dispose();
                }
                candidate.Dispose();
                continue;
            }

            _device = candidate;
            _service = services.Services[0];
            foreach (var unusedService in services.Services.Skip(1))
            {
                unusedService.Dispose();
            }
            break;
        }

        if (_device is null || _service is null)
        {
            throw new InvalidOperationException(
                "No paired iPhone exposed ANCS. Pair the iPhone in Windows Bluetooth settings and allow notification access.");
        }

        var characteristics = await _service
            .GetCharacteristicsAsync(BluetoothCacheMode.Uncached)
            .AsTask(cancellationToken);
        if (characteristics.Status != GattCommunicationStatus.Success)
        {
            throw new InvalidOperationException(
                $"Windows could not read the ANCS characteristics ({characteristics.Status}).");
        }
        _notificationSource = characteristics.Characteristics.FirstOrDefault(
            item => item.Uuid == NotificationSourceUuid);
        _controlPoint = characteristics.Characteristics.FirstOrDefault(item => item.Uuid == ControlPointUuid);
        _dataSource = characteristics.Characteristics.FirstOrDefault(item => item.Uuid == DataSourceUuid);
        if (_notificationSource is null)
        {
            throw new InvalidOperationException("The paired device did not expose the mandatory ANCS Notification Source.");
        }

        if (_dataSource is not null && _controlPoint is not null)
        {
            _dataSource.ValueChanged += DataSource_ValueChanged;
            await EnableNotificationsAsync(_dataSource, "ANCS Data Source", cancellationToken)
                .ConfigureAwait(false);
            _attributeProcessor = ProcessAttributeRequestsAsync(_cancellation.Token);
        }

        _notificationSource.ValueChanged += NotificationSource_ValueChanged;
        await EnableNotificationsAsync(_notificationSource, "ANCS Notification Source", cancellationToken)
            .ConfigureAwait(false);
        _device.ConnectionStatusChanged += Device_ConnectionStatusChanged;
    }

    private static async Task EnableNotificationsAsync(
        GattCharacteristic characteristic,
        string name,
        CancellationToken cancellationToken)
    {
        var result = await characteristic
            .WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify)
            .AsTask(cancellationToken);
        if (result != GattCommunicationStatus.Success)
        {
            throw new InvalidOperationException(
                $"Windows could not subscribe to {name} ({result}). Check the iPhone Bluetooth notification permission.");
        }
    }

    private void NotificationSource_ValueChanged(
        GattCharacteristic sender,
        GattValueChangedEventArgs args)
    {
        try
        {
            CryptographicBuffer.CopyToByteArray(args.CharacteristicValue, out var value);
            var notificationEvent = AncsProtocolParser.ParseNotificationSource(value);
            if (notificationEvent.Kind == AncsEventKind.Removed)
            {
                _metadata.TryRemove(notificationEvent.NotificationUid, out _);
                _activeNotifications.TryRemove(notificationEvent.NotificationUid, out _);
                NotificationChanged?.Invoke(
                    this,
                    new AncsNotificationChangedEventArgs(
                        AncsEventKind.Removed,
                        notificationEvent.NotificationUid,
                        notification: null));
                return;
            }

            _metadata[notificationEvent.NotificationUid] = notificationEvent;
            if (_controlPoint is not null && _dataSource is not null)
            {
                _attributeQueue.Writer.TryWrite(notificationEvent);
            }
            else
            {
                Publish(notificationEvent, new Dictionary<byte, string>());
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"ANCS notification was rejected: {exception.Message}");
        }
    }

    private void DataSource_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        TaskCompletionSource<AttributeResponse>? completion = null;
        AttributeResponse? response = null;
        Exception? failure = null;
        lock (_responseLock)
        {
            if (_responseCompletion is null || _responseBuffer is null)
            {
                return;
            }
            try
            {
                CryptographicBuffer.CopyToByteArray(args.CharacteristicValue, out var fragment);
                _responseBuffer.Write(fragment);
                var accumulated = _responseBuffer.GetBuffer().AsSpan(0, checked((int)_responseBuffer.Length));
                if (AncsProtocolParser.TryParseNotificationAttributes(
                    accumulated,
                    out var uid,
                    out var attributes,
                    out _))
                {
                    if (uid != _expectedResponseUid)
                    {
                        throw new InvalidDataException("The ANCS attribute response UID did not match the request.");
                    }
                    completion = _responseCompletion;
                    response = new AttributeResponse(uid, attributes!);
                    _responseCompletion = null;
                }
            }
            catch (Exception exception)
            {
                completion = _responseCompletion;
                failure = exception;
                _responseCompletion = null;
            }
        }

        if (failure is not null)
        {
            completion?.TrySetException(failure);
        }
        else if (response is not null)
        {
            completion?.TrySetResult(response);
        }
    }

    private async Task ProcessAttributeRequestsAsync(CancellationToken cancellationToken)
    {
        await foreach (var notificationEvent in _attributeQueue.Reader.ReadAllAsync(cancellationToken))
        {
            if (_controlPoint is null || notificationEvent.Kind == AncsEventKind.Removed)
            {
                continue;
            }

            var completion = new TaskCompletionSource<AttributeResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_responseLock)
            {
                _responseBuffer?.Dispose();
                _responseBuffer = new MemoryStream(2_048);
                _responseCompletion = completion;
                _expectedResponseUid = notificationEvent.NotificationUid;
            }

            try
            {
                var command = AncsProtocolParser.CreateGetNotificationAttributesCommand(
                    notificationEvent.NotificationUid);
                var status = await _controlPoint.WriteValueAsync(
                    CryptographicBuffer.CreateFromByteArray(command),
                    GattWriteOption.WriteWithResponse).AsTask(cancellationToken);
                if (status != GattCommunicationStatus.Success)
                {
                    throw new InvalidOperationException($"The ANCS Control Point write failed ({status}).");
                }

                var response = await completion.Task
                    .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                    .ConfigureAwait(false);
                if (_metadata.TryGetValue(response.NotificationUid, out var current))
                {
                    Publish(current, response.Attributes);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"ANCS attributes could not be retrieved for {notificationEvent.NotificationUid}: {exception.Message}");
                Publish(notificationEvent, new Dictionary<byte, string>());
            }
            finally
            {
                lock (_responseLock)
                {
                    if (ReferenceEquals(_responseCompletion, completion))
                    {
                        _responseCompletion = null;
                    }
                }
            }
        }
    }

    private void Publish(
        AncsNotificationEvent notificationEvent,
        IReadOnlyDictionary<byte, string> attributes)
    {
        static string Read(IReadOnlyDictionary<byte, string> values, byte key) =>
            values.TryGetValue(key, out var value) ? value : string.Empty;

        var notification = new AncsNotification(
            notificationEvent.NotificationUid,
            notificationEvent.Category,
            notificationEvent.Flags,
            Read(attributes, 0),
            Read(attributes, 1),
            Read(attributes, 2),
            Read(attributes, 3),
            Read(attributes, 5),
            Read(attributes, 6),
            Read(attributes, 7),
            DateTimeOffset.UtcNow);
        _activeNotifications[notification.NotificationUid] = notification;
        NotificationChanged?.Invoke(
            this,
            new AncsNotificationChangedEventArgs(
                notificationEvent.Kind,
                notificationEvent.NotificationUid,
                notification));
    }

    private void Device_ConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected || _disposed)
        {
            return;
        }
        _activeNotifications.Clear();
        _metadata.Clear();
        SessionEnded?.Invoke(this, EventArgs.Empty);
    }

    private sealed record AttributeResponse(
        uint NotificationUid,
        IReadOnlyDictionary<byte, string> Attributes);
}

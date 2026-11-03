using PhoneLink.Application.Features.Diagnostics;
using Windows.Devices.Enumeration;

namespace PhoneLink.DeviceCommunication.Diagnostics;

// Read-only PnP observations. Never opens a device, starts a service or changes a driver.
public sealed class WindowsUsbPresenceMonitor : IDisposable
{
    private readonly DeviceWatcher _watcher;
    private readonly UsbPresenceHistory _history = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;
    public UsbPresenceSnapshot Snapshot => _history.Snapshot;

    private WindowsUsbPresenceMonitor()
    {
        _watcher = DeviceInformation.CreateWatcher(
            "System.Devices.Present:=System.StructuredQueryType.Boolean#True",
            ["System.Devices.HardwareIds"], DeviceInformationKind.Device);
        _watcher.Added += Added;
        _watcher.Removed += Removed;
        _watcher.Updated += Updated;
        _watcher.EnumerationCompleted += EnumerationCompleted;
        _watcher.Stopped += Stopped;
    }

    public static async Task<WindowsUsbPresenceMonitor> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var monitor = new WindowsUsbPresenceMonitor();
        try
        {
            monitor._watcher.Start();
            await monitor._ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            if (monitor.Snapshot.State != UsbPresenceState.Watching)
                throw new InvalidOperationException("USB presence enumeration is unavailable.");
            return monitor;
        }
        catch { monitor.Dispose(); throw; }
    }

    private void Added(DeviceWatcher sender, DeviceInformation device)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            var ids = device.Properties.TryGetValue("System.Devices.HardwareIds", out var value) && value is string[] values ? values : [];
            if (WindowsConnectionDiagnosticProbe.IsAppleUsbNode(device.Id, ids)) _history.Add(device.Id, DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { Fail(); }
    }

    private void Removed(DeviceWatcher sender, DeviceInformationUpdate device)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { _history.Remove(device.Id, DateTimeOffset.UtcNow); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { Fail(); }
    }

    // Presence only; a property update is not a physical disconnect/reconnect.
    private void Updated(DeviceWatcher sender, DeviceInformationUpdate device) { }
    private void EnumerationCompleted(DeviceWatcher sender, object args)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _history.CompleteEnumeration();
        _ready.TrySetResult();
    }
    private void Stopped(DeviceWatcher sender, object args) { if (Volatile.Read(ref _disposed) == 0) Fail(); }
    private void Fail() { _history.Unavailable(); _ready.TrySetResult(); }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _history.Stop(); // In-flight callbacks cannot repopulate the history.
        _watcher.Added -= Added;
        _watcher.Removed -= Removed;
        _watcher.Updated -= Updated;
        _watcher.EnumerationCompleted -= EnumerationCompleted;
        _watcher.Stopped -= Stopped;
        try
        {
            if (_watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) _watcher.Stop();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
        // Stop is asynchronous. This instance is never restarted; callbacks are detached/guarded.
    }
}

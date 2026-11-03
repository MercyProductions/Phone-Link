using PhoneLink.Application.Features.Diagnostics;
using PhoneLink.DeviceCommunication.Diagnostics;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class WindowsUsbPresenceMonitorTests
{
    [Fact]
    public async Task NativeWatcherEnumeratesAndClearsOnDispose()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var monitor = await WindowsUsbPresenceMonitor.StartAsync(cancellation.Token);
        Assert.Equal(UsbPresenceState.Watching, monitor.Snapshot.State);
        Assert.True(monitor.Snapshot.PresentNodes >= 0);
        monitor.Dispose();
        Assert.Equal(UsbPresenceState.Stopped, monitor.Snapshot.State);
        Assert.Null(monitor.Snapshot.PresentNodes);
        Assert.Empty(monitor.Snapshot.RecentChanges);
    }

    [Fact]
    public async Task PreCancelledStartDoesNotCreateWatcher()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsUsbPresenceMonitor.StartAsync(cancellation.Token));
    }
}

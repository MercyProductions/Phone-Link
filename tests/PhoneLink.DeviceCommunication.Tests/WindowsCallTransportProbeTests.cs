using PhoneLink.Application.Features.Calling;
using PhoneLink.Integrations.AppleBluetooth.Calling;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class WindowsCallTransportProbeTests
{
    [Fact]
    public async Task CapturesBoundedNativeReadOnlyStateAndHonorsCancellation()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await WindowsCallTransportProbe.CaptureAsync(deadline.Token);
        Assert.InRange(result.Observations.Count, 0, 32);
        Assert.True(result.CapturedAt <= DateTimeOffset.UtcNow);
        if (result.QueryState == CallTransportQueryState.Completed)
        {
            Assert.NotNull(result.EnumeratedEndpoints);
            Assert.True(result.EnumeratedEndpoints >= result.Observations.Count);
        }
        else Assert.Null(result.EnumeratedEndpoints);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsCallTransportProbe.CaptureAsync(cancelled.Token));
    }
}

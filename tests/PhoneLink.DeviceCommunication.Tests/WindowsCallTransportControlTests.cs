using PhoneLink.Integrations.AppleBluetooth.Calling;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class WindowsCallTransportControlTests
{
    [Fact]
    public async Task ReadOnlyDiscoveryIsBoundedAndPreCancellationDoesNotRequestAccess()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var candidates = await WindowsCallTransportControl.DiscoverAsync(deadline.Token);
        Assert.InRange(candidates.Count, 0, 32);
        Assert.All(candidates, candidate =>
        {
            Assert.InRange(candidate.DisplayName.Length, 1, 100);
            Assert.DoesNotContain(candidate.DisplayName, char.IsControl);
            Assert.Equal(candidate.DisplayName, candidate.ToString());
        });
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsCallTransportControl.DiscoverAsync(cancelled.Token));
    }
}

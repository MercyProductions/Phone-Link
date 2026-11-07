using PhoneLink.Application.Features.Diagnostics;

namespace PhoneLink.Application.Tests;

public sealed class UsbConnectionRefreshTests
{
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.UnixEpoch;

    [Fact]
    public void EnumerationAndDuplicateArrivalsDoNotTriggerRefresh()
    {
        var history = new UsbPresenceHistory();
        var refresh = new UsbConnectionRefresh();
        history.Add("first", ObservedAt);
        Assert.False(Check(0));
        history.CompleteEnumeration();
        history.Add("FIRST", ObservedAt);
        Assert.False(Check(1));
        Assert.False(Check(12));

        bool Check(int seconds) => refresh.TryStart(history.Snapshot, TimeSpan.FromSeconds(seconds), false);
    }

    [Fact]
    public void BurstWithEqualEventTimestampsRefreshesOnlyAfterQuietPeriod()
    {
        var history = new UsbPresenceHistory();
        var refresh = new UsbConnectionRefresh();
        history.CompleteEnumeration();
        history.Add("first", ObservedAt);
        Assert.False(Check(1));
        history.Add("second", ObservedAt);
        Assert.False(Check(2));
        Assert.False(Check(3));
        Assert.True(Check(4));
        Assert.False(Check(15));

        bool Check(int seconds) => refresh.TryStart(history.Snapshot, TimeSpan.FromSeconds(seconds), false);
    }

    [Fact]
    public void ChangesDuringAnotherScanStayPendingAndAreRateLimited()
    {
        var history = new UsbPresenceHistory();
        var refresh = new UsbConnectionRefresh();
        history.CompleteEnumeration();
        history.Add("first", ObservedAt);
        Assert.False(Check(1));
        Assert.False(Check(3, busy: true));
        Assert.True(Check(4));
        history.Remove("first", ObservedAt);
        Assert.False(Check(5));
        Assert.False(Check(13));
        Assert.True(Check(14));
        Assert.False(Check(25));

        bool Check(int seconds, bool busy = false) => refresh.TryStart(history.Snapshot, TimeSpan.FromSeconds(seconds), busy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StopOrFailureDropsPendingRefresh(bool failed)
    {
        var history = new UsbPresenceHistory();
        var refresh = new UsbConnectionRefresh();
        history.CompleteEnumeration();
        history.Add("first", ObservedAt);
        Assert.False(refresh.TryStart(history.Snapshot, TimeSpan.Zero, false));
        if (failed) history.Unavailable(); else history.Stop();
        Assert.False(refresh.TryStart(history.Snapshot, TimeSpan.FromSeconds(10), false));
    }
}

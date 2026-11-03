using PhoneLink.Application.Features.Diagnostics;

namespace PhoneLink.Application.Tests;

public sealed class UsbPresenceHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void InitialEnumerationIsNotAReconnectAndDuplicateEventsAreIgnored()
    {
        var history = new UsbPresenceHistory();
        Assert.Null(history.Snapshot.PresentNodes);
        history.Add("private-key", Now);
        history.Add("PRIVATE-KEY", Now);
        history.Remove("missing", Now);
        history.CompleteEnumeration();
        Assert.Equal(1, history.Snapshot.PresentNodes);
        Assert.Empty(history.Snapshot.RecentChanges);
        history.Remove("PRIVATE-KEY", Now);
        history.Remove("private-key", Now);
        history.Add("private-key", Now.AddSeconds(1));
        Assert.Collection(history.Snapshot.RecentChanges,
            removed => { Assert.False(removed.Added); Assert.Equal(0, removed.PresentNodes); },
            added => { Assert.True(added.Added); Assert.Equal(1, added.PresentNodes); });
        Assert.DoesNotContain("private-key", System.Text.Json.JsonSerializer.Serialize(history.Snapshot), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HistoryIsBoundedAndSnapshotsAreDetached()
    {
        var history = new UsbPresenceHistory();
        history.CompleteEnumeration();
        var before = history.Snapshot;
        for (var index = 0; index < 30; index++) { history.Add("one", Now.AddSeconds(index)); history.Remove("one", Now.AddSeconds(index)); }
        Assert.Empty(before.RecentChanges);
        Assert.Equal(UsbPresenceHistory.MaximumChanges, history.Snapshot.RecentChanges.Count);
        Assert.Equal(Now.AddSeconds(20), history.Snapshot.RecentChanges[0].ObservedAt);
    }

    [Fact]
    public void OverflowFailsUnknownInsteadOfReportingPartialHealthyPresence()
    {
        var history = new UsbPresenceHistory();
        for (var index = 0; index <= UsbPresenceHistory.MaximumNodes; index++) history.Add($"key-{index}", Now);
        history.CompleteEnumeration();
        Assert.Equal(UsbPresenceState.Unavailable, history.Snapshot.State);
        Assert.Null(history.Snapshot.PresentNodes);
        Assert.Empty(history.Snapshot.RecentChanges);
    }

    [Fact]
    public void StopClearsKeysAndHistoryAndRejectsLateCallbacks()
    {
        var history = new UsbPresenceHistory();
        history.CompleteEnumeration();
        history.Add("private", Now);
        history.Stop();
        history.Add("late", Now); history.Remove("private", Now); history.CompleteEnumeration(); history.Unavailable();
        Assert.Equal(UsbPresenceState.Stopped, history.Snapshot.State);
        Assert.Null(history.Snapshot.PresentNodes);
        Assert.Empty(history.Snapshot.RecentChanges);
    }

    [Fact]
    public void FailureClearsEvidenceAndCannotBeRearmedByLateEnumeration()
    {
        var history = new UsbPresenceHistory();
        history.CompleteEnumeration(); history.Add("private", Now); history.Unavailable();
        history.CompleteEnumeration(); history.Add("late", Now);
        Assert.Equal(UsbPresenceState.Unavailable, history.Snapshot.State);
        Assert.Null(history.Snapshot.PresentNodes);
        Assert.Empty(history.Snapshot.RecentChanges);
    }
}

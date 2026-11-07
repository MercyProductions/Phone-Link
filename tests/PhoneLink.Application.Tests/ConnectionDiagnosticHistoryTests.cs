using System.Text.Json;
using PhoneLink.Application.Features.Diagnostics;

namespace PhoneLink.Application.Tests;

public sealed class ConnectionDiagnosticHistoryTests
{
    private static ConnectionDiagnosticSnapshot Snapshot(int scan) => new(
        DateTimeOffset.UnixEpoch.AddSeconds(scan), 2, 0, 0, true, null, 1, 1, 0, [])
        { HasPackageIdentity = false };

    [Fact]
    public void TracksRepeatedProblemsAndVerifiedAggregateImprovement()
    {
        var history = new ConnectionDiagnosticHistory();
        history.Observe(Snapshot(1) with { UsbNodesWithProblems = 1 });
        var repeated = history.Observe(Snapshot(2) with { UsbNodesWithProblems = 1 });
        var driver = repeated.Issues.Single(issue => issue.Id == "driver");
        Assert.Equal(DiagnosticTransition.RepeatedAttention, driver.Transition);
        Assert.Equal(2, driver.AttentionObservations);
        Assert.Equal(2, driver.ConsecutiveAttentionObservations);
        Assert.Contains(repeated.Insights, insight => insight.Id == "repeated-driver-indication");
        var after = history.Observe(Snapshot(3));
        driver = after.Issues.Single(issue => issue.Id == "driver");
        Assert.Equal(DiagnosticTransition.ImprovedObservation, driver.Transition);
        Assert.Equal(0, driver.ConsecutiveAttentionObservations);
        Assert.Equal(2, driver.AttentionObservations);
        Assert.DoesNotContain(after.Insights, insight => insight.Id == "repeated-driver-indication");
        Assert.Contains("not proof of repair", after.ToShareableText());
    }

    [Fact]
    public void MissingStatusIsNotImprovementAndBreaksConsecutiveEvidence()
    {
        var history = new ConnectionDiagnosticHistory();
        history.Observe(Snapshot(1) with { UsbNodesWithProblems = 1 });
        var unknown = history.Observe(Snapshot(2) with { UsbNodesWithProblems = null });
        Assert.Equal(DiagnosticTransition.NotVerifiable, unknown.Issues.Single(issue => issue.Id == "driver").Transition);
        var attention = history.Observe(Snapshot(3) with { UsbNodesWithProblems = 1 });
        var driver = attention.Issues.Single(issue => issue.Id == "driver");
        Assert.Equal(DiagnosticTransition.NeedsAttention, driver.Transition);
        Assert.Equal(1, driver.ConsecutiveAttentionObservations);
        Assert.Equal(2, driver.AttentionObservations);
        Assert.DoesNotContain(attention.Insights, insight => insight.Id == "repeated-driver-indication");
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(1, null)]
    [InlineData(null, null)]
    public void PartialCompanionQueryRemainsUnknown(int? trusted, int? connected)
    {
        var result = new ConnectionDiagnosticHistory().Observe(Snapshot(1) with
            { TrustedCompanions = trusted, ConnectedCompanions = connected });
        Assert.Equal(DiagnosticOutcome.Unknown, result.Issues.Single(issue => issue.Id == "companion").Current);
        Assert.DoesNotContain(result.Insights, insight => insight.Id is "companion-unpaired" or "trusted-offline");
    }

    [Theory]
    [InlineData(0, 0, "companion-unpaired")]
    [InlineData(1, 0, "network-ineligible")]
    [InlineData(1, 1, "trusted-offline")]
    public void RulesExplainKnownConditionsWithoutExecutingRepairs(int trusted, int endpoints, string rule)
    {
        var result = new ConnectionDiagnosticHistory().Observe(Snapshot(1) with
            { TrustedCompanions = trusted, LocalEndpointCount = endpoints });
        Assert.Contains(result.Insights, insight => insight.Id == rule);
        Assert.Contains(result.Insights, insight => insight.Id == "usb-not-companion");
        Assert.All(result.Insights, insight => Assert.False(string.IsNullOrWhiteSpace(insight.NextStep)));
    }

    [Fact]
    public void ActiveSessionDoesNotGetOfflineRules()
    {
        var result = new ConnectionDiagnosticHistory().Observe(Snapshot(1) with { ConnectedCompanions = 1 });
        Assert.Empty(result.Insights);
        Assert.Equal(DiagnosticOutcome.Passed, result.Issues.Single(issue => issue.Id == "companion").Current);
    }

    [Fact]
    public void BoundedWindowEvictsOldAttentionAndClearStartsFresh()
    {
        var history = new ConnectionDiagnosticHistory();
        history.Observe(Snapshot(1) with { UsbNodesWithProblems = 1 });
        ConnectionDiagnosticComparison? result = null;
        for (var scan = 2; scan <= 21; scan++) result = history.Observe(Snapshot(scan));
        Assert.NotNull(result);
        Assert.Equal(ConnectionDiagnosticHistory.MaximumScans, result.RetainedScans);
        Assert.Equal(0, result.Issues.Single(issue => issue.Id == "driver").AttentionObservations);
        history.Clear();
        result = history.Observe(Snapshot(22));
        Assert.Equal(1, result.RetainedScans);
        Assert.Null(result.PreviousCapturedAt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void DeploymentChangeOrLossOfIdentityKnowledgeResetsComparison(bool? identity)
    {
        var history = new ConnectionDiagnosticHistory();
        history.Observe(Snapshot(1));
        var result = history.Observe(Snapshot(2) with { HasPackageIdentity = identity });
        Assert.True(result.DeploymentScopeReset);
        Assert.Equal(1, result.RetainedScans);
        Assert.Null(result.PreviousCapturedAt);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void ReplayOrOutOfOrderDoesNotInflateRepeatedEvidence(int secondScan)
    {
        var history = new ConnectionDiagnosticHistory();
        history.Observe(Snapshot(1));
        Assert.Throws<ArgumentException>(() => history.Observe(Snapshot(secondScan)));
        Assert.Equal(2, history.Observe(Snapshot(2)).RetainedScans);
    }

    [Fact]
    public void TextAndJsonExcludeRawSnapshotStringsAndHaveVersionedStructuredEvidence()
    {
        var result = new ConnectionDiagnosticHistory().Observe(Snapshot(1) with
            { AppleDevicesVersion = "PRIVATE-VERSION-CANARY", UnavailableProbes = ["SECRET-ERROR-CANARY"] });
        foreach (var output in new[] { result.ToShareableText(), result.ToShareableJson() })
        {
            Assert.DoesNotContain("CANARY", output);
            Assert.Contains("not same-device tracking", output);
        }
        using var json = JsonDocument.Parse(result.ToShareableJson());
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Reported", json.RootElement.GetProperty("confidence").GetString());
        Assert.Equal(6, json.RootElement.GetProperty("comparison").GetProperty("Issues").GetArrayLength());
    }

    [Fact]
    public void TransportGapNeedsRepeatedKnownEvidenceAndStopsWhenInventoryRecovers()
    {
        var history = new ConnectionDiagnosticHistory();
        var gap = new UsbTransportObservation(UsbTransportState.Responded, 0);
        history.Observe(Snapshot(1) with { UsbTransport = gap });
        var timeout = history.Observe(Snapshot(2) with { UsbTransport = new(UsbTransportState.TimedOut) });
        Assert.Equal(DiagnosticTransition.NotVerifiable, timeout.Issues.Single(issue => issue.Id == "usb-transport").Transition);
        var first = history.Observe(Snapshot(3) with { UsbTransport = gap });
        Assert.DoesNotContain(first.Insights, insight => insight.Id == "repeated-usb-transport-gap");
        var repeated = history.Observe(Snapshot(4) with { UsbTransport = gap });
        Assert.Contains(repeated.Insights, insight => insight.Id == "repeated-usb-transport-gap");
        var recovered = history.Observe(Snapshot(5) with { UsbTransport = new(UsbTransportState.Responded, 1) });
        Assert.Equal(DiagnosticTransition.ImprovedObservation, recovered.Issues.Single(issue => issue.Id == "usb-transport").Transition);
        Assert.DoesNotContain(recovered.Insights, insight => insight.Id == "repeated-usb-transport-gap");
    }
}

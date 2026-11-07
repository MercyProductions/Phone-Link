using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhoneLink.Application.Features.Diagnostics;

public enum DiagnosticTransition { FirstObservation, Unchanged, NeedsAttention, RepeatedAttention, ImprovedObservation, NotVerifiable, Changed }

public sealed record ConnectionIssueObservation(string Id, string Title, DiagnosticOutcome Current,
    DiagnosticOutcome? Previous, DiagnosticTransition Transition, int AttentionObservations,
    int ConsecutiveAttentionObservations);

public sealed record ConnectionDiagnosticInsight(string Id, string Evidence, string NextStep);

public sealed record ConnectionDiagnosticComparison(DateTimeOffset CapturedAt, DateTimeOffset? PreviousCapturedAt,
    int RetainedScans, bool DeploymentScopeReset, IReadOnlyList<ConnectionIssueObservation> Issues,
    IReadOnlyList<ConnectionDiagnosticInsight> Insights)
{
    public const string Scope = "Aggregate Windows observations, not same-device tracking. Up to 20 completed scans stay in page memory; lock or leaving clears history. Improvement is not proof of repair or its cause. Unknown never means healthy.";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToShareableText() => string.Join(Environment.NewLine,
        new[] { "Connection comparison", Scope,
            $"Current: {CapturedAt.ToString("O", CultureInfo.InvariantCulture)}; previous: {PreviousCapturedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "none"}; retained scans: {RetainedScans}.",
            DeploymentScopeReset ? "History restarted because deployment identity availability or mode changed." : string.Empty }
        .Concat(Issues.Select(issue => $"{issue.Title}: {issue.Current}; previous={issue.Previous?.ToString() ?? "none"}; {issue.Transition}; attention in {issue.AttentionObservations}/{RetainedScans} retained scans, {issue.ConsecutiveAttentionObservations} consecutive."))
        .Concat(Insights.Select(insight => $"Evidence: {insight.Evidence}\nNext: {insight.NextStep}")));

    // Only fixed policy text, outcomes, counts and timestamps. Never serialize the source snapshot.
    public string ToShareableJson() => JsonSerializer.Serialize(new
    {
        schemaVersion = 1, source = "Phone Link Windows connection checks", confidence = "Reported",
        scope = Scope, comparison = this,
    }, JsonOptions);
}

/// <summary>UI-thread owned, bounded and identifier-free; no raw errors, versions or phone data retained.</summary>
public sealed class ConnectionDiagnosticHistory
{
    public const int MaximumScans = 20;
    private static readonly string[] TrackedIds = ["usb", "driver", "apple-devices", "usb-transport", "network", "companion"];
    private readonly Queue<Observation> _observations = new();

    public void Clear() => _observations.Clear();

    public ConnectionDiagnosticComparison Observe(ConnectionDiagnosticSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var previous = _observations.LastOrDefault();
        var reset = previous is not null && previous.HasPackageIdentity != snapshot.HasPackageIdentity;
        if (reset) { Clear(); previous = null; }
        if (previous is not null && snapshot.CapturedAt <= previous.CapturedAt)
            throw new ArgumentException("A comparison requires a fresh, later scan, not a repeated or out-of-order snapshot.", nameof(snapshot));

        var findings = ConnectionDiagnosticEvaluator.Evaluate(snapshot).Findings
            .Where(finding => TrackedIds.Contains(finding.Id, StringComparer.Ordinal)).ToArray();
        var current = new Observation(snapshot.CapturedAt, snapshot.HasPackageIdentity,
            findings.ToDictionary(finding => finding.Id, finding => finding.Outcome, StringComparer.Ordinal));
        _observations.Enqueue(current);
        while (_observations.Count > MaximumScans) _observations.Dequeue();
        var observations = _observations.ToArray();
        var issues = findings.Select(finding =>
        {
            DiagnosticOutcome? prior = previous?.Outcomes[finding.Id];
            var consecutive = observations.Reverse().TakeWhile(item => item.Outcomes[finding.Id] == DiagnosticOutcome.Attention).Count();
            var transition = finding.Outcome == DiagnosticOutcome.Unknown ? DiagnosticTransition.NotVerifiable :
                prior is null ? DiagnosticTransition.FirstObservation :
                finding.Outcome == DiagnosticOutcome.Attention ? prior == DiagnosticOutcome.Attention ? DiagnosticTransition.RepeatedAttention : DiagnosticTransition.NeedsAttention :
                finding.Outcome == DiagnosticOutcome.Passed && prior == DiagnosticOutcome.Attention ? DiagnosticTransition.ImprovedObservation :
                finding.Outcome == prior ? DiagnosticTransition.Unchanged : DiagnosticTransition.Changed;
            return new ConnectionIssueObservation(finding.Id, finding.Title, finding.Outcome, prior, transition,
                observations.Count(item => item.Outcomes[finding.Id] == DiagnosticOutcome.Attention), consecutive);
        }).ToArray();

        List<ConnectionDiagnosticInsight> insights = [];
        if (snapshot.AppleUsbNodes > 0 && snapshot.ConnectedCompanions == 0)
            insights.Add(new("usb-not-companion", "Windows reports Apple USB nodes but no authenticated Phone Link session.",
                "USB presence is a separate path. Install/open the companion and pair inside its scanner; do not reset USB Trust or replace drivers solely for this mismatch."));
        if (snapshot.TrustedCompanions == 0 && snapshot.ConnectedCompanions == 0)
            insights.Add(new("companion-unpaired", "This Windows protected store reports no trusted companions and no active sessions.",
                "Pair the installed companion with this app instance. A standalone and packaged app can use different protected stores; missing local trust is not proof that phone trust is corrupt."));
        if (snapshot.LocalEndpointCount == 0 && snapshot.ConnectedCompanions == 0)
            insights.Add(new("network-ineligible", "No eligible local pairing addresses and no authenticated session were reported.",
                "Check a trusted local network before retrying discovery. Do not disable the firewall or remove pairing records. This evidence does not establish the specific network cause."));
        if (snapshot.TrustedCompanions > 0 && snapshot.ConnectedCompanions == 0 && snapshot.LocalEndpointCount > 0)
            insights.Add(new("trusted-offline", "Local trust and eligible PC addresses exist, but there is no authenticated companion session.",
                "Open the companion in the foreground and check its local-network permission/network. Missing app discovery may be repaired by the existing bounded maintenance; local registration alone will not prove phone reachability. Stale pairing is only a possibility, not a diagnosis; do not delete trust automatically."));
        if (issues.Any(issue => issue.Id == "driver" && issue.ConsecutiveAttentionObservations >= 2))
            insights.Add(new("repeated-driver-indication", "Consecutive scans report a Windows device/driver problem indication.",
                "Compare the numeric Windows problem-code guidance with Device Manager. Aggregate observations may involve different nodes or phones; repetition does not prove a bad cable, failed hardware or a specific driver conflict. No driver reset is authorized by this rule."));
        if (issues.Any(issue => issue.Id == "usb-transport" && issue.ConsecutiveAttentionObservations >= 2))
            insights.Add(new("repeated-usb-transport-gap", "Consecutive scans show Apple USB nodes without a usable USB transport listing.",
                "Check whether Apple Devices sees the phone. Node and transport counts describe different layers; do not reset iPhone Trust or restart services based on this result alone."));
        return new(snapshot.CapturedAt, previous?.CapturedAt, observations.Length, reset, issues, insights.ToArray());
    }

    private sealed record Observation(DateTimeOffset CapturedAt, bool? HasPackageIdentity,
        IReadOnlyDictionary<string, DiagnosticOutcome> Outcomes);
}

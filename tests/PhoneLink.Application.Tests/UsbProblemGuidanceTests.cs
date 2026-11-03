using PhoneLink.Application.Features.Diagnostics;

namespace PhoneLink.Application.Tests;

public sealed class UsbProblemGuidanceTests
{
    [Theory]
    [InlineData(10u, "could not start", "data-capable cable")]
    [InlineData(14u, "restart is required", "Save your work")]
    [InlineData(22u, "is disabled", "Enable it only if")]
    [InlineData(28u, "not installed", "Avoid third-party driver packs")]
    [InlineData(43u, "reported a device failure", "cannot identify a damaged")]
    [InlineData(999u, "device problem", "official code reference")]
    public void GivesCodeSpecificNonAutomaticGuidance(uint code, string evidence, string next)
    {
        var finding = UsbProblemGuidance.Create(new(code, 2));
        Assert.Equal(DiagnosticOutcome.Attention, finding.Outcome);
        Assert.Contains(evidence, finding.Evidence);
        Assert.Contains(next, finding.NextStep);
        Assert.Contains("not a hardware-fault diagnosis", finding.Evidence);
        Assert.Equal("learn.microsoft.com", finding.SupportUri.Host);
    }

    [Fact]
    public void NonzeroCodeOverridesCleanBooleanAndZeroDoesNotCreateAProblemFinding()
    {
        var snapshot = new ConnectionDiagnosticSnapshot(DateTimeOffset.UtcNow, 3, 0, 0, true, null, 1, 0, 0, [])
        {
            UsbProblemCodes = [new(0, 2), new(43, 1)],
            UsbNodesWithUnavailableProblemCode = 0,
        };
        var report = ConnectionDiagnosticEvaluator.Evaluate(snapshot);
        Assert.Equal(DiagnosticOutcome.Attention, report.Findings.Single(item => item.Id == "driver").Outcome);
        Assert.Single(report.Findings, item => item.Id.StartsWith("usb-problem-", StringComparison.Ordinal));
        Assert.Contains("code 43", report.ToShareableText());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public void MissingNumericEvidenceIsUnknownNotAHealthyCode(int? missing)
    {
        var snapshot = new ConnectionDiagnosticSnapshot(DateTimeOffset.UtcNow, 1, 0, 0, true, null, 1, 0, 0, [])
        { UsbProblemCodes = [], UsbNodesWithUnavailableProblemCode = missing };
        var report = ConnectionDiagnosticEvaluator.Evaluate(snapshot);
        Assert.Equal(DiagnosticOutcome.Unknown, report.Findings.Single(item => item.Id == "usb-code-coverage").Outcome);
        Assert.DoesNotContain(report.Findings, item => item.Id.StartsWith("usb-problem-", StringComparison.Ordinal));
    }
}

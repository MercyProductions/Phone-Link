using PhoneLink.Application.Features.Diagnostics;

namespace PhoneLink.Application.Tests;

public sealed class ConnectionDiagnosticEvaluatorTests
{
    private static ConnectionDiagnosticSnapshot Snapshot => new(DateTimeOffset.UtcNow,
        2, 0, 0, true, "1.2.3.4", 2, 1, 1, []);

    [Fact]
    public void HealthyWindowsEvidenceDoesNotClaimPhoneHardwareOrTrustHealth()
    {
        var report = ConnectionDiagnosticEvaluator.Evaluate(Snapshot);
        Assert.Equal(6, report.Findings.Count);
        Assert.Equal(DiagnosticOutcome.Passed, report.Findings.Single(finding => finding.Id == "usb").Outcome);
        Assert.Contains("not necessarily iPhones", report.Findings.Single(finding => finding.Id == "usb").Evidence);
        Assert.Contains("does not prove iOS trust", report.Findings.Single(finding => finding.Id == "driver").Evidence);
        Assert.Equal(DiagnosticOutcome.Guidance, report.Findings.Single(finding => finding.Id == "recovery").Outcome);
    }

    [Fact]
    public void EmptyUsbInventoryIsNotAPhoneHardwareFailure()
    {
        var report = ConnectionDiagnosticEvaluator.Evaluate(Snapshot with { AppleUsbNodes = 0 });
        Assert.Equal(DiagnosticOutcome.Attention, report.Findings.Single(finding => finding.Id == "usb").Outcome);
        Assert.Equal(DiagnosticOutcome.Unknown, report.Findings.Single(finding => finding.Id == "driver").Outcome);
        Assert.Contains("Charging alone", report.ToShareableText());
    }

    [Fact]
    public void ProbeFailureIsUnknownRatherThanNotInstalledOrNotConnected()
    {
        var report = ConnectionDiagnosticEvaluator.Evaluate(Snapshot with
        {
            AppleUsbNodes = null, UsbNodesWithProblems = null, UsbNodesWithUnknownStatus = null,
            AppleDevicesInstalled = null, LocalEndpointCount = null,
            TrustedCompanions = null, ConnectedCompanions = null,
        });
        Assert.All(report.Findings.Where(finding => finding.Id != "recovery"),
            finding => Assert.Equal(DiagnosticOutcome.Unknown, finding.Outcome));
    }

    [Theory]
    [InlineData(1, 0, DiagnosticOutcome.Attention)]
    [InlineData(0, 1, DiagnosticOutcome.Unknown)]
    [InlineData(null, 0, DiagnosticOutcome.Unknown)]
    [InlineData(0, null, DiagnosticOutcome.Unknown)]
    [InlineData(0, 0, DiagnosticOutcome.Passed)]
    public void DistinguishesProblemNodesFromUnknownStatus(int? problems, int? unknown, DiagnosticOutcome expected)
    {
        var report = ConnectionDiagnosticEvaluator.Evaluate(Snapshot with
            { UsbNodesWithProblems = problems, UsbNodesWithUnknownStatus = unknown });
        Assert.Equal(expected, report.Findings.Single(finding => finding.Id == "driver").Outcome);
    }

    [Fact]
    public void UsbPresenceDoesNotEstablishCompanionSession()
    {
        var report = ConnectionDiagnosticEvaluator.Evaluate(Snapshot with { ConnectedCompanions = 0 });
        Assert.Equal(DiagnosticOutcome.Attention, report.Findings.Single(finding => finding.Id == "companion").Outcome);
        Assert.Contains("USB detection alone cannot establish", report.ToShareableText());
    }

    [Fact]
    public void ShareableReportExcludesProbeErrorsAndArbitraryPackageVersionText()
    {
        var report = ConnectionDiagnosticEvaluator.Evaluate(Snapshot with
            { UnavailableProbes = ["SENSITIVE-SERIAL"], AppleDevicesVersion = "PRIVATE-DEVICE-NAME" });
        var text = report.ToShareableText();
        Assert.DoesNotContain("SENSITIVE-SERIAL", text);
        Assert.DoesNotContain("PRIVATE-DEVICE-NAME", text);
        Assert.Contains("Restore erases data", text);
    }
}

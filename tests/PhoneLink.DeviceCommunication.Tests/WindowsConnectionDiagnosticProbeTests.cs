using PhoneLink.DeviceCommunication.Diagnostics;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class WindowsConnectionDiagnosticProbeTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("43", null)]
    [InlineData(true, null)]
    [InlineData(-1, null)]
    [InlineData(0, 0u)]
    [InlineData(43, 43u)]
    [InlineData(28u, 28u)]
    public void ProblemCodesAreTypedEvidenceNotCoercedDefaults(object? raw, uint? expected) =>
        Assert.Equal(expected, WindowsConnectionDiagnosticProbe.ParseProblemCode(raw));

    [Theory]
    [InlineData("USB\\VID_05AC&PID_12A8\\REDACTED", true)]
    [InlineData("usb\\vid_05ac&pid_0001\\REDACTED", true)]
    [InlineData("USB\\VID_05AC0&PID_0001\\REDACTED", false)]
    [InlineData("BTHENUM\\VID_05AC&PID_0001", false)]
    [InlineData("USB\\VID_1234&PID_05AC", false)]
    public void RequiresUsbAppleVendorPrefixRatherThanFriendlyNameOrSubstring(string instanceId, bool expected)
    {
        Assert.Equal(expected, WindowsConnectionDiagnosticProbe.IsAppleUsbNode(instanceId, []));
    }

    [Fact]
    public async Task CapturesReadOnlyHostEvidenceAndHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var snapshot = await WindowsConnectionDiagnosticProbe.CaptureAsync(cancellation.Token);
        Assert.True(snapshot.CapturedAt <= DateTimeOffset.UtcNow);
        Assert.True(snapshot.AppleUsbNodes is null or >= 0);
        Assert.True(snapshot.LocalEndpointCount is null or >= 0 and <= 8);
        Assert.Null(snapshot.TrustedCompanions);
        if (snapshot.AppleUsbNodes is { } nodes)
        {
            Assert.NotNull(snapshot.UsbProblemCodes);
            Assert.Equal(nodes, snapshot.UsbProblemCodes.Sum(item => item.NodeCount) + snapshot.UsbNodesWithUnavailableProblemCode);
        }
        Assert.All(snapshot.UnavailableProbes, code => Assert.EndsWith("-unavailable", code, StringComparison.Ordinal));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WindowsConnectionDiagnosticProbe.CaptureAsync(cancelled.Token));
    }
}

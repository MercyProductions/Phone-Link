using System.Net.Sockets;
using PhoneLink.Application.Features.Diagnostics;
using PhoneLink.DeviceCommunication.Diagnostics;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class WindowsConnectionDiagnosticProbeTests
{
    [Theory]
    [InlineData("refused", UsbTransportState.ConnectionRefused)]
    [InlineData("timeout", UsbTransportState.TimedOut)]
    [InlineData("bad-frame", UsbTransportState.InvalidReply)]
    [InlineData("truncated", UsbTransportState.InvalidReply)]
    [InlineData("other", UsbTransportState.Unavailable)]
    public async Task TransportErrorsAreMappedWithoutRawMessages(string fixture, UsbTransportState expected)
    {
        var observation = await WindowsConnectionDiagnosticProbe.CaptureUsbTransportAsync(_ => throw fixture switch
        {
            "refused" => new SocketException((int)SocketError.ConnectionRefused),
            "timeout" => new OperationCanceledException("PRIVATE-ERROR"),
            "bad-frame" => new InvalidDataException("PRIVATE-ERROR"),
            "truncated" => new EndOfStreamException("PRIVATE-ERROR"),
            _ => new IOException("PRIVATE-ERROR"),
        }, CancellationToken.None);
        Assert.Equal(new UsbTransportObservation(expected), observation);
        Assert.DoesNotContain("PRIVATE-ERROR", observation.ToString());
    }

    [Fact]
    public async Task TransportEvidenceRetainsOnlyUsbCountAndPropagatesCallerCancellation()
    {
        var observation = await WindowsConnectionDiagnosticProbe.CaptureUsbTransportAsync(
            _ => Task.FromResult(new UsbMuxInventory(2, 3, 4)), CancellationToken.None);
        Assert.Equal(new UsbTransportObservation(UsbTransportState.Responded, 2), observation);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WindowsConnectionDiagnosticProbe.CaptureUsbTransportAsync(async token =>
            {
                await cancellation.CancelAsync();
                token.ThrowIfCancellationRequested();
                return new UsbMuxInventory(0, 0, 0);
            }, cancellation.Token));
    }
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
        Assert.NotNull(snapshot.UsbTransport);
        Assert.True(snapshot.UsbTransport.UsbConnections is null or >= 0 and <= 256);
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

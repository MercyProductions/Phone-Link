using System.Text;
using PhoneLink.DeviceCommunication.Diagnostics;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class ExperimentalSshAvailabilityProbeTests
{
    [Theory]
    [InlineData("SSH-2.0-test-server\r\n")]
    [InlineData("Service notice\r\nSSH-2.0-test-server\r\n")]
    public async Task IdentifiesProtocolWithoutClaimingHostAuthentication(string wire)
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(wire));
        var result = await ExperimentalSshAvailabilityProbe.ReadIdentificationAsync(stream, CancellationToken.None);
        Assert.True(result.ServerIdentified);
        Assert.Equal("2.0", result.ProtocolVersion);
        Assert.Contains("authentication are not verified", result.Status);
    }

    [Theory]
    [InlineData("HTTP/1.1 200 OK\r\n")]
    [InlineData("SSH-1.0-old-server\r\n")]
    [InlineData("SSH-2.0-evil\0data\r\n")]
    public async Task DoesNotTreatNonSshOrMalformedIdentificationAsAServer(string wire)
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(wire));
        Assert.False((await ExperimentalSshAvailabilityProbe.ReadIdentificationAsync(stream, CancellationToken.None)).ServerIdentified);
    }

    [Fact]
    public async Task RejectsTruncatedOverlongIdentification()
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("SSH-2.0-" + new string('a', 256)));
        Assert.False((await ExperimentalSshAvailabilityProbe.ReadIdentificationAsync(stream, CancellationToken.None)).ServerIdentified);
    }

    [Fact]
    public async Task HonorsCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await using var stream = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ExperimentalSshAvailabilityProbe.ReadIdentificationAsync(stream, cancelled.Token));
    }
}

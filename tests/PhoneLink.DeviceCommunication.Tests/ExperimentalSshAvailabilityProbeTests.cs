using System.Text;
using PhoneLink.DeviceCommunication.Diagnostics;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class ExperimentalSshAvailabilityProbeTests
{
    [Theory]
    [InlineData("SSH-2.0-test_server\r\n")]
    [InlineData("Service notice\r\nSSH-2.0-test_server\r\n")]
    [InlineData("Service café\r\nSSH-2.0-OpenSSH_9.9 comments-with-hyphens\r\n")]
    public async Task IdentifiesProtocolWithoutClaimingHostAuthentication(string wire)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(wire));
        var result = await ExperimentalSshAvailabilityProbe.ReadIdentificationAsync(stream, CancellationToken.None);
        Assert.True(result.ServerIdentified);
        Assert.Equal("2.0", result.ProtocolVersion);
        Assert.Contains("authentication are not verified", result.Status);
    }

    [Theory]
    [InlineData("HTTP/1.1 200 OK\r\n")]
    [InlineData("SSH-1.0-old-server\r\n")]
    [InlineData("SSH-2.0-evil\0data\r\n")]
    [InlineData("SSH-2.0- \r\n")]
    [InlineData("SSH-2.0- bad\r\n")]
    [InlineData("SSH-2.0-bad-name\r\n")]
    [InlineData("SSH-2.0-ba\rd\r\n")]
    [InlineData("SSH-2.0-valid\n")]
    [InlineData("SSH-2.0-valid\r\r\n")]
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

    [Theory]
    [InlineData(245, true)]
    [InlineData(246, false)]
    public async Task Enforces255ByteLimitIncludingCrLf(int softwareLength, bool expected)
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("SSH-2.0-" + new string('a', softwareLength) + "\r\n"));
        Assert.Equal(expected, (await ExperimentalSshAvailabilityProbe.ReadIdentificationAsync(stream, CancellationToken.None)).ServerIdentified);
    }

    [Fact]
    public async Task Identifies199CompatibilityWithoutClaimingSsh1Login()
    {
        await using var stream = new MemoryStream("SSH-1.99-example\n"u8.ToArray());
        var result = await ExperimentalSshAvailabilityProbe.ReadIdentificationAsync(stream, CancellationToken.None);
        Assert.True(result.ServerIdentified);
        Assert.Equal("1.99", result.ProtocolVersion);
        Assert.Contains("no login", result.Status);
    }

    [Fact]
    public async Task BoundedPreambleAndBannerAreNotRetained()
    {
        var wire = string.Concat(Enumerable.Repeat("PRIVATE-PREAMBLE\r\n", 8)) + "SSH-2.0-valid\r\n";
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(wire));
        var result = await ExperimentalSshAvailabilityProbe.ReadIdentificationAsync(stream, CancellationToken.None);
        Assert.False(result.ServerIdentified);
        Assert.DoesNotContain("PRIVATE", result.ToString());
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

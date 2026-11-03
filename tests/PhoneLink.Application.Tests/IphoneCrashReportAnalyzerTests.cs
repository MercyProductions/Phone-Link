using System.Text;
using PhoneLink.Application.Features.Diagnostics;

namespace PhoneLink.Application.Tests;

public sealed class IphoneCrashReportAnalyzerTests
{
    private static async Task<IphoneCrashAnalysis> AnalyzeAsync(string body, string header = "{\"bug_type\":\"309\"}")
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(header + "\n" + body));
        return await IphoneCrashReportAnalyzer.AnalyzeAsync(stream);
    }

    [Theory]
    [InlineData("EXC_BAD_ACCESS", "Invalid memory access signature")]
    [InlineData("EXC_RESOURCE", "Resource-limit signature")]
    [InlineData("EXC_GUARD", "Guarded-resource violation signature")]
    [InlineData("EXC_BREAKPOINT", "Trap or invalid-instruction signature")]
    [InlineData("EXC_BAD_INSTRUCTION", "Trap or invalid-instruction signature")]
    [InlineData("EXC_ARITHMETIC", "Arithmetic exception signature")]
    [InlineData("EXC_CRASH", "Process termination; cause unresolved")]
    [InlineData("FUTURE_TYPE", "Unrecognized or missing crash signature")]
    public async Task KnownSignaturesDoNotClaimRootCause(string type, string expected)
    {
        var analysis = await AnalyzeAsync($$$"""{"exception":{"type":"{{{type}}}"}}""");
        Assert.Equal(expected, analysis.Category);
        Assert.Contains("does not establish the cause", analysis.NextStep);
        Assert.Contains("No phone change or repair", analysis.ToRedactedText());
    }

    [Theory]
    [InlineData("FRONTBOARD", "Watchdog termination signature")]
    [InlineData("RUNNINGBOARD", "Watchdog termination signature")]
    [InlineData("SPRINGBOARD", "Watchdog termination signature")]
    [InlineData("SIGNAL", "Unrecognized or missing crash signature")]
    [InlineData("PRIVATE_NAMESPACE", "Unrecognized or missing crash signature")]
    public async Task WatchdogRequiresKnownNamespaceAndNumericCode(string space, string expected)
    {
        var analysis = await AnalyzeAsync($$$"""{"termination":{"namespace":"{{{space}}}","code":2343432205}}""");
        Assert.Equal(expected, analysis.Category);
        Assert.Contains("0x8badf00d", analysis.Evidence);
    }

    [Fact]
    public async Task SourceSecretsNeverEnterAnalysis()
    {
        const string marker = "PRIVATE-OWNER-CONTENT";
        var analysis = await AnalyzeAsync("""{"procName":"PRIVATE-OWNER-CONTENT","crashReporterKey":"PRIVATE-OWNER-CONTENT","incident":"PRIVATE-OWNER-CONTENT","exception":{"type":"EXC_CRASH","signal":"SIGABRT"},"termination":{"indicator":"PRIVATE-OWNER-CONTENT"},"threads":[{"frames":[{"symbol":"PRIVATE-OWNER-CONTENT"}]}]}""");
        Assert.Equal("Abort signal signature", analysis.Category);
        Assert.DoesNotContain(marker, analysis.ToRedactedText());
    }

    [Theory]
    [InlineData("{\"isNonFatal\":true}")]
    [InlineData("{\"isSimulated\":\"true\"}")]
    [InlineData("{\"isNonFatal\":\"unexpected\"}")]
    public async Task NonfatalOrAmbiguousFlagsPreventCrashClaim(string body) =>
        Assert.Equal("Nonfatal, simulated or ambiguous report", (await AnalyzeAsync(body)).Category);

    [Theory]
    [InlineData("{}", "{\"bug_type\":\"210\"}")]
    [InlineData("{}", "{\"bug_type\":\"298\"}")]
    [InlineData("{}", "{\"bug_type\":309}")]
    [InlineData("{}", "{}")]
    [InlineData("[]", "{\"bug_type\":\"309\"}")]
    [InlineData("{", "{\"bug_type\":\"309\"}")]
    [InlineData("{} {}", "{\"bug_type\":\"309\"}")]
    [InlineData("{\"exception\":{},\"exception\":{}}", "{\"bug_type\":\"309\"}")]
    [InlineData("{\"a\":[{\"b\":1,\"b\":2}]}", "{\"bug_type\":\"309\"}")]
    public async Task InvalidOrUnsupportedReportsAreRejected(string body, string header) =>
        await Assert.ThrowsAsync<InvalidDataException>(() => AnalyzeAsync(body, header));

    [Fact]
    public async Task Utf8BomAndWhitespaceAccepted()
    {
        var analysis = await AnalyzeAsync(" {\"isNonFatal\":\"false\",\"isSimulated\":false} \r\n", "\uFEFF{\"bug_type\":\"309\"}");
        Assert.Equal("Unrecognized or missing crash signature", analysis.Category);
    }

    [Fact]
    public async Task OversizedReportRejected()
    {
        using var stream = new MemoryStream(new byte[IphoneCrashReportAnalyzer.MaximumBytes + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => IphoneCrashReportAnalyzer.AnalyzeAsync(stream));
    }

    [Fact]
    public async Task CancellationObserved()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => IphoneCrashReportAnalyzer.AnalyzeAsync(stream, cancellation.Token));
    }

    [Fact]
    public async Task ExcessiveNestingRejectedWithoutSourceEcho()
    {
        var body = "{\"private\":" + new string('[', 70) + "0" + new string(']', 70) + "}";
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => AnalyzeAsync(body));
        Assert.DoesNotContain("private", exception.Message);
    }

    [Fact]
    public async Task ArbitraryExceptionAndNamespaceStringsAreNotEchoed()
    {
        var analysis = await AnalyzeAsync("""{"exception":{"type":"SECRET","signal":"SECRET"},"termination":{"namespace":"SECRET","code":"SECRET"}}""");
        Assert.DoesNotContain("SECRET", analysis.ToRedactedText());
        Assert.Equal("Unrecognized or missing crash signature", analysis.Category);
    }

    [Fact]
    public async Task StringTerminationCodeDoesNotTriggerWatchdog()
    {
        var analysis = await AnalyzeAsync("""{"termination":{"namespace":"FRONTBOARD","code":"2343432205"}}""");
        Assert.Equal("Unrecognized or missing crash signature", analysis.Category);
    }

    [Fact]
    public async Task InputStreamRemainsOpen()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"bug_type\":\"309\"}\n{}"));
        await IphoneCrashReportAnalyzer.AnalyzeAsync(stream);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task DocumentedSyntheticFixtureHasExpectedRedactedResult()
    {
        using var stream = typeof(IphoneCrashReportAnalyzerTests).Assembly.GetManifestResourceStream("SyntheticCrashReport")!;
        var analysis = await IphoneCrashReportAnalyzer.AnalyzeAsync(stream);
        Assert.Equal("Invalid memory access signature", analysis.Category);
        Assert.DoesNotContain("SYNTHETIC-NOT-A-REAL", analysis.ToRedactedText());
    }

    [Fact]
    public async Task ValidReportAtExactByteLimitAccepted()
    {
        const string report = "{\"bug_type\":\"309\"}\n{}";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(report.PadRight(IphoneCrashReportAnalyzer.MaximumBytes)));
        Assert.Equal("Unrecognized or missing crash signature", (await IphoneCrashReportAnalyzer.AnalyzeAsync(stream)).Category);
    }

    [Fact]
    public async Task ShortReadsDoNotTruncateReport()
    {
        using var stream = new ShortReadStream(Encoding.UTF8.GetBytes("{\"bug_type\":\"309\"}\n{\"exception\":{\"type\":\"EXC_RESOURCE\"}}"));
        Assert.Equal("Resource-limit signature", (await IphoneCrashReportAnalyzer.AnalyzeAsync(stream)).Category);
    }

    [Fact]
    public async Task InvalidUtf8EvenInIgnoredValueIsRejected()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"bug_type\":\"309\"}\n{\"ignored\":\"X\"}");
        bytes[Array.IndexOf(bytes, (byte)'X')] = 0xff;
        using var stream = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => IphoneCrashReportAnalyzer.AnalyzeAsync(stream));
    }

    private sealed class ShortReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancellationToken);
    }
}

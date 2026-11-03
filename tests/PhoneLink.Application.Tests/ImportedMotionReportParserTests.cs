using System.Text;
using System.Text.Json.Nodes;
using PhoneLink.Application.Features.Diagnostics;

namespace PhoneLink.Application.Tests;

public sealed class ImportedMotionReportParserTests
{
    private const string Fixture = """
        {"schemaVersion":1,"capturedAt":"2026-11-03T00:00:00.123Z","method":"PRIVATE-METHOD-CANARY","limitation":"PRIVATE-ADVICE-CANARY",
         "sensors":[
          {"sensor":"Accelerometer","source":"CMMotionManager.accelerometerData","units":"g (includes gravity)","outcome":"Reported",
           "summary":{"sampleCount":2,"x":{"minimum":-1,"maximum":1},"y":{"minimum":0,"maximum":2},"z":{"minimum":3,"maximum":4}}},
          {"sensor":"Gyroscope","source":"CMMotionManager.gyroData","units":"radians/second","outcome":"Unknown"},
          {"sensor":"Magnetometer","source":"CMMotionManager.magnetometerData","units":"microtesla (uncalibrated field)","outcome":"Unavailable","summary":null}]}
        """;

    private static async Task<ImportedMotionReport> Parse(string text)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return await ImportedMotionReportParser.ParseAsync(stream);
    }

    [Fact]
    public async Task ReadsCompanionSchemaWithoutUpgradingFileClaimsToLiveEvidence()
    {
        var result = await Parse(Fixture);
        Assert.Equal(3, result.Sensors.Count);
        Assert.Equal(2, result.Sensors[0].Summary?.SampleCount);
        Assert.Null(result.Sensors[1].Summary);
        Assert.Contains("unverified file origin", result.ToDisplayText());
        Assert.Contains("not an authenticated live measurement", result.ToDisplayText());
        Assert.Contains("not calibration", result.ToDisplayText());
        Assert.DoesNotContain("CANARY", result.ToDisplayText());
        Assert.Equal("Unknown", result.Sensors[1].FileOutcome);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("timestamp")]
    [InlineData("unknown-root")]
    [InlineData("unknown-sensor")]
    [InlineData("duplicate-sensor")]
    [InlineData("source")]
    [InlineData("units")]
    [InlineData("outcome")]
    [InlineData("missing-summary")]
    [InlineData("unknown-with-summary")]
    [InlineData("too-many-samples")]
    [InlineData("zero-samples")]
    [InlineData("fractional-count")]
    [InlineData("reversed-range")]
    [InlineData("raw-samples")]
    [InlineData("missing-sensor")]
    [InlineData("object-not-string")]
    public async Task RejectsAmbiguousOrInconsistentClaims(string mutation)
    {
        var root = JsonNode.Parse(Fixture)!.AsObject();
        var sensors = root["sensors"]!.AsArray();
        var first = sensors[0]!.AsObject();
        switch (mutation)
        {
            case "version": root["schemaVersion"] = 2; break;
            case "timestamp": root["capturedAt"] = "not-a-time"; break;
            case "unknown-root": root["deviceIdentifier"] = "PRIVATE"; break;
            case "unknown-sensor": first["sensor"] = "PRIVATE"; break;
            case "duplicate-sensor": sensors[1] = first.DeepClone(); break;
            case "source": first["source"] = "UNTRUSTED-SOURCE"; break;
            case "units": first["units"] = "degrees"; break;
            case "outcome": first["outcome"] = "Verified"; break;
            case "missing-summary": first.Remove("summary"); break;
            case "unknown-with-summary": first["outcome"] = "Unknown"; break;
            case "too-many-samples": first["summary"]!["sampleCount"] = 121; break;
            case "zero-samples": first["summary"]!["sampleCount"] = 0; break;
            case "fractional-count": first["summary"]!["sampleCount"] = 1.5; break;
            case "reversed-range": first["summary"]!["x"]!["minimum"] = 100; break;
            case "raw-samples": first["summary"]!["samples"] = new JsonArray(1, 2, 3); break;
            case "missing-sensor": sensors.RemoveAt(2); break;
            case "object-not-string": root["method"] = new JsonObject(); break;
        }
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Parse(root.ToJsonString()));
        Assert.DoesNotContain("PRIVATE", error.Message);
    }

    [Theory]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1")]
    [InlineData("\"minimum\":-1", "\"minimum\":-1,\"minimum\":-1")]
    [InlineData("\"minimum\":-1", "\"minimum\":1e400")]
    [InlineData("\"outcome\":\"Reported\"", "\"outcome\":null")]
    public async Task RejectsDuplicateKeysNonFiniteNumbersAndWrongKinds(string before, string after)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Parse(Fixture.Replace(before, after, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RejectsTrailingDocuments()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Parse(Fixture + "{}"));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task EnforcesExactByteBoundOnNonSeekablePartialReads(int extra, bool accepted)
    {
        var bytes = Encoding.UTF8.GetBytes(Fixture.PadRight(ImportedMotionReportParser.MaximumBytes + extra));
        using var stream = new ChunkedStream(bytes);
        if (accepted) Assert.Equal(3, (await ImportedMotionReportParser.ParseAsync(stream)).Sensors.Count);
        else await Assert.ThrowsAsync<InvalidDataException>(() => ImportedMotionReportParser.ParseAsync(stream));
        Assert.Equal(ImportedMotionReportParser.MaximumBytes + extra, stream.Position);
    }

    [Fact]
    public async Task HonorsCancellationBeforeReading()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Fixture));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImportedMotionReportParser.ParseAsync(stream, cancellation.Token));
    }

    private sealed class ChunkedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancellationToken);
    }
}

using System.Globalization;
using System.Text.Json;

namespace PhoneLink.Application.Features.Diagnostics;

public sealed record ImportedMotionAxis(double Minimum, double Maximum);
public sealed record ImportedMotionSummary(int SampleCount, ImportedMotionAxis X, ImportedMotionAxis Y, ImportedMotionAxis Z);
public sealed record ImportedMotionSensor(string Sensor, string Source, string Units, string FileOutcome, ImportedMotionSummary? Summary);

public sealed record ImportedMotionReport(DateTimeOffset CapturedAt, IReadOnlyList<ImportedMotionSensor> Sensors)
{
    public string ToDisplayText() => string.Join(Environment.NewLine,
        new[] { "Imported motion summary — unverified file origin, not an authenticated live measurement.",
            $"File-reported capture time: {CapturedAt.ToString("O", CultureInfo.InvariantCulture)}.",
            "Source and status below are claims from a user-selected file. Ranges are not calibration, hardware health, a fault diagnosis or proof of repair." }
        .Concat(Sensors.Select(sensor => $"{sensor.Sensor} · file status: {sensor.FileOutcome}\nClaimed source: {sensor.Source}; {sensor.Units}\n" +
            (sensor.Summary is { } summary ? $"{summary.SampleCount} claimed fresh samples; X {Axis(summary.X)}; Y {Axis(summary.Y)}; Z {Axis(summary.Z)}."
                : "No sample range available; no sensor failure inferred."))));

    private static string Axis(ImportedMotionAxis axis) =>
        $"{axis.Minimum.ToString("G6", CultureInfo.InvariantCulture)} … {axis.Maximum.ToString("G6", CultureInfo.InvariantCulture)}";
}

/// <summary>Schema-1 companion summary only; never retains arbitrary metadata, filenames or raw paths.</summary>
public static class ImportedMotionReportParser
{
    public const int MaximumBytes = 8 * 1024;

    public static async Task<ImportedMotionReport> ParseAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var bytes = new byte[MaximumBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (length > MaximumBytes) throw Invalid();
        try
        {
            using var document = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 8 });
            var root = Object(document.RootElement, ["schemaVersion", "capturedAt", "method", "limitation", "sensors"]);
            if (!root["schemaVersion"].TryGetInt32(out var version) || version != 1 ||
                root["capturedAt"].ValueKind != JsonValueKind.String || !root["capturedAt"].TryGetDateTimeOffset(out var capturedAt)) throw Invalid();
            _ = Text(root["method"], 1024);
            _ = Text(root["limitation"], 2048); // Validate but do not retain/render peer-authored advice.
            var sensors = root["sensors"];
            if (sensors.ValueKind != JsonValueKind.Array || sensors.GetArrayLength() != 3) throw Invalid();
            List<ImportedMotionSensor> observations = [];
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (var item in sensors.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = Object(item, ["sensor", "source", "units", "outcome"], ["summary"]);
                var name = Text(values["sensor"], 32);
                var (source, units) = name switch
                {
                    "Accelerometer" => ("CMMotionManager.accelerometerData", "g (includes gravity)"),
                    "Gyroscope" => ("CMMotionManager.gyroData", "radians/second"),
                    "Magnetometer" => ("CMMotionManager.magnetometerData", "microtesla (uncalibrated field)"),
                    _ => throw Invalid(),
                };
                if (!names.Add(name) || Text(values["source"], 64) != source || Text(values["units"], 64) != units) throw Invalid();
                var outcome = Text(values["outcome"], 16);
                if (outcome is not ("Reported" or "Unavailable" or "Unknown")) throw Invalid();
                ImportedMotionSummary? summary = null;
                if (values.TryGetValue("summary", out var raw) && raw.ValueKind != JsonValueKind.Null)
                {
                    if (outcome != "Reported") throw Invalid();
                    var fields = Object(raw, ["sampleCount", "x", "y", "z"]);
                    if (!fields["sampleCount"].TryGetInt32(out var count) || count is < 1 or > 120) throw Invalid();
                    summary = new(count, Axis(fields["x"]), Axis(fields["y"]), Axis(fields["z"]));
                }
                if (outcome == "Reported" && summary is null) throw Invalid();
                observations.Add(new(name, source, units, outcome, summary));
            }
            return new(capturedAt, observations.ToArray());
        }
        catch (JsonException) { throw Invalid(); }
        catch (InvalidOperationException) { throw Invalid(); }
    }

    private static ImportedMotionAxis Axis(JsonElement value)
    {
        var fields = Object(value, ["minimum", "maximum"]);
        if (!fields["minimum"].TryGetDouble(out var minimum) || !fields["maximum"].TryGetDouble(out var maximum) ||
            !double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum > maximum) throw Invalid();
        return new(minimum, maximum);
    }

    private static Dictionary<string, JsonElement> Object(JsonElement value, string[] required, string[]? optional = null)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        Dictionary<string, JsonElement> fields = new(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if ((!required.Contains(property.Name, StringComparer.Ordinal) && optional?.Contains(property.Name, StringComparer.Ordinal) != true) ||
                !fields.TryAdd(property.Name, property.Value)) throw Invalid();
        if (required.Any(name => !fields.ContainsKey(name))) throw Invalid();
        return fields;
    }

    private static string Text(JsonElement value, int maximumLength)
    {
        if (value.ValueKind != JsonValueKind.String) throw Invalid();
        var text = value.GetString()!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximumLength) throw Invalid();
        return text;
    }

    private static InvalidDataException Invalid() => new("The selected file is not a supported bounded schema-1 motion summary.");
}

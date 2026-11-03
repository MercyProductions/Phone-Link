using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PhoneLink.Application.Features.Diagnostics;

public sealed record IphoneCrashAnalysis(string Category, string Evidence, string NextStep)
{
    public string ToRedactedText() => $"Phone Link — local crash-report analysis\n{Category}\n{Evidence}\n{NextStep}\n" +
        "No app names, timestamps, paths, incident/device identifiers, stack traces or original log content included. " +
        "This is not a hardware diagnosis. No phone change or repair was performed.";
}

/// <summary>Analyzes only documented type-309 IPS JSON. Never retains or echoes arbitrary report strings.</summary>
public static class IphoneCrashReportAnalyzer
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const string GeneralAdvice = "Preserve the original report privately and reproduce the symptom if safe. " +
        "Check for app/iOS updates and share the original only with a support recipient you choose. " +
        "A report category alone does not establish the cause or justify a reset, restore or part replacement.";

    public static async Task<IphoneCrashAnalysis> AnalyzeAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > MaximumBytes) throw new InvalidDataException("Report exceeds the 4 MiB analysis limit.");
            buffer.Write(chunk, 0, count);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Analyze(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)), cancellationToken);
    }

    private static IphoneCrashAnalysis Analyze(ReadOnlySpan<byte> bytes, CancellationToken token)
    {
        if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bytes = bytes[3..];
        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
            token.ThrowIfCancellationRequested();
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 64, AllowMultipleValues = true });
            using var header = JsonDocument.ParseValue(ref reader);
            ValidateObject(header.RootElement, token);
            if (!header.RootElement.TryGetProperty("bug_type", out var bugType) ||
                bugType.ValueKind != JsonValueKind.String || bugType.GetString() != "309")
                throw new InvalidDataException("Only documented type-309 JSON crash reports are supported. Panic, Jetsam, stackshot and translated text reports require separate analysis.");
            if (!reader.Read()) throw new InvalidDataException("The crash report body is missing.");
            using var body = JsonDocument.ParseValue(ref reader);
            ValidateObject(body.RootElement, token);
            if (reader.Read()) throw new InvalidDataException("Unexpected data follows the crash report.");

            // Nonfatal/simulated reports must not be described as actual crashes.
            foreach (var name in new[] { "isNonFatal", "isSimulated" })
                if (body.RootElement.TryGetProperty(name, out var flag) && flag.ValueKind != JsonValueKind.False &&
                    !(flag.ValueKind == JsonValueKind.String && flag.GetString() == "false"))
                    return new("Nonfatal, simulated or ambiguous report", "This report is not confirmed to describe an actual process crash.", GeneralAdvice);

            var exception = ObjectProperty(body.RootElement, "exception");
            var type = KnownString(exception, "type", ["EXC_BAD_ACCESS", "EXC_CRASH", "EXC_RESOURCE", "EXC_GUARD", "EXC_BREAKPOINT", "EXC_BAD_INSTRUCTION", "EXC_ARITHMETIC"]);
            var signal = KnownString(exception, "signal", ["SIGSEGV", "SIGBUS", "SIGABRT", "SIGKILL", "SIGQUIT", "SIGSYS", "SIGTERM", "SIGTRAP", "SIGILL", "SIGFPE"]);
            var termination = ObjectProperty(body.RootElement, "termination");
            var space = KnownString(termination, "namespace", ["FRONTBOARD", "RUNNINGBOARD", "SPRINGBOARD", "SIGNAL", "DYLD", "CODESIGNING"]);
            var code = termination.ValueKind == JsonValueKind.Object && termination.TryGetProperty("code", out var value) &&
                value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var number) ? (ulong?)number : null;
            var evidence = $"Type: {type ?? "unknown/unrecognized"}; signal: {signal ?? "unknown/unrecognized"}; " +
                $"termination namespace: {space ?? "unknown/unrecognized"}; code: {(code is { } c ? "0x" + c.ToString("x", CultureInfo.InvariantCulture) : "unknown")}.";
            var category = code == 0x8badf00d && space is "FRONTBOARD" or "RUNNINGBOARD" or "SPRINGBOARD"
                ? "Watchdog termination signature" : type switch
                {
                    "EXC_BAD_ACCESS" => "Invalid memory access signature",
                    "EXC_RESOURCE" => "Resource-limit signature",
                    "EXC_GUARD" => "Guarded-resource violation signature",
                    "EXC_BREAKPOINT" or "EXC_BAD_INSTRUCTION" => "Trap or invalid-instruction signature",
                    "EXC_ARITHMETIC" => "Arithmetic exception signature",
                    "EXC_CRASH" when signal == "SIGABRT" => "Abort signal signature",
                    "EXC_CRASH" => "Process termination; cause unresolved",
                    _ => "Unrecognized or missing crash signature",
                };
            var guidance = category switch
            {
                "Watchdog termination signature" => "The signature indicates a responsiveness deadline was missed. Note what you were doing when the app stopped responding; the responsible developer needs timing and stack evidence. ",
                "Invalid memory access signature" => "A process memory access failed; this does not mean the phone's physical memory is defective. A developer needs symbolicated stack frames to locate the software issue. ",
                "Resource-limit signature" => "The process hit a resource limit. This field alone does not identify which limit or establish low storage, bad battery or defective memory. Detailed resource evidence is needed. ",
                "Abort signal signature" => "An abort signal ended the process. The signal alone does not identify an uncaught exception, missing framework or hardware problem. The original diagnostic sections need developer review. ",
                "Trap or invalid-instruction signature" => "A trap or instruction exception was reported. Swift runtime assertions are one possible source, not a conclusion without the stack and diagnostic context. ",
                "Guarded-resource violation signature" => "A protected resource rule was violated. Developer review of the full exception details is needed; this does not indicate a device lock or an antivirus problem. ",
                "Arithmetic exception signature" => "An arithmetic exception was reported. A developer needs the failing instruction and stack context; this is not evidence of a damaged processor. ",
                _ => "The recognized fields do not establish a specific cause. Do not infer a hardware fault or a successful repair. ",
            };
            return new(category, evidence, guidance + GeneralAdvice);
        }
        catch (JsonException)
        {
            // Parser messages can include arbitrary source content; never propagate them to UI/logs.
            throw new InvalidDataException("Report is malformed, truncated, too deeply nested or not supported IPS JSON.");
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("Report is not valid UTF-8.");
        }
    }

    private static JsonElement ObjectProperty(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : default;

    private static string? KnownString(JsonElement parent, string name, string[] allowed) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String && allowed.Contains(value.GetString(), StringComparer.Ordinal) ? value.GetString() : null;

    private static void ValidateObject(JsonElement root, CancellationToken token)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("IPS metadata and body must be JSON objects.");
        Validate(root, token);
    }

    private static void Validate(JsonElement element, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Ambiguous duplicate JSON properties are not supported.");
                Validate(property.Value, token);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) Validate(child, token);
    }
}

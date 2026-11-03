using System.Globalization;
using System.Text.Json;

namespace PhoneLink.Application.Features.BinaryInspection;

public sealed record SnapshotMappedOffset(MachOOffsetEntry StaticEntry, string SnapshotDerivedAddress);
public sealed record CompanionRuntimeMapping(DateTimeOffset ReportedCaptureTime, string ImageUuid,
    string Architecture, bool SnapshotOriginAuthenticated, bool LiveProcessVerified, bool EntriesTruncated,
    IReadOnlyList<SnapshotMappedOffset> TextEntries, string Warning)
{
    public string? ExactSymbolName { get; init; }
}

public static class CompanionRuntimeMapper
{
    public const int MaximumSnapshotBytes = 8192;
    private static readonly HashSet<string> Keys = new(StringComparer.Ordinal)
    {
        "schemaVersion", "capturedAt", "scope", "imageUUID", "cpuType", "cpuSubtype", "preferredTextAddress",
        "loadedHeaderAddress", "textVirtualSize", "slide", "markerRuntimeAddress", "markerImageOffset",
    };

    public static CompanionRuntimeMapping Map(MachOInspection inspection, ReadOnlyMemory<byte> json, string? exactSymbolName = null)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        if (exactSymbolName is not null && (exactSymbolName.Length is 0 or > 4096 || exactSymbolName.Any(char.IsControl)))
            throw new InvalidDataException("Choose an exact symbol name up to 4,096 characters without control characters.");
        if (json.Length > MaximumSnapshotBytes) throw new InvalidDataException("Runtime snapshot exceeds 8 KiB.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!Keys.Contains(property.Name) || !seen.Add(property.Name)) throw Invalid();
            if (!seen.SetEquals(Keys) || root.GetProperty("schemaVersion").GetInt32() != 1 ||
                Text(root, "scope") != "companion-own-marker-image" ||
                !Guid.TryParseExact(Text(root, "imageUUID"), "D", out var uuid) ||
                !DateTimeOffset.TryParse(Text(root, "capturedAt"), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var capturedAt) || capturedAt == default) throw Invalid();
            var cpu = root.GetProperty("cpuType").GetUInt32();
            var subtype = root.GetProperty("cpuSubtype").GetUInt32();
            var architecture = cpu switch
            {
                0x100000c => (subtype & 0xffffff) == 2 ? "arm64e" : "arm64",
                0x1000007 => "x86_64",
                _ => throw Invalid(),
            };
            var preferred = Hex(Text(root, "preferredTextAddress"));
            var loaded = Hex(Text(root, "loadedHeaderAddress"));
            var size = Hex(Text(root, "textVirtualSize"));
            var marker = Hex(Text(root, "markerRuntimeAddress"));
            var markerOffset = Hex(Text(root, "markerImageOffset"));
            if (size < 32 || markerOffset >= size || checked(loaded + markerOffset) != marker ||
                loaded == 0 || checked(loaded + size) <= loaded || checked(preferred + size) <= preferred) throw Invalid();
            var slide = loaded >= preferred ? Format(loaded - preferred) : "-" + Format(preferred - loaded);
            if (Text(root, "slide") != slide) throw Invalid();

            var images = inspection.Images.Where(image =>
                Guid.TryParseExact(image.Uuid, "D", out var imageUuid) && imageUuid == uuid &&
                image.Architecture == architecture && Hex(image.CpuSubtype) == subtype).ToArray();
            if (images.Length != 1) throw new InvalidDataException("Runtime UUID/architecture must match exactly one inspected image.");
            var image = images[0];
            var segments = image.Segments.Where(segment => segment.Name == "__TEXT").ToArray();
            if (segments.Length != 1 || Hex(segments[0].SliceRelativeFileOffset) != 0 ||
                Hex(segments[0].FileSize) == 0 || Hex(segments[0].VirtualAddress) != preferred ||
                Hex(segments[0].VirtualSize) != size || Hex(image.ImageBase) != preferred)
                throw new InvalidDataException("Runtime text layout does not match the inspected image.");
            List<SnapshotMappedOffset> entries = [];
            var truncated = image.EntriesTruncated;
            foreach (var entry in image.Entries)
            {
                if (exactSymbolName is not null && !string.Equals(entry.Name, exactSymbolName, StringComparison.Ordinal)) continue;
                if (entry.VirtualAddress is null) continue;
                var address = Hex(entry.VirtualAddress);
                if (address < preferred || address - preferred >= size) continue;
                if (entries.Count == 4096) { truncated = true; break; }
                entries.Add(new(entry, Format(checked(loaded + (address - preferred)))));
            }
            return new(capturedAt, uuid.ToString("D"), architecture, false, false, truncated, entries,
                "Addresses are derived from a user-supplied snapshot, not authenticated or verified live. UUID/layout matching does not establish origin or freshness. Only this image's text range is mapped; restart/reload invalidates addresses. No memory was read or written, and no address was executed.")
                { ExactSymbolName = exactSymbolName };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException)
        { throw Invalid(); }
    }

    private static string Text(JsonElement root, string key) => root.GetProperty(key).GetString() ?? throw Invalid();
    private static ulong Hex(string value)
    {
        if (value.Length is < 3 or > 18 || !value.StartsWith("0x", StringComparison.Ordinal) ||
            !ulong.TryParse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var number)) throw Invalid();
        return number;
    }
    private static string Format(ulong value) => "0x" + value.ToString("x", CultureInfo.InvariantCulture);
    private static InvalidDataException Invalid() => new("Runtime snapshot is malformed, inconsistent or unsupported; no address mapping was produced.");
}

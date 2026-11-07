using System.Buffers;
using System.Globalization;

namespace PhoneLink.Application.Features.BinaryInspection;

public enum MachOFileOffsetOutcome { Mapped, OutsideFile, OutsideImageSlice, NotMapped, AmbiguousSegment, UnsupportedLayout }

public sealed record MachOFileOffsetResult(
    MachOFileOffsetOutcome Outcome, string FileOffset, string? SliceRelativeOffset = null,
    string? Architecture = null, string? ImageUuid = null, string? Segment = null, string? Section = null,
    bool SectionAmbiguous = false, string? UnslidVirtualAddress = null, string? ImageRelativeOffset = null);

/// <summary>Reverse-maps outer-file bytes against an already inspected Mach-O, never live memory.</summary>
public static class MachOFileOffsetLookup
{
    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdefABCDEF");

    public static bool TryParseHex(string? text, out ulong offset)
    {
        offset = 0;
        return text is { Length: >= 3 and <= 18 } && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            text.AsSpan(2).IndexOfAnyExcept(HexDigits) < 0 &&
            ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out offset);
    }

    public static MachOFileOffsetResult Lookup(MachOInspection report, ulong offset)
    {
        ArgumentNullException.ThrowIfNull(report);
        var file = Hex(offset);
        if (report.ByteLength <= 0 || offset >= (ulong)report.ByteLength)
            return new(MachOFileOffsetOutcome.OutsideFile, file);
        var image = report.Images.FirstOrDefault(candidate => candidate.SliceByteLength > 0 &&
            InRange(offset, Number(candidate.SliceFileOffset), (ulong)candidate.SliceByteLength));
        if (image is null) return new(MachOFileOffsetOutcome.OutsideImageSlice, file);
        var result = new MachOFileOffsetResult(MachOFileOffsetOutcome.NotMapped, file,
            Hex(offset - Number(image.SliceFileOffset)), image.Architecture, image.Uuid);
        var candidates = image.Segments.Where(segment =>
            InRange(offset, Number(segment.FileOffset), Number(segment.FileSize))).Take(2).ToArray();
        if (candidates.Length == 0) return result;
        if (candidates.Length > 1) return result with { Outcome = MachOFileOffsetOutcome.AmbiguousSegment };

        var selected = candidates[0];
        var displacement = offset - Number(selected.FileOffset);
        var vmStart = Number(selected.VirtualAddress);
        // SG_HIGHVM puts the file at the high end of a segment. Do not apply the normal low-end formula.
        if ((selected.Flags & 1) != 0 || displacement >= Number(selected.VirtualSize) || displacement > ulong.MaxValue - vmStart)
            return result with { Outcome = MachOFileOffsetOutcome.UnsupportedLayout, Segment = selected.Name };
        var va = vmStart + displacement;
        if (image.Segments.Any(segment => !ReferenceEquals(segment, selected) &&
            InRange(va, Number(segment.VirtualAddress), Number(segment.VirtualSize))))
            return result with { Outcome = MachOFileOffsetOutcome.AmbiguousSegment };

        var sections = image.Sections.Where(section => section.FileOffset is { } start &&
            InRange(offset, Number(start), Number(section.ByteSize)) &&
            InRange(va, Number(section.VirtualAddress), Number(section.ByteSize)) &&
            offset - Number(start) == va - Number(section.VirtualAddress)).Take(2).ToArray();
        var imageBase = Number(image.ImageBase);
        return result with
        {
            Outcome = MachOFileOffsetOutcome.Mapped,
            Segment = selected.Name,
            Section = sections.Length == 1 ? sections[0].Name : null,
            SectionAmbiguous = sections.Length > 1,
            UnslidVirtualAddress = Hex(va),
            ImageRelativeOffset = va >= imageBase ? Hex(va - imageBase) : null,
        };
    }

    private static bool InRange(ulong value, ulong start, ulong length) => value >= start && value - start < length;
    private static ulong Number(string hex) => ulong.Parse(hex.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    private static string Hex(ulong value) => "0x" + value.ToString("x", CultureInfo.InvariantCulture);
}

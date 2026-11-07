using System.Globalization;

namespace PhoneLink.Application.Features.BinaryInspection;

public enum MachOVirtualAddressOutcome { Mapped, NotMapped, NoFileBacking, AmbiguousSegment, AmbiguousSection, UnsupportedLayout }

public sealed record MachOVirtualAddressResult(MachOVirtualAddressOutcome Outcome, int SliceIndex, string Architecture,
    string? ImageUuid, string UnslidVirtualAddress, string? ImageRelativeOffset, string? Segment = null,
    string? Section = null, string? FileOffset = null, string? SliceRelativeOffset = null);

/// <summary>Maps an explicitly unslid address in one inspected image, never a live process address.</summary>
public static class MachOVirtualAddressLookup
{
    public static MachOVirtualAddressResult Lookup(MachOInspection report, int sliceIndex, ulong address)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (sliceIndex < 0 || sliceIndex >= report.Images.Count) throw new ArgumentOutOfRangeException(nameof(sliceIndex));
        var image = report.Images[sliceIndex];
        var imageBase = Number(image.ImageBase);
        var result = new MachOVirtualAddressResult(MachOVirtualAddressOutcome.NotMapped, sliceIndex, image.Architecture,
            image.Uuid, Hex(address), address >= imageBase ? Hex(address - imageBase) : null);
        var segments = image.Segments.Where(segment =>
            Contains(address, Number(segment.VirtualAddress), Number(segment.VirtualSize))).Take(2).ToArray();
        if (segments.Length == 0) return result with { ImageRelativeOffset = null };
        if (segments.Length > 1) return result with { Outcome = MachOVirtualAddressOutcome.AmbiguousSegment };
        var segment = segments[0];
        result = result with { Segment = segment.Name };
        var fileLength = Number(segment.FileSize);
        if ((segment.Flags & 1) != 0 || fileLength > Number(segment.VirtualSize))
            return result with { Outcome = MachOVirtualAddressOutcome.UnsupportedLayout };

        var sections = image.Sections.Where(section => section.Segment == segment.Name &&
            Contains(address, Number(section.VirtualAddress), Number(section.ByteSize))).Take(2).ToArray();
        if (sections.Length > 1) return result with { Outcome = MachOVirtualAddressOutcome.AmbiguousSection };
        var section = sections.FirstOrDefault();
        var displacement = address - Number(segment.VirtualAddress);
        if (section is { FileOffset: null } || (section is null && displacement >= fileLength))
            return result with { Outcome = MachOVirtualAddressOutcome.NoFileBacking, Section = section?.Name };
        if (displacement >= fileLength) return result with { Outcome = MachOVirtualAddressOutcome.UnsupportedLayout };

        var fileStart = Number(segment.FileOffset);
        if (displacement > ulong.MaxValue - fileStart)
            return result with { Outcome = MachOVirtualAddressOutcome.UnsupportedLayout };
        var offset = fileStart + displacement;
        if (!Contains(offset, Number(image.SliceFileOffset), (ulong)image.SliceByteLength))
            return result with { Outcome = MachOVirtualAddressOutcome.UnsupportedLayout };
        var reverse = MachOFileOffsetLookup.Lookup(report, offset);
        if (reverse.Outcome == MachOFileOffsetOutcome.AmbiguousSegment)
            return result with { Outcome = MachOVirtualAddressOutcome.AmbiguousSegment };
        if (reverse.Outcome != MachOFileOffsetOutcome.Mapped || reverse.UnslidVirtualAddress != result.UnslidVirtualAddress ||
            (section is not null && (section.FileOffset is not { } sectionFileStart ||
                !Contains(offset, Number(sectionFileStart), Number(section.ByteSize)) ||
                offset - Number(sectionFileStart) != address - Number(section.VirtualAddress))))
            return result with { Outcome = MachOVirtualAddressOutcome.UnsupportedLayout };
        return result with { Outcome = MachOVirtualAddressOutcome.Mapped, Section = section?.Name,
            FileOffset = reverse.FileOffset, SliceRelativeOffset = reverse.SliceRelativeOffset };
    }

    private static bool Contains(ulong value, ulong start, ulong length) => value >= start && value - start < length;
    private static ulong Number(string value) => ulong.Parse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    private static string Hex(ulong value) => "0x" + value.ToString("x", CultureInfo.InvariantCulture);
}

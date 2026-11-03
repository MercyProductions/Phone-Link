namespace PhoneLink.Application.Features.BinaryInspection;

public sealed record MachOOffsetEntry(string Name, string Source, string Kind,
    string? VirtualAddress, string? ImageRelativeOffset, string? FileOffset, string? AbsoluteValue);
public sealed record MachOSegment(string Name, string VirtualAddress, string VirtualSize,
    string SliceRelativeFileOffset, string FileOffset, string FileSize);
public sealed record MachOSection(string Segment, string Name, string VirtualAddress, string ByteSize, string? FileOffset);
public sealed record MachOImage(string Architecture, string CpuSubtype, string? Uuid, uint FileType,
    string SliceFileOffset, long SliceByteLength, string ImageBase, bool EncryptedContent,
    uint DeclaredSymbolCount, bool EntriesTruncated, IReadOnlyList<MachOSegment> Segments,
    IReadOnlyList<MachOSection> Sections, IReadOnlyList<MachOOffsetEntry> Entries, IReadOnlyList<string> Warnings);
public sealed record MachOInspection(string Sha256, long ByteLength, bool RuntimeAddressesAvailable,
    IReadOnlyList<MachOImage> Images);

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PhoneLink.Application.Features.BinaryInspection;

public static partial class MachOInspector
{
    public static async Task<MachOInspection> InspectAsync(Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanSeek || !source.CanRead) throw new ArgumentException("A readable, seekable image stream is required.", nameof(source));
        if (source.Length is < 4 or > 8L * 1024 * 1024 * 1024)
            throw new InvalidDataException("The image length is outside the 4-byte–8-GiB inspection limit.");
        cancellationToken.ThrowIfCancellationRequested();
        source.Position = 0;
        var magic = new byte[4];
        await source.ReadExactlyAsync(magic, cancellationToken).ConfigureAwait(false);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(magic);
        if (value != 0xfeedfacf && BinaryPrimitives.ReadUInt32BigEndian(magic) is not (0xcafebabe or 0xcafebabf))
            throw new NotSupportedException("Select a thin little-endian 64-bit or universal Mach-O image, not an archive, compressed kernel, IPSW, or memory device.");
        source.Position = 0;
        var hash = await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => Parse(source, Convert.ToHexStringLower(hash), cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static MachOInspection Parse(Stream source, string hash, CancellationToken cancellationToken)
    {
        var slices = ReadSlices(source);
        List<MachOImage> images = [];
        var budget = new InspectionBudget();
        foreach (var slice in slices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            images.Add(new ImageReader(source, slice.Offset, slice.Size, budget, cancellationToken).Read(slice.Cpu, slice.Subtype));
        }
        return new(hash, source.Length, false, images);
    }

    private static List<(long Offset, long Size, uint? Cpu, uint? Subtype)> ReadSlices(Stream stream)
    {
        stream.Position = 0;
        var header = new byte[8];
        stream.ReadExactly(header.AsSpan(0, 4));
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) == 0xfeedfacf) return [(0, stream.Length, null, null)];
        stream.ReadExactly(header.AsSpan(4, 4));
        var fat64 = BinaryPrimitives.ReadUInt32BigEndian(header) == 0xcafebabf;
        var count = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
        if (count is 0 or > 64) throw new InvalidDataException("The universal image architecture count is invalid.");
        var recordSize = fat64 ? 32 : 20;
        var tableEnd = 8L + count * recordSize;
        if (tableEnd > stream.Length) throw new InvalidDataException("The universal image table is truncated.");
        List<(long Offset, long Size, uint? Cpu, uint? Subtype)> slices = [];
        for (var index = 0; index < count; index++)
        {
            var bytes = new byte[recordSize];
            stream.ReadExactly(bytes);
            var cpu = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            var subtype = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4));
            var offset = fat64 ? BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(8)) : BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8));
            var size = fat64 ? BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(16)) : BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(12));
            var align = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(fat64 ? 24 : 16));
            if (offset > (ulong)stream.Length || size < 32 || size > (ulong)stream.Length - offset || offset < (ulong)tableEnd ||
                align > 62 || offset % (1UL << (int)align) != 0 ||
                slices.Any(slice => offset < (ulong)(slice.Offset + slice.Size) && (ulong)slice.Offset < offset + size))
                throw new InvalidDataException("A universal image slice has invalid, overlapping, or unaligned bounds.");
            slices.Add(((long)offset, (long)size, cpu, subtype));
        }
        return slices;
    }

    private sealed class InspectionBudget { public int Count; public int NameBytes; public int LoadCommands; }

    private sealed partial class ImageReader(Stream source, long sliceOffset, long sliceLength,
        InspectionBudget budget, CancellationToken cancellationToken)
    {
        private const int MaximumEntries = 100_000;
        private readonly List<Segment> _segments = [];
        private readonly List<Section> _sections = [];
        private readonly List<MachOOffsetEntry> _entries = [];
        private readonly List<string> _warnings = [];
        private ulong _imageBase;
        private bool _truncated;

        public MachOImage Read(uint? expectedCpu, uint? expectedSubtype)
        {
            var header = Bytes(0, 32);
            if (U32(header, 0) != 0xfeedfacf)
                throw new NotSupportedException("A universal slice is not a supported little-endian 64-bit Mach-O image.");
            var cpu = U32(header, 4);
            var subtype = U32(header, 8);
            var fileType = U32(header, 12);
            if (fileType == 0x0c) throw new NotSupportedException("Mach-O fileset containers require per-subimage inspection; no kernel offsets are inferred.");
            if (expectedCpu is not null && (expectedCpu != cpu || expectedSubtype != subtype))
                throw new InvalidDataException("The universal slice architecture disagrees with its Mach-O header.");
            var commandCount = U32(header, 16);
            var commandBytes = U32(header, 20);
            if (commandCount > 65_536 || commandBytes > 16 * 1024 * 1024 || commandCount * 8UL > commandBytes)
                throw new InvalidDataException("The load-command table exceeds inspection limits.");
            var commands = Bytes(32, checked((int)commandBytes));
            string? uuid = null;
            bool encrypted = false;
            List<MachOLoadCommand> directory = [];
            List<MachOBuildTarget> targets = [];
            MachODeclaredSignature? signature = null;
            (uint Offset, uint Count, uint Strings, uint StringBytes)? symbols = null;
            (uint Offset, uint Bytes)? functions = null, exports = null;
            var position = 0;
            for (var index = 0; index < commandCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (commands.Length - position < 8) throw new InvalidDataException("A load command is truncated.");
                var command = commands.AsSpan(position);
                var kind = U32(command, 0);
                var size = U32(command, 4);
                if (size < 8 || size % 8 != 0 || size > commands.Length - position)
                    throw new InvalidDataException("A load command has invalid bounds or alignment.");
                command = command[..(int)size];
                if (budget.LoadCommands < 4096)
                {
                    directory.Add(new(Hex(kind), LoadCommandName(kind), Hex((ulong)sliceOffset + 32 + (uint)position), size));
                    budget.LoadCommands++;
                }
                switch (kind)
                {
                    case 0x19: ReadSegment(command); break;
                    case 0x1b:
                        Minimum(command, 24);
                        if (uuid is not null) throw new InvalidDataException("Duplicate image UUID commands are unsupported.");
                        uuid = new Guid(command.Slice(8, 16), bigEndian: true).ToString("D", CultureInfo.InvariantCulture);
                        break;
                    case 0x02:
                        Minimum(command, 24);
                        if (symbols is not null) throw new InvalidDataException("Duplicate symbol tables are unsupported.");
                        symbols = (U32(command, 8), U32(command, 12), U32(command, 16), U32(command, 20));
                        break;
                    case 0x26:
                        Minimum(command, 16);
                        functions = (U32(command, 8), U32(command, 12));
                        break;
                    case 0x1d:
                        if (command.Length != 16 || signature is not null)
                            throw new InvalidDataException("The code-signature range declaration is malformed or duplicated.");
                        var dataOffset = U32(command, 8);
                        var dataSize = U32(command, 12);
                        Range(dataOffset, dataSize);
                        if (dataSize > 0 && dataOffset < 32UL + commandBytes)
                            throw new InvalidDataException("The declared signature overlaps the Mach-O header or load commands.");
                        signature = new(Hex((ulong)sliceOffset + dataOffset), dataSize);
                        break;
                    case 0x32:
                        var target = ReadBuildTarget(command);
                        if (targets.Count >= 16 || targets.Any(existing => existing.PlatformId == target.PlatformId))
                            throw new InvalidDataException("Build target declarations exceed the limit or repeat a platform.");
                        targets.Add(target);
                        break;
                    case 0x80000033:
                        Minimum(command, 16);
                        exports = (U32(command, 8), U32(command, 12));
                        break;
                    case 0x22 or 0x80000022:
                        Minimum(command, 48);
                        if (exports is null) exports = (U32(command, 40), U32(command, 44));
                        break;
                    case 0x2c:
                        Minimum(command, 24);
                        encrypted |= U32(command, 16) != 0;
                        Range(U32(command, 8), U32(command, 12));
                        break;
                }
                position += (int)size;
            }
            if (position != commands.Length) throw new InvalidDataException("The command count does not consume its declared table.");
            _imageBase = _segments.Where(segment => segment.FileSize > 0 && segment.FileOffset == 0)
                .Select(segment => segment.Address).DefaultIfEmpty(_segments.Where(segment => segment.FileSize > 0)
                    .Select(segment => segment.Address).DefaultIfEmpty().Min()).Min();
            if (symbols is { } table) ReadSymbols(table.Offset, table.Count, table.Strings, table.StringBytes,
                fileType == 2 && (U32(header, 24) & 0x200000) != 0); // MH_EXECUTE + MH_PIE
            if (functions is { Bytes: > 0 } starts) ReadFunctionStarts(starts.Offset, starts.Bytes);
            if (exports is { Bytes: > 0 } trie) ReadExports(trie.Offset, trie.Bytes);
            if (uuid is null) _warnings.Add("No LC_UUID is present; do not assume this image matches a runtime binary or dSYM.");
            if (encrypted) _warnings.Add("Encrypted content is present. Structural offsets do not decrypt code or establish runtime addresses.");
            if (_truncated) _warnings.Add("Entry/name output budget reached; output is explicitly partial.");
            if (_entries.Count == 0) _warnings.Add("No supported named symbols, exports, or function starts were exposed; no offsets were guessed.");
            return new(cpu switch { 0x100000c => (subtype & 0x00ffffff) == 2 ? "arm64e" : "arm64", 0x1000007 => "x86_64", _ => Hex(cpu) },
                Hex(subtype), uuid, fileType, Hex((ulong)sliceOffset), sliceLength, Hex(_imageBase), encrypted,
                symbols?.Count ?? 0, _truncated,
                _segments.Select(segment => new MachOSegment(segment.Name, Hex(segment.Address), Hex(segment.Size), Hex(segment.FileOffset),
                    Hex((ulong)sliceOffset + segment.FileOffset), Hex(segment.FileSize)) { Flags = segment.Flags }).ToArray(),
                _sections.Select(section => new MachOSection(section.Segment, section.Name, Hex(section.Address), Hex(section.Size),
                    section.ZeroFill ? null : Hex((ulong)sliceOffset + section.FileOffset))).ToArray(), _entries, _warnings)
            {
                DeclaredLoadCommandCount = commandCount,
                LoadCommandsTruncated = directory.Count < commandCount,
                LoadCommands = directory,
                DeclaredBuildTargets = targets,
                DeclaredSignature = signature,
            };
        }

        private static MachOBuildTarget ReadBuildTarget(ReadOnlySpan<byte> command)
        {
            Minimum(command, 24);
            var toolCount = U32(command, 20);
            if (toolCount > 64 || 24 + toolCount * 8UL != (ulong)command.Length)
                throw new InvalidDataException("The build target's tool table has invalid bounds.");
            var platform = U32(command, 8);
            var sdk = U32(command, 16);
            var tools = new MachOBuildTool[toolCount];
            for (var index = 0; index < tools.Length; index++)
                tools[index] = new(U32(command, 24 + index * 8), U32(command, 28 + index * 8));
            return new(platform, platform switch
            {
                1 => "macOS", 2 => "iOS", 3 => "tvOS", 4 => "watchOS", 5 => "bridgeOS",
                6 => "Mac Catalyst", 7 => "iOS Simulator", 8 => "tvOS Simulator", 9 => "watchOS Simulator",
                10 => "DriverKit", _ => $"Unknown platform ({platform})",
            }, OsVersion(U32(command, 12)), sdk == 0 ? null : OsVersion(sdk), tools);
        }

        private static string OsVersion(uint value) =>
            FormattableString.Invariant($"{value >> 16}.{(value >> 8) & 255}.{value & 255}");

        private static string? LoadCommandName(uint kind) => kind switch
        {
            0x2 => "LC_SYMTAB", 0xb => "LC_DYSYMTAB", 0xc => "LC_LOAD_DYLIB", 0xd => "LC_ID_DYLIB",
            0xe => "LC_LOAD_DYLINKER", 0x19 => "LC_SEGMENT_64", 0x1b => "LC_UUID", 0x1d => "LC_CODE_SIGNATURE",
            0x22 => "LC_DYLD_INFO", 0x80000022 => "LC_DYLD_INFO_ONLY", 0x24 => "LC_VERSION_MIN_MACOSX",
            0x25 => "LC_VERSION_MIN_IPHONEOS", 0x26 => "LC_FUNCTION_STARTS", 0x80000028 => "LC_MAIN",
            0x29 => "LC_DATA_IN_CODE", 0x2a => "LC_SOURCE_VERSION", 0x2c => "LC_ENCRYPTION_INFO_64",
            0x2f => "LC_VERSION_MIN_TVOS", 0x30 => "LC_VERSION_MIN_WATCHOS", 0x32 => "LC_BUILD_VERSION",
            0x8000001c => "LC_RPATH", 0x80000033 => "LC_DYLD_EXPORTS_TRIE", 0x80000034 => "LC_DYLD_CHAINED_FIXUPS",
            _ => null,
        };

        private void ReadSegment(ReadOnlySpan<byte> command)
        {
            Minimum(command, 72);
            var count = U32(command, 64);
            if (count > 4096 || _sections.Count + count > 4096 || 72 + count * 80UL > (ulong)command.Length)
                throw new InvalidDataException("A segment section table exceeds its bounds.");
            var segment = new Segment(FixedName(command.Slice(8, 16)), U64(command, 24), U64(command, 32), U64(command, 40), U64(command, 48), U32(command, 68));
            Range(segment.FileOffset, segment.FileSize);
            AddressEnd(segment.Address, segment.Size);
            _segments.Add(segment);
            for (var index = 0; index < count; index++)
            {
                var bytes = command.Slice(72 + index * 80, 80);
                var type = U32(bytes, 64) & 0xff;
                var section = new Section(FixedName(bytes.Slice(16, 16)), FixedName(bytes[..16]),
                    U64(bytes, 32), U64(bytes, 40), U32(bytes, 48), type is 1 or 0x0c or 0x12);
                var end = AddressEnd(section.Address, section.Size);
                if (section.Address < segment.Address || end > AddressEnd(segment.Address, segment.Size))
                    throw new InvalidDataException("A section escaped its segment's virtual range.");
                if (!section.ZeroFill && section.Size > 0)
                {
                    Range(section.FileOffset, section.Size);
                    if (section.FileOffset < segment.FileOffset || section.Size > segment.FileSize ||
                        section.FileOffset - segment.FileOffset > segment.FileSize - section.Size)
                        throw new InvalidDataException("A section escaped its segment's file range.");
                }
                _sections.Add(section);
            }
        }

        private byte[] Bytes(ulong offset, int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Range(offset, (ulong)count);
            source.Position = checked(sliceOffset + (long)offset);
            var bytes = new byte[count];
            source.ReadExactly(bytes);
            return bytes;
        }

        private void Range(ulong offset, ulong size)
        {
            if (offset > (ulong)sliceLength || size > (ulong)sliceLength - offset)
                throw new InvalidDataException("An image range escaped its selected file slice.");
        }

        private bool Add(MachOOffsetEntry entry)
        {
            var nameBytes = Encoding.UTF8.GetByteCount(entry.Name);
            if (budget.Count >= MaximumEntries || nameBytes > 16 * 1024 * 1024 - budget.NameBytes)
            { _truncated = true; return false; }
            budget.NameBytes += nameBytes;
            budget.Count++;
            _entries.Add(entry);
            return true;
        }

        private MachOOffsetEntry AddressEntry(string name, string origin, string kind, ulong address)
        {
            var mappings = _segments.Where(segment => address >= segment.Address && address - segment.Address < segment.FileSize).ToArray();
            var file = mappings.Length == 1 ? Hex((ulong)sliceOffset + mappings[0].FileOffset + address - mappings[0].Address) : null;
            return new(name, origin, kind, Hex(address), address >= _imageBase ? Hex(address - _imageBase) : null, file, null);
        }

        private static string FixedName(ReadOnlySpan<byte> bytes)
        {
            var zero = bytes.IndexOf((byte)0);
            return new UTF8Encoding(false, true).GetString(zero < 0 ? bytes : bytes[..zero]);
        }

        private static ulong AddressEnd(ulong start, ulong size) => ulong.MaxValue - start < size
            ? throw new InvalidDataException("A virtual-address range overflowed.") : start + size;
        private static void Minimum(ReadOnlySpan<byte> bytes, int size)
        { if (bytes.Length < size) throw new InvalidDataException("A known load command is truncated."); }
        private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
        private static ulong U64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
        private static string Hex(ulong value) => "0x" + value.ToString("x", CultureInfo.InvariantCulture);
        private sealed record Segment(string Name, ulong Address, ulong Size, ulong FileOffset, ulong FileSize, uint Flags);
        private sealed record Section(string Segment, string Name, ulong Address, ulong Size, ulong FileOffset, bool ZeroFill);
    }
}

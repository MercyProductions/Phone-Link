using System.Text;

namespace PhoneLink.Application.Features.BinaryInspection;

public static partial class MachOInspector
{
    private sealed partial class ImageReader
    {
        private void ReadSymbols(uint offset, uint count, uint stringOffset, uint stringBytes, bool pieExecutable)
        {
            if (count > 2_000_000 || stringBytes > 64 * 1024 * 1024)
                throw new InvalidDataException("The symbol/string table exceeds inspection limits.");
            var records = Bytes(offset, checked((int)count * 16));
            var strings = Bytes(stringOffset, (int)stringBytes);
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = records.AsSpan(index * 16, 16);
                var type = entry[4];
                if ((type & 0xe0) != 0 || U32(entry, 0) == 0) continue;
                var nameIndex = U32(entry, 0);
                if (nameIndex >= strings.Length) throw new InvalidDataException("A symbol name escaped its string table.");
                var position = (int)nameIndex;
                var name = CString(strings, ref position, strings.Length);
                var value = U64(entry, 8);
                var kind = type & 0x0e;
                MachOOffsetEntry result;
                if (kind == 0x0e)
                {
                    var sectionIndex = entry[5];
                    if (sectionIndex == 0 || sectionIndex > _sections.Count)
                        throw new InvalidDataException("A symbol references an invalid section ordinal.");
                    var section = _sections[sectionIndex - 1];
                    // Linkers assign this PIE header symbol N_SECT/section 1 even though the header precedes __text.
                    // Recognize only the exact header at a unique file-backed __TEXT base; do not relax normal symbols.
                    if (pieExecutable && type == 0x0f && name == "__mh_execute_header" && sectionIndex == 1 &&
                        section is { Segment: "__TEXT", Name: "__text", ZeroFill: false } && value == _imageBase && value < section.Address &&
                        _segments.Count(segment => segment.Name == "__TEXT" && segment.FileOffset == 0 && segment.Address == value &&
                            segment.FileSize >= 32 && segment.Size >= 32) == 1 &&
                        _segments.Count(segment => value >= segment.Address && value - segment.Address < segment.FileSize) == 1)
                        result = AddressEntry(name, "LC_SYMTAB", "image-header", value);
                    else
                    {
                        if (value < section.Address || value - section.Address > section.Size)
                            throw new InvalidDataException("A section symbol escaped its declared virtual range.");
                        var file = section.ZeroFill || value - section.Address == section.Size ? null
                            : Hex((ulong)sliceOffset + section.FileOffset + value - section.Address);
                        result = new(name, "LC_SYMTAB", (type & 1) != 0 ? "section/external" : "section/local", Hex(value),
                            value >= _imageBase ? Hex(value - _imageBase) : null, file, null);
                    }
                }
                else result = new(name, "LC_SYMTAB", kind switch { 0 => "undefined", 2 => "absolute", 0x0a => "indirect", _ => "unclassified" },
                    null, null, null, kind == 2 ? Hex(value) : null);
                if (!Add(result)) break;
            }
        }

        private void ReadFunctionStarts(uint offset, uint count)
        {
            if (count > 16 * 1024 * 1024) throw new InvalidDataException("The function-start table exceeds inspection limits.");
            var bytes = Bytes(offset, (int)count);
            var text = _segments.FirstOrDefault(segment => segment.Name == "__TEXT");
            if (text is null) { _warnings.Add("Function-start data has no __TEXT base; no function addresses were inferred."); return; }
            var address = text.Address;
            var position = 0;
            var ordinal = 0;
            while (position < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var delta = Uleb(bytes, ref position, bytes.Length);
                if (delta == 0)
                {
                    if (bytes.AsSpan(position).IndexOfAnyExcept((byte)0) >= 0)
                        throw new InvalidDataException("The function-start table has nonzero trailing data.");
                    return;
                }
                address = AddressEnd(address, delta);
                if (address < text.Address || address - text.Address >= text.Size)
                    throw new InvalidDataException("A function-start address escaped __TEXT.");
                if (!Add(AddressEntry($"function_{ordinal++}", "LC_FUNCTION_STARTS", "unnamed-function-start", address))) return;
            }
            throw new InvalidDataException("The function-start table has no terminator.");
        }

        private void ReadExports(uint offset, uint count)
        {
            if (count > 16 * 1024 * 1024) throw new InvalidDataException("The export trie exceeds inspection limits.");
            var bytes = Bytes(offset, (int)count);
            var stack = new Stack<(int Offset, string Prefix, int Depth)>();
            var visited = new HashSet<int>();
            stack.Push((0, "", 0));
            while (stack.TryPop(out var node))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (visited.Count >= 200_000 || node.Depth > 256)
                    throw new InvalidDataException("The export trie exceeds traversal limits.");
                if (node.Offset < 0 || node.Offset >= bytes.Length || !visited.Add(node.Offset))
                    throw new InvalidDataException("The export trie has an invalid, shared, or cyclic node reference.");
                var position = node.Offset;
                var terminalBytes = Uleb(bytes, ref position, bytes.Length);
                if (terminalBytes > (ulong)(bytes.Length - position)) throw new InvalidDataException("An export terminal escaped the trie.");
                var terminalEnd = position + (int)terminalBytes;
                if (terminalBytes > 0)
                {
                    var flags = Uleb(bytes, ref position, terminalEnd);
                    MachOOffsetEntry entry;
                    if ((flags & ~0x1fUL) != 0)
                    {
                        entry = new(node.Prefix, "EXPORT_TRIE", "unsupported-flags", null, null, null, null);
                        const string warning = "Some export flags are unsupported; those entries have no inferred address.";
                        if (!_warnings.Contains(warning, StringComparer.Ordinal)) _warnings.Add(warning);
                    }
                    else if ((flags & 8) != 0)
                    {
                        Uleb(bytes, ref position, terminalEnd);
                        CString(bytes, ref position, terminalEnd);
                        entry = new(node.Prefix, "EXPORT_TRIE", "re-export", null, null, null, null);
                    }
                    else
                    {
                        var value = Uleb(bytes, ref position, terminalEnd);
                        entry = (flags & 3) switch
                        {
                            0 => AddressEntry(node.Prefix, "EXPORT_TRIE", "export", AddressEnd(_imageBase, value)),
                            1 => new(node.Prefix, "EXPORT_TRIE", "thread-local-relative", null, Hex(value), null, null),
                            2 => new(node.Prefix, "EXPORT_TRIE", "absolute-export", null, null, null, Hex(value)),
                            _ => new(node.Prefix, "EXPORT_TRIE", "unsupported-kind", null, null, null, null),
                        };
                        if ((flags & 0x10) != 0)
                        {
                            if ((flags & 3) != 0) throw new InvalidDataException("An export resolver has an unsupported kind.");
                            var resolver = Uleb(bytes, ref position, terminalEnd);
                            if (!Add(AddressEntry(node.Prefix + "$resolver", "EXPORT_TRIE", "resolver", AddressEnd(_imageBase, resolver)))) return;
                        }
                    }
                    if (!Add(entry)) return;
                }
                position = terminalEnd;
                if (position >= bytes.Length) throw new InvalidDataException("An export node has no child count.");
                var children = bytes[position++];
                for (var child = 0; child < children; child++)
                {
                    var suffix = CString(bytes, ref position, bytes.Length);
                    if (suffix.Length == 0 || node.Prefix.Length + suffix.Length > 4096)
                        throw new InvalidDataException("An export name exceeds the limit or has an empty edge.");
                    var childOffset = Uleb(bytes, ref position, bytes.Length);
                    if (childOffset >= (ulong)bytes.Length) throw new InvalidDataException("An export child escaped the trie.");
                    stack.Push(((int)childOffset, node.Prefix + suffix, node.Depth + 1));
                }
            }
        }

        private static string CString(byte[] bytes, ref int position, int end)
        {
            if (position < 0 || position >= end || end > bytes.Length) throw new InvalidDataException("A name string is out of bounds.");
            var remaining = bytes.AsSpan(position, Math.Min(end - position, 4097));
            var zero = remaining.IndexOf((byte)0);
            if (zero < 0) throw new InvalidDataException("A name is unterminated or exceeds 4096 bytes.");
            var value = new UTF8Encoding(false, true).GetString(remaining[..zero]);
            position += zero + 1;
            return value;
        }

        private static ulong Uleb(byte[] bytes, ref int position, int end)
        {
            ulong result = 0;
            for (var shift = 0; shift <= 63; shift += 7)
            {
                if (position >= end) throw new InvalidDataException("A variable-length integer is truncated.");
                var value = bytes[position++];
                if (shift == 63 && (value & 0x7e) != 0) throw new InvalidDataException("A variable-length integer overflows 64 bits.");
                result |= (ulong)(value & 0x7f) << shift;
                if ((value & 0x80) == 0) return result;
            }
            throw new InvalidDataException("A variable-length integer exceeds the encoding limit.");
        }
    }
}

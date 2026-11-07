using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PhoneLink.Application.Features.BinaryInspection;

namespace PhoneLink.Application.Tests;

public sealed class MachOInspectorTests
{
    private const ulong BaseAddress = 0x100000000;

    [Theory]
    [InlineData("0x204", true)]
    [InlineData("0XABCDEF", true)]
    [InlineData("0xffffffffffffffff", true)]
    [InlineData("0x10000000000000000", false)]
    [InlineData("0x12\0", false)]
    [InlineData("0x12 ", false)]
    [InlineData("204", false)]
    [InlineData("0x", false)]
    public void FileOffsetInputRequiresExplicitAsciiHex(string input, bool expected) =>
        Assert.Equal(expected, MachOFileOffsetLookup.TryParseHex(input, out _));

    [Fact]
    public async Task ReverseLookupDistinguishesUniversalHeaderGapsAndMappedSections()
    {
        using var stream = new MemoryStream(Fat(Thin(), Thin(0x1000007, 3), false));
        var report = await MachOInspector.InspectAsync(stream);
        var result = MachOFileOffsetLookup.Lookup(report, 0x2204);
        Assert.Equal(MachOFileOffsetOutcome.Mapped, result.Outcome);
        Assert.Equal("x86_64", result.Architecture);
        Assert.Equal("0x204", result.SliceRelativeOffset);
        Assert.Equal("__TEXT", result.Segment);
        Assert.Equal("__text", result.Section);
        Assert.Equal("0x100000204", result.UnslidVirtualAddress);
        Assert.Equal("0x204", result.ImageRelativeOffset);
        Assert.Equal(MachOFileOffsetOutcome.OutsideImageSlice, MachOFileOffsetLookup.Lookup(report, 0x20).Outcome);
        Assert.Equal(MachOFileOffsetOutcome.NotMapped, MachOFileOffsetLookup.Lookup(report, 0x2300).Outcome);
        Assert.Equal(MachOFileOffsetOutcome.OutsideFile, MachOFileOffsetLookup.Lookup(report, (ulong)report.ByteLength).Outcome);
        Assert.False(report.RuntimeAddressesAvailable);
    }

    [Fact]
    public async Task UnslidAddressResolvesAgainstTheChosenUniversalSlice()
    {
        using var stream = new MemoryStream(Fat(Thin(), Thin(0x1000007, 3), false));
        var report = await MachOInspector.InspectAsync(stream);
        Assert.Equal("0x1204", MachOVirtualAddressLookup.Lookup(report, 0, BaseAddress + 0x204).FileOffset);
        var result = MachOVirtualAddressLookup.Lookup(report, 1, BaseAddress + 0x204);
        Assert.Equal(MachOVirtualAddressOutcome.Mapped, result.Outcome);
        Assert.Equal("0x2204", result.FileOffset);
        Assert.Equal("0x204", result.SliceRelativeOffset);
        Assert.Equal("__text", result.Section);
        Assert.Equal("x86_64", result.Architecture);
        Assert.Throws<ArgumentOutOfRangeException>(() => MachOVirtualAddressLookup.Lookup(report, 2, BaseAddress));
    }

    [Theory]
    [InlineData(0x300, MachOVirtualAddressOutcome.NoFileBacking, null)]
    [InlineData(0x1000, MachOVirtualAddressOutcome.NoFileBacking, "__bss")]
    [InlineData(0x2000, MachOVirtualAddressOutcome.NotMapped, null)]
    public async Task VirtualOnlyRangesDoNotInventOffsets(int displacement, MachOVirtualAddressOutcome expected, string? section)
    {
        using var stream = new MemoryStream(Thin());
        var result = MachOVirtualAddressLookup.Lookup(await MachOInspector.InspectAsync(stream), 0, BaseAddress + (ulong)displacement);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(section, result.Section);
        Assert.Null(result.FileOffset);
    }

    [Theory]
    [InlineData("file-overlap", MachOFileOffsetOutcome.AmbiguousSegment)]
    [InlineData("virtual-overlap", MachOFileOffsetOutcome.AmbiguousSegment)]
    [InlineData("high-vm", MachOFileOffsetOutcome.UnsupportedLayout)]
    public async Task ReverseLookupRefusesToGuessAddressesForNonUniqueOrUnsupportedLayouts(string fixture, MachOFileOffsetOutcome expected)
    {
        var bytes = Thin();
        switch (fixture)
        {
            case "file-overlap": W64(bytes, 224, 0x200); W64(bytes, 232, 0x40); break;
            case "virtual-overlap":
                W64(bytes, 208, BaseAddress + 0x200); W64(bytes, 288, BaseAddress + 0x200);
                W64(bytes, 0x318, BaseAddress + 0x200);
                break;
            case "high-vm": W32(bytes, 100, 1); break;
        }
        using var stream = new MemoryStream(bytes);
        var report = await MachOInspector.InspectAsync(stream);
        var result = MachOFileOffsetLookup.Lookup(report, 0x204);
        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.UnslidVirtualAddress);
        var virtualResult = MachOVirtualAddressLookup.Lookup(report, 0, BaseAddress + 0x204);
        Assert.Equal(expected == MachOFileOffsetOutcome.AmbiguousSegment
            ? MachOVirtualAddressOutcome.AmbiguousSegment : MachOVirtualAddressOutcome.UnsupportedLayout, virtualResult.Outcome);
        Assert.Null(virtualResult.FileOffset);
    }

    [Fact]
    public async Task SectionIsNotAttributedWhenItsFileAndVirtualDisplacementsDisagree()
    {
        var bytes = Thin();
        W64(bytes, 136, BaseAddress + 0x201);
        using var stream = new MemoryStream(bytes);
        var result = MachOFileOffsetLookup.Lookup(await MachOInspector.InspectAsync(stream), 0x204);
        Assert.Equal(MachOFileOffsetOutcome.Mapped, result.Outcome);
        Assert.Equal("__TEXT", result.Segment);
        Assert.Null(result.Section);
    }

    [Fact]
    public async Task Arm64DeviceAndSimulatorTargetsAndCommandOffsetsStayDistinct()
    {
        using var stream = new MemoryStream(Fat(BuildMetadata(2), BuildMetadata(7), false));
        var report = await MachOInspector.InspectAsync(stream);
        Assert.False(report.RuntimeAddressesAvailable);
        Assert.All(report.Images, image => Assert.Equal("arm64", image.Architecture));
        var device = report.Images[0];
        var target = Assert.Single(device.DeclaredBuildTargets);
        Assert.Equal("iOS", target.Platform);
        Assert.Equal("26.0.0", target.MinimumOs);
        Assert.Equal("26.2.1", target.Sdk);
        Assert.Equal(new MachOBuildTool(3, 0x12345678), Assert.Single(target.Tools));
        Assert.Equal(2u, device.DeclaredLoadCommandCount);
        Assert.False(device.LoadCommandsTruncated);
        Assert.Equal(new MachOLoadCommand("0x32", "LC_BUILD_VERSION", "0x1020", 32), device.LoadCommands[0]);
        Assert.Equal(new MachODeclaredSignature("0x1080", 16), device.DeclaredSignature);
        Assert.Equal("iOS Simulator", Assert.Single(report.Images[1].DeclaredBuildTargets).Platform);
        Assert.Equal("0x2020", report.Images[1].LoadCommands[0].FileOffset);
    }

    [Theory]
    [InlineData("tool-count")]
    [InlineData("duplicate-target")]
    [InlineData("signature-outside-slice")]
    [InlineData("signature-overlaps-header")]
    [InlineData("duplicate-signature")]
    public async Task RejectsInvalidBuildAndSignatureDeclarations(string mutation)
    {
        var bytes = BuildMetadata(2);
        switch (mutation)
        {
            case "tool-count": W32(bytes, 52, uint.MaxValue); break;
            case "duplicate-target":
                W32(bytes, 20, 64); bytes.AsSpan(32, 32).CopyTo(bytes.AsSpan(64)); break;
            case "signature-outside-slice": W32(bytes, 72, 255); break;
            case "signature-overlaps-header": W32(bytes, 72, 60); break;
            case "duplicate-signature":
                W32(bytes, 16, 3); W32(bytes, 20, 64); bytes.AsSpan(64, 16).CopyTo(bytes.AsSpan(80)); break;
        }
        using var stream = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => MachOInspector.InspectAsync(stream));
    }

    [Fact]
    public async Task LoadCommandDirectoryCapsOutputAcrossSlicesButStillValidatesRemainder()
    {
        static byte[] UnknownCommands(int count)
        {
            var bytes = new byte[32 + count * 8];
            W32(bytes, 0, 0xfeedfacf); W32(bytes, 4, 0x100000c);
            W32(bytes, 16, (uint)count); W32(bytes, 20, (uint)count * 8);
            for (var index = 0; index < count; index++)
            {
                W32(bytes, 32 + index * 8, 0x99);
                W32(bytes, 36 + index * 8, 8);
            }
            return bytes;
        }

        var first = UnknownCommands(3000);
        var second = UnknownCommands(1200);
        using var stream = new MemoryStream(Fat(first, second, false));
        var report = await MachOInspector.InspectAsync(stream);
        Assert.False(report.Images[0].LoadCommandsTruncated);
        Assert.True(report.Images[1].LoadCommandsTruncated);
        Assert.Equal(1200u, report.Images[1].DeclaredLoadCommandCount);
        Assert.Equal(4096, report.Images.Sum(image => image.LoadCommands.Count));
        Assert.Null(report.Images[0].LoadCommands[0].Name);
        W32(second, second.Length - 4, 0);
        using var corrupt = new MemoryStream(Fat(first, second, false));
        await Assert.ThrowsAsync<InvalidDataException>(() => MachOInspector.InspectAsync(corrupt));
    }

    private static byte[] BuildMetadata(uint platform)
    {
        var bytes = new byte[256];
        W32(bytes, 0, 0xfeedfacf); W32(bytes, 4, 0x100000c); W32(bytes, 12, 2);
        W32(bytes, 16, 2); W32(bytes, 20, 48);
        W32(bytes, 32, 0x32); W32(bytes, 36, 32); W32(bytes, 40, platform);
        W32(bytes, 44, 0x1a0000); W32(bytes, 48, 0x1a0201); W32(bytes, 52, 1);
        W32(bytes, 56, 3); W32(bytes, 60, 0x12345678);
        W32(bytes, 64, 0x1d); W32(bytes, 68, 16); W32(bytes, 72, 128); W32(bytes, 76, 16);
        return bytes;
    }

    [Fact]
    public async Task DistinguishesUnslidAddressesFileOffsetsConstantsAndZeroFill()
    {
        var bytes = Thin();
        using var stream = new MemoryStream(bytes);
        var report = await MachOInspector.InspectAsync(stream);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), report.Sha256);
        Assert.False(report.RuntimeAddressesAvailable);
        var image = Assert.Single(report.Images);
        Assert.Equal("arm64", image.Architecture);
        Assert.Equal("00010203-0405-0607-0809-0a0b0c0d0e0f", image.Uuid);
        var symbol = Assert.Single(image.Entries, entry => entry.Name == "_probe");
        Assert.Equal("0x100000204", symbol.VirtualAddress);
        Assert.Equal("0x204", symbol.FileOffset);
        Assert.Equal("0x204", symbol.ImageRelativeOffset);
        Assert.Null(Assert.Single(image.Entries, entry => entry.Name == "_bss").FileOffset);
        var constant = Assert.Single(image.Entries, entry => entry.Name == "_constant");
        Assert.Null(constant.VirtualAddress);
        Assert.Null(constant.FileOffset);
        Assert.Equal("0x2a", constant.AbsoluteValue);
        Assert.Contains(image.Entries, entry => entry.Source == "LC_FUNCTION_STARTS" && entry.VirtualAddress == symbol.VirtualAddress);
        Assert.Contains(image.Entries, entry => entry.Name == "_export" && entry.VirtualAddress == "0x100000208" && entry.FileOffset == "0x208");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UniversalSlicesPreserveAbsoluteFileOffsetsAndArchitecture(bool fat64)
    {
        var bytes = Fat(Thin(subtype: 2), Thin(cpu: 0x1000007, subtype: 3), fat64);
        using var stream = new MemoryStream(bytes);
        var report = await MachOInspector.InspectAsync(stream);
        Assert.Equal(2, report.Images.Count);
        Assert.Equal("arm64e", report.Images[0].Architecture);
        Assert.Equal("x86_64", report.Images[1].Architecture);
        var first = Assert.Single(report.Images[0].Entries, entry => entry.Name == "_probe");
        Assert.Equal("0x1204", first.FileOffset);
        Assert.Equal("0x204", first.ImageRelativeOffset);
        Assert.Equal("0x2204", Assert.Single(report.Images[1].Entries, entry => entry.Name == "_probe").FileOffset);
    }

    [Theory]
    [InlineData("command-size")]
    [InlineData("command-count")]
    [InlineData("vm-overflow")]
    [InlineData("file-range")]
    [InlineData("section-ordinal")]
    [InlineData("name-index")]
    [InlineData("symbol-table")]
    [InlineData("string-limit")]
    [InlineData("unterminated-name")]
    [InlineData("function-uleb")]
    [InlineData("function-terminator")]
    [InlineData("export-cycle")]
    [InlineData("export-terminal")]
    public async Task RejectsCorruptBoundsAndEncodings(string mutation)
    {
        var bytes = Thin();
        switch (mutation)
        {
            case "command-size": W32(bytes, 36, 7); break;
            case "command-count": W32(bytes, 16, 5); break;
            case "vm-overflow": W64(bytes, 56, ulong.MaxValue - 1); break;
            case "file-range": W64(bytes, 72, 1023); W64(bytes, 80, 4); break;
            case "section-ordinal": bytes[0x305] = 4; break;
            case "name-index": W32(bytes, 0x300, uint.MaxValue); break;
            case "symbol-table": W32(bytes, 368, uint.MaxValue); break;
            case "string-limit": W32(bytes, 380, uint.MaxValue); break;
            case "unterminated-name": bytes[0x330 + Encoding.UTF8.GetByteCount("\0_probe\0_bss\0_constant\0") - 1] = 65; break;
            case "function-uleb": bytes.AsSpan(0x350, 4).Fill(0x80); break;
            case "function-terminator": W32(bytes, 396, 1); bytes[0x350] = 1; break;
            case "export-cycle": bytes[0x36a] = 0; break;
            case "export-terminal": bytes[0x36b] = 200; break;
        }
        using var stream = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => MachOInspector.InspectAsync(stream));
    }

    [Fact]
    public async Task RejectsOverlappingFatSlicesAndInconsistentArchitecture()
    {
        var bytes = Fat(Thin(), Thin(cpu: 0x1000007, subtype: 3), false);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(36), 0x1000);
        using var overlap = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => MachOInspector.InspectAsync(overlap));
        bytes = Fat(Thin(), Thin(cpu: 0x1000007, subtype: 3), false);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 0x1000007);
        using var mismatch = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => MachOInspector.InspectAsync(mismatch));
    }

    [Fact]
    public async Task MarksEncryptionWithoutClaimingDecryptionOrRuntimeAddresses()
    {
        var bytes = Thin();
        W32(bytes, 16, 7); W32(bytes, 20, 408);
        W32(bytes, 416, 0x2c); W32(bytes, 420, 24);
        W32(bytes, 424, 0x200); W32(bytes, 428, 0x20); W32(bytes, 432, 1);
        using var stream = new MemoryStream(bytes);
        var report = await MachOInspector.InspectAsync(stream);
        Assert.True(report.Images[0].EncryptedContent);
        Assert.False(report.RuntimeAddressesAvailable);
        Assert.Contains(report.Images[0].Warnings, warning => warning.Contains("do not decrypt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnforcesOneOutputBudgetAcrossUniversalSlices()
    {
        var first = ManySymbols(60_000, 0x100000c, 0);
        var second = ManySymbols(60_000, 0x1000007, 3);
        using var stream = new MemoryStream(Fat(first, second, false));
        var report = await MachOInspector.InspectAsync(stream);
        Assert.Equal(100_000, report.Images.Sum(image => image.Entries.Count));
        Assert.False(report.Images[0].EntriesTruncated);
        Assert.True(report.Images[1].EntriesTruncated);
    }

    [Fact]
    public async Task RejectsNonImagesAndHonorsCancellation()
    {
        using var invalid = new MemoryStream("not a Mach-O image"u8.ToArray());
        await Assert.ThrowsAsync<NotSupportedException>(() => MachOInspector.InspectAsync(invalid));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        using var stream = new MemoryStream(Thin());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MachOInspector.InspectAsync(stream, cancellation.Token));
    }

    private static byte[] Thin(uint cpu = 0x100000c, uint subtype = 0)
    {
        var bytes = new byte[1024];
        W32(bytes, 0, 0xfeedfacf); W32(bytes, 4, cpu); W32(bytes, 8, subtype);
        W32(bytes, 12, 6); W32(bytes, 16, 6); W32(bytes, 20, 384);
        Segment(bytes, 32, "__TEXT", BaseAddress, 0x1000, 0, 0x300, "__text", BaseAddress + 0x200, 0x20, 0x200, 0);
        Segment(bytes, 184, "__DATA", BaseAddress + 0x1000, 0x1000, 0x300, 0, "__bss", BaseAddress + 0x1000, 0x20, 0, 1);
        W32(bytes, 336, 0x1b); W32(bytes, 340, 24);
        for (var index = 0; index < 16; index++) bytes[344 + index] = (byte)index;
        var strings = Encoding.UTF8.GetBytes("\0_probe\0_bss\0_constant\0");
        W32(bytes, 360, 2); W32(bytes, 364, 24); W32(bytes, 368, 0x300); W32(bytes, 372, 3);
        W32(bytes, 376, 0x330); W32(bytes, 380, (uint)strings.Length);
        Symbol(bytes, 0x300, 1, 0xf, 1, BaseAddress + 0x204);
        Symbol(bytes, 0x310, 8, 0xf, 2, BaseAddress + 0x1000);
        Symbol(bytes, 0x320, 13, 3, 0, 42);
        strings.CopyTo(bytes, 0x330);
        W32(bytes, 384, 0x26); W32(bytes, 388, 16); W32(bytes, 392, 0x350); W32(bytes, 396, 4);
        new byte[] { 0x84, 4, 0, 0 }.CopyTo(bytes, 0x350);
        W32(bytes, 400, 0x80000033); W32(bytes, 404, 16); W32(bytes, 408, 0x360); W32(bytes, 412, 16);
        new byte[] { 0, 1, (byte)'_', (byte)'e', (byte)'x', (byte)'p', (byte)'o', (byte)'r', (byte)'t', 0, 11, 3, 0, 0x88, 4, 0 }.CopyTo(bytes, 0x360);
        return bytes;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecognizesPieExecutableHeaderWithoutCallingItASectionOrFunction(bool universal)
    {
        var bytes = PieHeader();
        if (universal) bytes = Fat(bytes, Thin(cpu: 0x1000007, subtype: 3), false);
        using var stream = new MemoryStream(bytes);
        var report = await MachOInspector.InspectAsync(stream);
        var header = Assert.Single(report.Images[0].Entries, item => item.Name == "__mh_execute_header");
        Assert.Equal("image-header", header.Kind);
        Assert.Equal("0x100000000", header.VirtualAddress);
        Assert.Equal("0x0", header.ImageRelativeOffset);
        Assert.Equal(universal ? "0x1000" : "0x0", header.FileOffset);
        Assert.False(report.RuntimeAddressesAvailable);
    }

    [Theory]
    [InlineData("not-header-base")]
    [InlineData("wrong-file-type")]
    [InlineData("not-pie")]
    [InlineData("wrong-section")]
    [InlineData("ordinary-name")]
    [InlineData("zero-fill")]
    [InlineData("outside-section-end")]
    public async Task HeaderRecognitionDoesNotRelaxOrdinarySectionBounds(string mutation)
    {
        var bytes = PieHeader();
        switch (mutation)
        {
            case "not-header-base": W64(bytes, 0x308, BaseAddress + 8); break;
            case "wrong-file-type": W32(bytes, 12, 6); break;
            case "not-pie": W32(bytes, 24, 0); break;
            case "wrong-section": bytes[0x305] = 2; break;
            case "ordinary-name": bytes.AsSpan(0x330, 32).Clear(); Encoding.UTF8.GetBytes("\0_probe\0").CopyTo(bytes, 0x330); break;
            case "zero-fill": W32(bytes, 168, 1); break;
            case "outside-section-end": W64(bytes, 0x308, BaseAddress + 0x221); break;
        }
        using var stream = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => MachOInspector.InspectAsync(stream));
    }

    private static byte[] PieHeader()
    {
        var bytes = Thin();
        W32(bytes, 12, 2); W32(bytes, 24, 0x200000);
        bytes.AsSpan(0x330, 32).Clear();
        var name = Encoding.UTF8.GetBytes("\0__mh_execute_header\0");
        name.CopyTo(bytes, 0x330); W32(bytes, 380, (uint)name.Length);
        Symbol(bytes, 0x300, 1, 0xf, 1, BaseAddress);
        W32(bytes, 0x310, 0); W32(bytes, 0x320, 0);
        return bytes;
    }

    private static byte[] ManySymbols(int count, uint cpu, uint subtype)
    {
        var stringsOffset = 0x300 + count * 16;
        var bytes = new byte[stringsOffset + 3];
        Thin(cpu, subtype).AsSpan(0, 768).CopyTo(bytes);
        W32(bytes, 16, 4); W32(bytes, 20, 352);
        W32(bytes, 372, (uint)count); W32(bytes, 376, (uint)stringsOffset); W32(bytes, 380, 3);
        for (var index = 0; index < count; index++) Symbol(bytes, 0x300 + index * 16, 1, 0xf, 1, BaseAddress + 0x204);
        bytes[stringsOffset + 1] = (byte)'s';
        return bytes;
    }

    private static byte[] Fat(byte[] first, byte[] second, bool fat64)
    {
        const int firstOffset = 0x1000;
        var secondOffset = (firstOffset + first.Length + 4095) / 4096 * 4096;
        var bytes = new byte[secondOffset + second.Length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, fat64 ? 0xcafebabfU : 0xcafebabeU);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), 2);
        var recordSize = fat64 ? 32 : 20;
        for (var index = 0; index < 2; index++)
        {
            var image = index == 0 ? first : second;
            var offset = index == 0 ? firstOffset : secondOffset;
            var record = bytes.AsSpan(8 + index * recordSize, recordSize);
            BinaryPrimitives.WriteUInt32BigEndian(record, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(4)));
            BinaryPrimitives.WriteUInt32BigEndian(record[4..], BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(8)));
            if (fat64)
            {
                BinaryPrimitives.WriteUInt64BigEndian(record[8..], (ulong)offset);
                BinaryPrimitives.WriteUInt64BigEndian(record[16..], (ulong)image.Length);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(record[8..], (uint)offset);
                BinaryPrimitives.WriteUInt32BigEndian(record[12..], (uint)image.Length);
            }
            BinaryPrimitives.WriteUInt32BigEndian(record[(fat64 ? 24 : 16)..], 12);
            image.CopyTo(bytes, offset);
        }
        return bytes;
    }

    private static void Segment(byte[] bytes, int offset, string name, ulong address, ulong size, ulong fileOffset,
        ulong fileSize, string section, ulong sectionAddress, ulong sectionSize, uint sectionOffset, uint flags)
    {
        W32(bytes, offset, 0x19); W32(bytes, offset + 4, 152); Name(bytes, offset + 8, name);
        W64(bytes, offset + 24, address); W64(bytes, offset + 32, size); W64(bytes, offset + 40, fileOffset); W64(bytes, offset + 48, fileSize);
        W32(bytes, offset + 64, 1);
        Name(bytes, offset + 72, section); Name(bytes, offset + 88, name);
        W64(bytes, offset + 104, sectionAddress); W64(bytes, offset + 112, sectionSize); W32(bytes, offset + 120, sectionOffset); W32(bytes, offset + 136, flags);
    }

    private static void Symbol(byte[] bytes, int offset, uint name, byte type, byte section, ulong value)
    { W32(bytes, offset, name); bytes[offset + 4] = type; bytes[offset + 5] = section; W64(bytes, offset + 8, value); }
    private static void Name(byte[] bytes, int offset, string value) => Encoding.ASCII.GetBytes(value).CopyTo(bytes, offset);
    private static void W32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void W64(byte[] bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
}

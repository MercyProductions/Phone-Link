using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PhoneLink.Application.Features.BinaryInspection;

namespace PhoneLink.Application.Tests;

public sealed class CompanionRuntimeMapperTests
{
    private const string Uuid = "11111111-2222-3333-4444-555555555555";
    private static MachOImage Image => new("arm64", "0x0", Uuid, 6, "0x0", 4096, "0x100000000", false, 0, false,
        [new("__TEXT", "0x100000000", "0x1000", "0x0", "0x0", "0x1000")], [],
        [new("_marker", "test", "symbol", "0x100000100", "0x100", "0x100", null),
         new("_data", "test", "symbol", "0x100002000", "0x2000", null, null),
         new("_constant", "test", "absolute", null, null, null, "0x2a")], []);
    private static MachOInspection Inspection => new("synthetic", 4096, false, [Image]);
    private static JsonObject Snapshot => new()
    {
        ["schemaVersion"] = 1, ["capturedAt"] = "2026-11-03T09:00:00.000Z",
        ["scope"] = "companion-own-marker-image", ["imageUUID"] = Uuid,
        ["cpuType"] = 0x100000c, ["cpuSubtype"] = 0,
        ["preferredTextAddress"] = "0x100000000", ["loadedHeaderAddress"] = "0x110000000",
        ["textVirtualSize"] = "0x1000", ["slide"] = "0x10000000",
        ["markerRuntimeAddress"] = "0x110000100", ["markerImageOffset"] = "0x100",
    };
    private static byte[] Bytes(JsonObject snapshot) => JsonSerializer.SerializeToUtf8Bytes(snapshot);

    [Fact]
    public void MapsOnlyMatchedTextAndRetainsUnverifiedStaticSemantics()
    {
        var report = CompanionRuntimeMapper.Map(Inspection, Bytes(Snapshot));
        Assert.Equal("0x110000100", Assert.Single(report.TextEntries).SnapshotDerivedAddress);
        Assert.False(report.LiveProcessVerified);
        Assert.False(report.SnapshotOriginAuthenticated);
        Assert.False(Inspection.RuntimeAddressesAvailable);
        Assert.Contains("restart/reload", report.Warning);
    }

    [Fact]
    public void SupportsNegativeSlidesWithoutUnsignedWraparound()
    {
        var snapshot = Snapshot;
        snapshot["loadedHeaderAddress"] = "0x80000000";
        snapshot["markerRuntimeAddress"] = "0x80000100";
        snapshot["slide"] = "-0x80000000";
        Assert.Equal("0x80000100", Assert.Single(CompanionRuntimeMapper.Map(Inspection, Bytes(snapshot)).TextEntries).SnapshotDerivedAddress);
    }

    [Theory]
    [InlineData("schemaVersion", "2")]
    [InlineData("scope", "\"other-process\"")]
    [InlineData("imageUUID", "\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\"")]
    [InlineData("cpuType", "16777223")]
    [InlineData("cpuSubtype", "1")]
    [InlineData("cpuType", "\"16777228\"")]
    [InlineData("preferredTextAddress", "\"0x100001000\"")]
    [InlineData("textVirtualSize", "\"0x2000\"")]
    [InlineData("slide", "\"0x20\"")]
    [InlineData("markerRuntimeAddress", "\"0x110000200\"")]
    [InlineData("markerImageOffset", "\"0x1000\"")]
    [InlineData("loadedHeaderAddress", "\"0xffffffffffffffff\"")]
    [InlineData("capturedAt", "\"not-a-date\"")]
    [InlineData("markerImageOffset", "\" 0x100\"")]
    public void RejectsWrongBuildArchitectureSchemaLayoutOrArithmetic(string field, string raw)
    {
        var snapshot = Snapshot;
        snapshot[field] = JsonNode.Parse(raw);
        Assert.Throws<InvalidDataException>(() => CompanionRuntimeMapper.Map(Inspection, Bytes(snapshot)));
    }

    [Fact]
    public void RejectsMissingExtraDuplicateOversizedAndInvalidJsonWithoutEchoingInput()
    {
        var missing = Snapshot; missing.Remove("scope");
        var extra = Snapshot; extra["private-owner-name"] = "SENSITIVE-MARKER";
        var duplicate = Snapshot.ToJsonString().Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal);
        foreach (var bytes in new[] { Bytes(missing), Bytes(extra), Encoding.UTF8.GetBytes(duplicate),
                     new byte[8193], Encoding.UTF8.GetBytes("{broken SENSITIVE-MARKER}") })
        {
            var exception = Assert.Throws<InvalidDataException>(() => CompanionRuntimeMapper.Map(Inspection, bytes));
            Assert.DoesNotContain("SENSITIVE-MARKER", exception.Message);
        }
    }

    [Fact]
    public void RejectsAmbiguousMatchingImagesAndChangedStaticLayout()
    {
        Assert.Throws<InvalidDataException>(() => CompanionRuntimeMapper.Map(Inspection with { Images = [Image, Image] }, Bytes(Snapshot)));
        var changed = Image with { Segments = [new("__TEXT", "0x100000000", "0x2000", "0x0", "0x0", "0x1000")] };
        Assert.Throws<InvalidDataException>(() => CompanionRuntimeMapper.Map(Inspection with { Images = [changed] }, Bytes(Snapshot)));
    }

    [Fact]
    public void BoundsMappingOutputAndPreservesPartialStaticEvidence()
    {
        var image = Image with { Entries = Enumerable.Repeat(Image.Entries[0], 4100).ToArray() };
        var report = CompanionRuntimeMapper.Map(Inspection with { Images = [image] }, Bytes(Snapshot));
        Assert.Equal(4096, report.TextEntries.Count);
        Assert.True(report.EntriesTruncated);
        Assert.True(CompanionRuntimeMapper.Map(Inspection with { Images = [Image with { EntriesTruncated = true }] }, Bytes(Snapshot)).EntriesTruncated);
    }
}

using PhoneLink.Security.Session;

namespace PhoneLink.Security.Tests;

public sealed class SessionTranscriptTests
{
    [Fact]
    public void MatchesSharedSwiftVector()
    {
        var serverProofInput = SessionTranscript.CreateServerProofInput(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa"),
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            Guid.Parse("ffffffff-eeee-dddd-cccc-bbbbbbbbbbbb"),
            Enumerable.Range(0, 32).Select(value => (byte)value).ToArray(),
            Enumerable.Range(32, 32).Select(value => (byte)value).ToArray(),
            1,
            "1.2.3");

        Assert.Equal(
            "c65d3942de65d3611c8d4c10615ed03a4dd233209af33abf823ce5d16139c3ae",
            Convert.ToHexStringLower(serverProofInput));

        var clientProofInput = SessionTranscript.CreateClientProofInput(
            serverProofInput,
            Enumerable.Range(64, 64).Select(value => (byte)value).ToArray());
        Assert.Equal(
            "8a8d05c4cfb4a3e9e73dd0aa0ca486dfd8eb07b8eaa17a345e72bfd763d60474",
            Convert.ToHexStringLower(clientProofInput));
    }
}

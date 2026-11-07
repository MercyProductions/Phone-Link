using PhoneLink.Application.Features.Calling;

namespace PhoneLink.Application.Tests;

public sealed class CompanionMessageDraftTests
{
    [Fact]
    public void NormalizesOnePhoneNumberAndDoesNotExposeDraftInDiagnosticString()
    {
        var draft = CompanionMessageDraft.Create("+1 (555) 010-1000", "PRIVATE-DRAFT\r\n👩‍👩‍👦");
        Assert.Equal("+15550101000", draft.Recipient);
        Assert.Equal("PRIVATE-DRAFT\n👩‍👩‍👦", draft.Body);
        Assert.DoesNotContain("PRIVATE", draft.ToString());
        Assert.DoesNotContain("555", draft.ToString());
    }

    [Theory]
    [InlineData("*123", "text")]
    [InlineData("123#", "text")]
    [InlineData("123,456", "text")]
    [InlineData("123", "   ")]
    [InlineData("123", "no\0control")]
    public void RejectsDialCodesMultipleRecipientsAndInvalidBodies(string recipient, string body) =>
        Assert.ThrowsAny<ArgumentException>(() => CompanionMessageDraft.Create(recipient, body));

    [Fact]
    public void AppliesUtf8ByteLimitRatherThanCharacterCount()
    {
        Assert.Equal(500, CompanionMessageDraft.Create("123", new string('é', 500)).Body.Length);
        Assert.Throws<ArgumentException>(() => CompanionMessageDraft.Create("123", new string('é', 501)));
    }
}

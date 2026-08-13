using PhoneLink.Application.Features.Calling;

namespace PhoneLink.Application.Tests;

public sealed class TelephoneDialStringTests
{
    [Theory]
    [InlineData("+1 (312) 555-0198", "+13125550198")]
    [InlineData("555.0100", "5550100")]
    [InlineData("*67 555-0100#", "*675550100#")]
    public void NormalizeReturnsSafeTelDialString(string input, string expected)
    {
        Assert.Equal(expected, TelephoneDialString.Normalize(input));
    }

    [Theory]
    [InlineData("12")]
    [InlineData("555;0100")]
    [InlineData("tel:5550100")]
    public void NormalizeRejectsUnsafeOrIncompleteValues(string input)
    {
        Assert.ThrowsAny<ArgumentException>(() => TelephoneDialString.Normalize(input));
    }
}

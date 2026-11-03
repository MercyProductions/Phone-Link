using PhoneLink.Application.Features.Calling;
using PhoneLink.Domain.Devices;

namespace PhoneLink.Application.Tests;

public sealed class CompanionDialTargetTests
{
    [Theory]
    [InlineData("matching", true)]
    [InlineData("other-device", false)]
    [InlineData("reconnected", false)]
    [InlineData("disconnected", false)]
    [InlineData("revoked", false)]
    [InlineData("windows", false)]
    [InlineData("missing", false)]
    [InlineData("empty-session", false)]
    [InlineData("empty-device", false)]
    public void RequiresExactTrustedIosIdentityAndSession(string scenario, bool expected)
    {
        var id = DeviceId.New();
        var session = Guid.NewGuid();
        var device = TrustedDevice.Create(
            scenario == "other-device" ? DeviceId.New() : id, "Same phone name",
            scenario == "windows" ? DevicePlatform.Windows : DevicePlatform.Ios,
            new byte[32], DateTimeOffset.UtcNow);
        if (scenario == "revoked") device = device.Revoke(DateTimeOffset.UtcNow);
        var target = new CompanionDialTarget(
            scenario == "empty-device" ? default : id,
            scenario == "empty-session" ? Guid.Empty : session, "Same phone name");
        var active = scenario switch
        {
            "reconnected" => Guid.NewGuid(),
            "disconnected" or "empty-session" => Guid.Empty,
            _ => session,
        };
        Assert.Equal(expected, target.Matches(scenario == "missing" ? null : device, active));
    }

    [Fact]
    public void SameNamesHaveDistinctIdentityLabels()
    {
        var first = new CompanionDialTarget(DeviceId.New(), Guid.NewGuid(), "iPhone");
        var second = new CompanionDialTarget(DeviceId.New(), Guid.NewGuid(), "iPhone");
        Assert.NotEqual(first.SelectionLabel, second.SelectionLabel);
        Assert.Contains(first.DeviceId.ToString(), first.SelectionLabel, StringComparison.Ordinal);
    }
}

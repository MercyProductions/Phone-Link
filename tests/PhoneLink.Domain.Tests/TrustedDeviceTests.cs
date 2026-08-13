using PhoneLink.Domain.Devices;

namespace PhoneLink.Domain.Tests;

public sealed class TrustedDeviceTests
{
    [Fact]
    public void DeviceIdRejectsEmptyGuid()
    {
        var exception = Assert.Throws<ArgumentException>(() => new DeviceId(Guid.Empty));

        Assert.Contains("cannot be empty", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateCopiesIdentityKeyAndNormalizesName()
    {
        var key = Enumerable.Range(0, TrustedDevice.IdentityPublicKeyLength)
            .Select(value => (byte)value)
            .ToArray();
        var originalFirstByte = key[0];

        var device = TrustedDevice.Create(
            DeviceId.New(),
            "  Gabriel's iPhone  ",
            DevicePlatform.Ios,
            key,
            DateTimeOffset.UtcNow);

        key[0] = 255;

        Assert.Equal("Gabriel's iPhone", device.DisplayName);
        Assert.Equal(originalFirstByte, device.IdentityPublicKey.Span[0]);
        Assert.Equal(DeviceTrustState.Trusted, device.TrustState);
    }

    [Fact]
    public void RevokeIsIdempotent()
    {
        var pairedAt = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
        var revokedAt = pairedAt.AddHours(1);
        var device = TrustedDevice.Create(
            DeviceId.New(),
            "iPhone",
            DevicePlatform.Ios,
            new byte[TrustedDevice.IdentityPublicKeyLength],
            pairedAt);

        var revoked = device.Revoke(revokedAt);
        var revokedAgain = revoked.Revoke(revokedAt.AddMinutes(1));

        Assert.Equal(DeviceTrustState.Revoked, revoked.TrustState);
        Assert.Equal(revokedAt, revoked.RevokedAt);
        Assert.Same(revoked, revokedAgain);
    }
}


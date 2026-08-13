using PhoneLink.Protocol.Pairing;
using PhoneLink.Protocol.Validation;

namespace PhoneLink.Protocol.Tests;

public sealed class PairingOfferCodecTests
{
    [Fact]
    public void OfferRoundTripsWithoutChangingRawTranscript()
    {
        using var offer = CreateOffer(
            DateTimeOffset.Parse("2026-08-12T19:06:31.427Z", System.Globalization.CultureInfo.InvariantCulture));
        using var encoded = PairingOfferCodec.Encode(offer);
        var rawJson = encoded.RawJson.ToArray();

        using var decoded = PairingOfferCodec.Decode(encoded.QrPayload);

        Assert.Equal(rawJson, decoded.RawJson.ToArray());
        Assert.Equal(1, decoded.Offer.Version);
        Assert.Equal("192.168.1.24", Assert.Single(decoded.Offer.Endpoints).Host);
        Assert.Equal(45_831, decoded.Offer.Endpoints[0].Port);
        Assert.Equal(32, decoded.Offer.PairingSecret.Length);
    }

    [Fact]
    public void DecoderRejectsExtraQueryParameters()
    {
        using var offer = CreateOffer(DateTimeOffset.UtcNow.AddMinutes(1));
        using var encoded = PairingOfferCodec.Encode(offer);

        var exception = Assert.Throws<PhoneLinkProtocolException>(
            () => PairingOfferCodec.Decode(encoded.QrPayload + "&redirect=https://example.com"));

        Assert.Equal("pairing.offer.invalid-uri", exception.Code);
    }

    [Theory]
    [InlineData("8.8.8.8", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("192.168.50.4", true)]
    [InlineData("desktop.local", true)]
    public void LocalEndpointPolicyRejectsInternetAddresses(string host, bool expected)
    {
        Assert.Equal(expected, PairingOfferValidator.IsLocalEndpoint(host));
    }

    [Fact]
    public void ClientValidationRejectsExpiredOffer()
    {
        var now = DateTimeOffset.UtcNow;
        using var offer = CreateOffer(now.AddSeconds(-1));

        var exception = Assert.Throws<PhoneLinkProtocolException>(
            () => PairingOfferValidator.ValidateForClient(offer, now));

        Assert.Equal("pairing.offer.expired", exception.Code);
    }

    private static PairingOffer CreateOffer(DateTimeOffset expiresAt) =>
        new(
            1,
            Guid.Parse("4abbe312-e81e-45ec-8c8e-6a474161ad7f"),
            expiresAt,
            [new PairingEndpoint("192.168.1.24", 45_831)],
            Enumerable.Repeat((byte)0x11, 32).ToArray(),
            Enumerable.Repeat((byte)0x22, 32).ToArray(),
            Enumerable.Repeat((byte)0x33, 16).ToArray(),
            Enumerable.Repeat((byte)0x44, 32).ToArray());
}

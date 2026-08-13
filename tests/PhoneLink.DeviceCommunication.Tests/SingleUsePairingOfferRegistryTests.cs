using PhoneLink.DeviceCommunication.Pairing;
using PhoneLink.Protocol.Pairing;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class SingleUsePairingOfferRegistryTests
{
    [Fact]
    public void OfferCanBeConsumedOnlyOnce()
    {
        using var registry = new SingleUsePairingOfferRegistry();
        using var original = CreateArtifact(DateTimeOffset.UtcNow.AddMinutes(1));
        var sessionId = original.Offer.SessionId;
        registry.Register(Clone(original));

        using var consumed = registry.Consume(sessionId, DateTimeOffset.UtcNow);

        Assert.NotNull(consumed);
        Assert.Null(registry.Consume(sessionId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ExpiredOfferIsDestroyedAndCannotBeReturned()
    {
        using var registry = new SingleUsePairingOfferRegistry();
        using var original = CreateArtifact(DateTimeOffset.UtcNow.AddSeconds(-1));
        var sessionId = original.Offer.SessionId;
        registry.Register(Clone(original));

        Assert.Null(registry.Consume(sessionId, DateTimeOffset.UtcNow));
        Assert.Null(registry.Consume(sessionId, DateTimeOffset.UtcNow));
    }

    private static PairingOfferArtifact CreateArtifact(DateTimeOffset expiresAt)
    {
        var offer = new PairingOffer(
            1,
            Guid.NewGuid(),
            expiresAt,
            [new PairingEndpoint("192.168.1.4", 45_831)],
            new byte[32],
            new byte[32],
            new byte[16],
            new byte[32]);
        return PairingOfferCodec.Encode(offer);
    }

    private static PairingOfferArtifact Clone(PairingOfferArtifact artifact) =>
        PairingOfferCodec.Decode(artifact.QrPayload);
}

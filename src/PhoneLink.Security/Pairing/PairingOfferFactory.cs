using System.Security.Cryptography;
using PhoneLink.Protocol;
using PhoneLink.Protocol.Pairing;

namespace PhoneLink.Security.Pairing;

public static class PairingOfferFactory
{
    public static PairingOfferArtifact Create(
        IReadOnlyList<PairingEndpoint> endpoints,
        ReadOnlySpan<byte> tlsSpkiSha256,
        Ed25519Identity serverIdentity,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(serverIdentity);

        var publicKey = serverIdentity.ExportPublicKey();
        var offerNonce = RandomNumberGenerator.GetBytes(16);
        var pairingSecret = RandomNumberGenerator.GetBytes(32);

        try
        {
            var offer = new PairingOffer(
                ProtocolConstants.CurrentVersion,
                Guid.NewGuid(),
                now.AddMinutes(2),
                endpoints,
                tlsSpkiSha256,
                publicKey,
                offerNonce,
                pairingSecret);

            return PairingOfferCodec.Encode(offer);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(publicKey);
            CryptographicOperations.ZeroMemory(offerNonce);
            CryptographicOperations.ZeroMemory(pairingSecret);
        }
    }
}

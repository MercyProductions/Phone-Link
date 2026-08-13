using System.Security.Cryptography;

namespace PhoneLink.Protocol.Pairing;

public sealed class PairingOfferArtifact : IDisposable
{
    private byte[]? _rawJson;

    public PairingOfferArtifact(PairingOffer offer, ReadOnlySpan<byte> rawJson, string qrPayload)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentException.ThrowIfNullOrWhiteSpace(qrPayload);

        Offer = offer;
        _rawJson = rawJson.ToArray();
        QrPayload = qrPayload;
    }

    public PairingOffer Offer { get; }

    public ReadOnlyMemory<byte> RawJson => _rawJson
        ?? throw new ObjectDisposedException(nameof(PairingOfferArtifact));

    public string QrPayload { get; private set; }

    public void Dispose()
    {
        Offer.Dispose();

        if (_rawJson is not null)
        {
            CryptographicOperations.ZeroMemory(_rawJson);
            _rawJson = null;
        }

        QrPayload = string.Empty;
    }
}

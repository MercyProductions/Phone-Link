using System.Security.Cryptography;

namespace PhoneLink.Protocol.Pairing;

public sealed class PairingOffer : IDisposable
{
    private byte[]? _tlsSpkiSha256;
    private byte[]? _serverIdentityKey;
    private byte[]? _offerNonce;
    private byte[]? _pairingSecret;

    public PairingOffer(
        int version,
        Guid sessionId,
        DateTimeOffset expiresAt,
        IReadOnlyList<PairingEndpoint> endpoints,
        ReadOnlySpan<byte> tlsSpkiSha256,
        ReadOnlySpan<byte> serverIdentityKey,
        ReadOnlySpan<byte> offerNonce,
        ReadOnlySpan<byte> pairingSecret)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        if (endpoints.Count is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(endpoints), "A pairing offer must have between one and eight endpoints.");
        }

        RequireLength(tlsSpkiSha256, 32, nameof(tlsSpkiSha256));
        RequireLength(serverIdentityKey, 32, nameof(serverIdentityKey));
        RequireLength(offerNonce, 16, nameof(offerNonce));
        RequireLength(pairingSecret, 32, nameof(pairingSecret));

        Version = version;
        SessionId = sessionId;
        ExpiresAt = expiresAt;
        Endpoints = endpoints.ToArray();
        _tlsSpkiSha256 = tlsSpkiSha256.ToArray();
        _serverIdentityKey = serverIdentityKey.ToArray();
        _offerNonce = offerNonce.ToArray();
        _pairingSecret = pairingSecret.ToArray();
    }

    public int Version { get; }

    public Guid SessionId { get; }

    public DateTimeOffset ExpiresAt { get; }

    public IReadOnlyList<PairingEndpoint> Endpoints { get; }

    public ReadOnlyMemory<byte> TlsSpkiSha256 => GetBytes(_tlsSpkiSha256);

    public ReadOnlyMemory<byte> ServerIdentityKey => GetBytes(_serverIdentityKey);

    public ReadOnlyMemory<byte> OfferNonce => GetBytes(_offerNonce);

    public ReadOnlyMemory<byte> PairingSecret => GetBytes(_pairingSecret);

    public void Dispose()
    {
        ZeroAndRelease(ref _tlsSpkiSha256);
        ZeroAndRelease(ref _serverIdentityKey);
        ZeroAndRelease(ref _offerNonce);
        ZeroAndRelease(ref _pairingSecret);
    }

    private static ReadOnlyMemory<byte> GetBytes(byte[]? value) =>
        value ?? throw new ObjectDisposedException(nameof(PairingOffer));

    private static void RequireLength(ReadOnlySpan<byte> value, int length, string parameterName)
    {
        if (value.Length != length)
        {
            throw new ArgumentException($"The value must contain exactly {length} bytes.", parameterName);
        }
    }

    private static void ZeroAndRelease(ref byte[]? value)
    {
        if (value is null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(value);
        value = null;
    }
}

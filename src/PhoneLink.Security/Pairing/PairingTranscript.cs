using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PhoneLink.Security.Pairing;

public static class PairingTranscript
{
    private static readonly byte[] ClientDomain = Encoding.ASCII.GetBytes("PhoneLink-Pairing-Client-v1");
    private static readonly byte[] ServerDomain = Encoding.ASCII.GetBytes("PhoneLink-Pairing-Server-v1");
    private static readonly byte[] CompareDomain = Encoding.ASCII.GetBytes("PhoneLink-Pairing-Compare-v1");

    public static byte[] CreateClientDigest(
        ReadOnlySpan<byte> rawOfferJson,
        string clientDeviceId,
        ReadOnlySpan<byte> clientIdentityPublicKey,
        ReadOnlySpan<byte> clientNonce)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientDeviceId);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendLengthPrefixed(hash, ClientDomain);
        AppendLengthPrefixed(hash, rawOfferJson);
        AppendLengthPrefixed(hash, Encoding.UTF8.GetBytes(clientDeviceId));
        AppendLengthPrefixed(hash, clientIdentityPublicKey);
        AppendLengthPrefixed(hash, clientNonce);
        return hash.GetHashAndReset();
    }

    public static byte[] CreateClientProof(
        ReadOnlySpan<byte> pairingSecret,
        ReadOnlySpan<byte> clientDigest) =>
        HMACSHA256.HashData(pairingSecret, clientDigest);

    public static bool VerifyClientProof(
        ReadOnlySpan<byte> pairingSecret,
        ReadOnlySpan<byte> clientDigest,
        ReadOnlySpan<byte> proof)
    {
        Span<byte> expected = stackalloc byte[32];
        HMACSHA256.HashData(pairingSecret, clientDigest, expected);
        return proof.Length == expected.Length && CryptographicOperations.FixedTimeEquals(expected, proof);
    }

    public static byte[] CreateServerSignatureInput(
        ReadOnlySpan<byte> rawOfferJson,
        string clientDeviceId,
        ReadOnlySpan<byte> clientIdentityPublicKey,
        ReadOnlySpan<byte> clientNonce,
        ReadOnlySpan<byte> serverNonce)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientDeviceId);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendLengthPrefixed(hash, ServerDomain);
        AppendLengthPrefixed(hash, rawOfferJson);
        AppendLengthPrefixed(hash, Encoding.UTF8.GetBytes(clientDeviceId));
        AppendLengthPrefixed(hash, clientIdentityPublicKey);
        AppendLengthPrefixed(hash, clientNonce);
        AppendLengthPrefixed(hash, serverNonce);
        return hash.GetHashAndReset();
    }

    public static byte[] CreateComparisonDigest(
        ReadOnlySpan<byte> serverSignatureInput,
        ReadOnlySpan<byte> serverSignature)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(CompareDomain);
        hash.AppendData(serverSignatureInput);
        hash.AppendData(serverSignature);
        return hash.GetHashAndReset();
    }

    private static void AppendLengthPrefixed(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)value.Length));
        hash.AppendData(length);
        hash.AppendData(value);
    }
}

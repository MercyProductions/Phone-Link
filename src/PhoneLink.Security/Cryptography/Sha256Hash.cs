using System.Security.Cryptography;

namespace PhoneLink.Security.Cryptography;

public static class Sha256Hash
{
    public const int Length = 32;

    public static byte[] Compute(ReadOnlySpan<byte> value) => SHA256.HashData(value);

    public static bool FixedTimeEquals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) =>
        left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
}


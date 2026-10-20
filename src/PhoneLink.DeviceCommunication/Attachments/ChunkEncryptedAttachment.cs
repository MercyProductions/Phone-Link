using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PhoneLink.DeviceCommunication.Attachments;

// PLA2: authenticated header followed by independently authenticated, ordered chunks.
// Each file has a fresh key. The nonce's final four bytes encode the chunk index.
internal static class ChunkEncryptedAttachment
{
    internal const int HeaderBytes = 28;

    internal static byte[] CreateHeader(int chunkSize, long byteCount, byte[] nonce)
    {
        var header = new byte[HeaderBytes];
        "PLA2"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), chunkSize);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(8), byteCount);
        nonce.CopyTo(header, 16);
        return header;
    }

    internal static async Task WriteChunkAsync(Stream stream, byte[] header, byte[] key,
        byte[] hash, uint index, byte[] plaintext, CancellationToken cancellationToken)
    {
        var encrypted = new byte[plaintext.Length + 16];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(Nonce(header, index), plaintext, encrypted.AsSpan(0, plaintext.Length),
                encrypted.AsSpan(plaintext.Length), AssociatedData(header, hash, index, plaintext.Length));
            await stream.WriteAsync(encrypted, cancellationToken).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(encrypted); }
    }

    internal static async Task CopyDecryptedAsync(Stream source, Stream destination,
        ReadOnlyMemory<byte> key, byte[] expectedNonce, byte[] expectedHash, long expectedBytes,
        CancellationToken cancellationToken)
    {
        var header = new byte[HeaderBytes];
        await source.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var chunkSize = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
        var bytes = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(8));
        if (!header.AsSpan(0, 4).SequenceEqual("PLA2"u8) ||
            chunkSize is < 16_384 or > 262_144 || bytes != expectedBytes || bytes <= 0 ||
            bytes > (long)chunkSize * uint.MaxValue ||
            !header.AsSpan(16, 12).SequenceEqual(expectedNonce))
            throw new CryptographicException("The encrypted attachment header is invalid.");

        var encrypted = new byte[chunkSize + 16];
        var plaintext = new byte[chunkSize];
        using var aes = new AesGcm(key.Span, 16);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            uint index = 0;
            long offset = 0;
            while (offset < bytes)
            {
                var length = (int)Math.Min(chunkSize, bytes - offset);
                await source.ReadExactlyAsync(encrypted.AsMemory(0, length + 16), cancellationToken).ConfigureAwait(false);
                aes.Decrypt(Nonce(header, index), encrypted.AsSpan(0, length),
                    encrypted.AsSpan(length, 16), plaintext.AsSpan(0, length),
                    AssociatedData(header, expectedHash, index, length));
                hash.AppendData(plaintext.AsSpan(0, length));
                await destination.WriteAsync(plaintext.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                CryptographicOperations.ZeroMemory(plaintext);
                offset += length;
                index++;
            }
            if (source.ReadByte() != -1 ||
                !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expectedHash))
                throw new CryptographicException("The encrypted attachment failed its final integrity check.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    private static byte[] Nonce(byte[] header, uint index)
    {
        var nonce = header.AsSpan(16, 12).ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), index);
        return nonce;
    }

    private static byte[] AssociatedData(byte[] header, byte[] hash, uint index, int length)
    {
        var data = new byte[HeaderBytes + 32 + 8];
        header.CopyTo(data, 0);
        hash.CopyTo(data, HeaderBytes);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(HeaderBytes + 32), index);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(HeaderBytes + 36), length);
        return data;
    }
}

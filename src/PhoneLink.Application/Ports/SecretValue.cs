using System.Security.Cryptography;

namespace PhoneLink.Application.Ports;

public sealed class SecretValue : IDisposable
{
    private byte[]? _bytes;

    public SecretValue(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            throw new ArgumentException("A secret value cannot be empty.", nameof(value));
        }

        _bytes = value.ToArray();
    }

    public ReadOnlyMemory<byte> Bytes => _bytes
        ?? throw new ObjectDisposedException(nameof(SecretValue));

    public void Dispose()
    {
        if (_bytes is null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_bytes);
        _bytes = null;
    }

    public override string ToString() => "[protected secret]";
}


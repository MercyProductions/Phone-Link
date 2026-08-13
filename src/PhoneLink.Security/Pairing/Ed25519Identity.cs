using System.Security.Cryptography;
using NSec.Cryptography;

namespace PhoneLink.Security.Pairing;

public sealed class Ed25519Identity : IDisposable
{
    private static readonly SignatureAlgorithm Algorithm = SignatureAlgorithm.Ed25519;
    private Key? _key;

    private Ed25519Identity(Key key)
    {
        _key = key;
    }

    public byte[] ExportPublicKey() =>
        GetKey().PublicKey.Export(KeyBlobFormat.RawPublicKey);

    public byte[] ExportPrivateKey() =>
        GetKey().Export(KeyBlobFormat.RawPrivateKey);

    public byte[] Sign(ReadOnlySpan<byte> data) =>
        Algorithm.Sign(GetKey(), data);

    public static Ed25519Identity Create()
    {
        var parameters = CreateExportableKeyParameters();
        return new Ed25519Identity(Key.Create(Algorithm, parameters));
    }

    public static Ed25519Identity ImportPrivateKey(ReadOnlySpan<byte> privateKey)
    {
        var parameters = CreateExportableKeyParameters();
        return new Ed25519Identity(
            Key.Import(Algorithm, privateKey, KeyBlobFormat.RawPrivateKey, parameters));
    }

    public static bool Verify(
        ReadOnlySpan<byte> publicKey,
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> signature)
    {
        var imported = PublicKey.Import(Algorithm, publicKey, KeyBlobFormat.RawPublicKey);
        return Algorithm.Verify(imported, data, signature);
    }

    public void Dispose()
    {
        _key?.Dispose();
        _key = null;
    }

    private Key GetKey() =>
        _key ?? throw new ObjectDisposedException(nameof(Ed25519Identity));

    private static KeyCreationParameters CreateExportableKeyParameters() =>
        new() { ExportPolicy = KeyExportPolicies.AllowPlaintextExport };
}

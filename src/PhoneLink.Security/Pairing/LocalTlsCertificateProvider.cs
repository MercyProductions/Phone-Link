using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PhoneLink.Application.Ports;

namespace PhoneLink.Security.Pairing;

public sealed class LocalTlsCertificateProvider
{
    private static readonly SecretName CertificateSecretName = new("tls/local-server-pkcs12-v1");
    private const X509KeyStorageFlags WindowsServerKeyFlags =
        X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable;
    private readonly ISecretStore _secretStore;

    public LocalTlsCertificateProvider(ISecretStore secretStore)
    {
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
    }

    public async Task<X509Certificate2> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        using var stored = await _secretStore
            .RetrieveAsync(CertificateSecretName, cancellationToken)
            .ConfigureAwait(false);
        if (stored is not null)
        {
            return X509CertificateLoader.LoadPkcs12(
                stored.Bytes.Span,
                password: null,
                WindowsServerKeyFlags);
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            "CN=Phone Link Local",
            key,
            HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") },
                critical: true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        names.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(names.Build());

        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(5));
        var exported = generated.Export(X509ContentType.Pkcs12);
        try
        {
            await _secretStore
                .StoreAsync(CertificateSecretName, exported, cancellationToken)
                .ConfigureAwait(false);
            return X509CertificateLoader.LoadPkcs12(
                exported,
                password: null,
                WindowsServerKeyFlags);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(exported);
        }
    }

    public static byte[] GetSubjectPublicKeyInfoSha256(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        using var publicKey = certificate.GetECDsaPublicKey()
            ?? throw new CryptographicException("The Phone Link TLS certificate must use ECDSA.");
        return SHA256.HashData(publicKey.ExportSubjectPublicKeyInfo());
    }
}

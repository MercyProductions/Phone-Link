using System.Security.Cryptography;
using PhoneLink.Application.Ports;

namespace PhoneLink.Security.Pairing;

public sealed class WindowsIdentityKeyProvider
{
    private static readonly SecretName IdentitySecretName = new("identity/windows-ed25519-v1");
    private readonly ISecretStore _secretStore;

    public WindowsIdentityKeyProvider(ISecretStore secretStore)
    {
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
    }

    public async Task<Ed25519Identity> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        using var storedKey = await _secretStore
            .RetrieveAsync(IdentitySecretName, cancellationToken)
            .ConfigureAwait(false);

        if (storedKey is not null)
        {
            return Ed25519Identity.ImportPrivateKey(storedKey.Bytes.Span);
        }

        var identity = Ed25519Identity.Create();
        var privateKey = identity.ExportPrivateKey();
        try
        {
            await _secretStore
                .StoreAsync(IdentitySecretName, privateKey, cancellationToken)
                .ConfigureAwait(false);
            return identity;
        }
        catch
        {
            identity.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }
}

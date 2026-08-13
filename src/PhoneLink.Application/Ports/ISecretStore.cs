namespace PhoneLink.Application.Ports;

public interface ISecretStore
{
    Task StoreAsync(
        SecretName name,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken);

    Task<SecretValue?> RetrieveAsync(
        SecretName name,
        CancellationToken cancellationToken);

    Task DeleteAsync(SecretName name, CancellationToken cancellationToken);
}


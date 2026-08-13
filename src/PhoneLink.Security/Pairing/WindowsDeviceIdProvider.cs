using System.Text;
using PhoneLink.Application.Ports;

namespace PhoneLink.Security.Pairing;

public sealed class WindowsDeviceIdProvider
{
    private static readonly SecretName DeviceIdSecretName = new("identity/windows-device-id-v1");
    private readonly ISecretStore _secretStore;

    public WindowsDeviceIdProvider(ISecretStore secretStore)
    {
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
    }

    public async Task<Guid> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        using var stored = await _secretStore
            .RetrieveAsync(DeviceIdSecretName, cancellationToken)
            .ConfigureAwait(false);
        if (stored is not null)
        {
            var text = Encoding.UTF8.GetString(stored.Bytes.Span);
            if (Guid.TryParseExact(text, "D", out var existing) && existing != Guid.Empty)
            {
                return existing;
            }

            throw new InvalidOperationException("The protected Windows device identifier is invalid.");
        }

        var deviceId = Guid.NewGuid();
        await _secretStore
            .StoreAsync(
                DeviceIdSecretName,
                Encoding.UTF8.GetBytes(deviceId.ToString("D")),
                cancellationToken)
            .ConfigureAwait(false);
        return deviceId;
    }
}

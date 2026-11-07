using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace PhoneLink.Integrations.AppleBluetooth.Media;

// Only for the uncached characteristic enumeration after a user-started, authorized shared open.
// Never use this for permission requests, subscription writes or playback commands.
internal static class AmsSetupRecovery
{
    internal static async Task<(T Result, bool Retried)> ReadCharacteristicsAsync<T>(
        Func<CancellationToken, Task<T>> read, Func<T, GattCommunicationStatus> status,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await read(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (status(result) != GattCommunicationStatus.Unreachable) return (result, false);
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        result = await read(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return (result, true);
    }
}

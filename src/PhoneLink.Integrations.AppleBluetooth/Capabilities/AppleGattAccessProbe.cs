using PhoneLink.Integrations.AppleBluetooth.Ancs;
using PhoneLink.Integrations.AppleBluetooth.Media;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace PhoneLink.Integrations.AppleBluetooth.Capabilities;

public sealed record AppleGattProfileCheck(string Profile, string Discovery = "Not attempted", int? ServiceCount = null,
    string ServiceAccess = "Not attempted", string SharedOpen = "Not attempted",
    string Characteristics = "Not attempted", int? CharacteristicCount = null);

public sealed record AppleGattAccessReport(string EndpointAccess, string Endpoint, IReadOnlyList<AppleGattProfileCheck> Profiles)
{
    public string ToDisplayText() =>
        $"Paired endpoint access: {EndpointAccess}. Endpoint: {Endpoint}.\n" +
        string.Join("\n", Profiles.Select(profile =>
            $"{profile.Profile} — discovery: {profile.Discovery}{Count(profile.ServiceCount)}; existing service access: {profile.ServiceAccess}; " +
            $"shared open: {profile.SharedOpen}; characteristic enumeration: {profile.Characteristics}{Count(profile.CharacteristicCount)}.")) +
        "\nThis does not verify working notifications, media commands, or the cause of a denial. No access request, characteristic value read or write, or subscription was sent.";

    private static string Count(int? count) => count is { } value ? $" ({value})" : "";
}

public sealed record BluetoothEndpointAccessInventory(int Paired, int Allowed, int DeniedByUser, int DeniedBySystem, int Unknown);

public static class AppleGattAccessProbe
{
    private const string Notifications = "Notifications (ANCS)";
    private const string Media = "Media (AMS)";

    public static async Task<BluetoothEndpointAccessInventory> CountEndpointAccessAsync(CancellationToken cancellationToken)
    {
        var targets = await PairedBluetoothLeTarget.ListAsync(cancellationToken).ConfigureAwait(false);
        List<DeviceAccessStatus?> statuses = [];
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            statuses.Add(ReadEndpointAccess(target));
        }
        return CountStatuses(statuses);
    }

    internal static BluetoothEndpointAccessInventory CountStatuses(IReadOnlyList<DeviceAccessStatus?> statuses)
    {
        var allowed = statuses.Count(status => status == DeviceAccessStatus.Allowed);
        var user = statuses.Count(status => status == DeviceAccessStatus.DeniedByUser);
        var system = statuses.Count(status => status == DeviceAccessStatus.DeniedBySystem);
        return new(statuses.Count, allowed, user, system, statuses.Count - allowed - user - system);
    }

    // Call from the UI context for BluetoothLEDevice.FromIdAsync.
    public static async Task<AppleGattAccessReport> InspectAsync(PairedBluetoothLeTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        var status = ReadEndpointAccess(target);
        if (!CanContinue(status)) return NotOpened(status, "Skipped; existing access was not confirmed");

        try
        {
            using var endpointDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            endpointDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            using var device = await BluetoothLEDevice.FromIdAsync(target.Id).AsTask(endpointDeadline.Token);
            cancellationToken.ThrowIfCancellationRequested();
            if (device is null || !device.DeviceInformation.Pairing.IsPaired)
                return NotOpened(status, "Unavailable or no longer paired");
            var notifications = await InspectProfileAsync(device, Notifications, AncsNotificationSession.ServiceUuid, cancellationToken);
            var media = await InspectProfileAsync(device, Media, AmsSession.ServiceUuid, cancellationToken);
            return new(DescribeAccess(status), "Paired device opened for inspection", [notifications, media]);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return NotOpened(status, Failure(exception));
        }
    }

    private static async Task<AppleGattProfileCheck> InspectProfileAsync(BluetoothLEDevice device, string profile,
        Guid uuid, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var token = deadline.Token;
        var step = 0;
        var report = new AppleGattProfileCheck(profile);
        try
        {
            token.ThrowIfCancellationRequested();
            var result = await device.GetGattServicesForUuidAsync(uuid, BluetoothCacheMode.Uncached).AsTask(token);
            try
            {
                report = report with { Discovery = result.Status.ToString() };
                if (result.Status != GattCommunicationStatus.Success) return report;
                report = report with { ServiceCount = result.Services.Count };
                if (result.Services.Count != 1) return report;
                step = 1;
                token.ThrowIfCancellationRequested();
                var service = result.Services[0];
                var access = service.DeviceAccessInformation.CurrentStatus;
                report = report with { ServiceAccess = DescribeAccess(access) };
                if (!CanContinue(access)) return report;
                step = 2;
                token.ThrowIfCancellationRequested();
                var open = await service.OpenAsync(GattSharingMode.SharedReadAndWrite).AsTask(token);
                report = report with { SharedOpen = open.ToString() };
                if (!GattServiceAuthorization.IsSharedOpen(open)) return report;
                step = 3;
                token.ThrowIfCancellationRequested();
                var characteristics = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(token);
                return report with { Characteristics = characteristics.Status.ToString(),
                    CharacteristicCount = characteristics.Status == GattCommunicationStatus.Success ? characteristics.Characteristics.Count : null };
            }
            finally { foreach (var service in result.Services) service.Dispose(); }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var failure = Failure(exception);
            return step switch
            {
                1 => report with { ServiceAccess = failure },
                2 => report with { SharedOpen = failure },
                3 => report with { Characteristics = failure, CharacteristicCount = null },
                _ => report with { Discovery = failure },
            };
        }
    }

    private static AppleGattAccessReport NotOpened(DeviceAccessStatus? status, string reason) =>
        new(DescribeAccess(status), reason, [new(Notifications), new(Media)]);

    private static DeviceAccessStatus? ReadEndpointAccess(PairedBluetoothLeTarget target)
    {
        try { return DeviceAccessInformation.CreateFromId(target.Id).CurrentStatus; }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return null; }
    }

    internal static bool CanContinue(DeviceAccessStatus? status) => status == DeviceAccessStatus.Allowed;
    private static string DescribeAccess(DeviceAccessStatus? status) => status?.ToString() ?? "Unavailable";
    private static string Failure(Exception exception) => exception switch
    {
        OperationCanceledException => "Timed out",
        { HResult: unchecked((int)0x80070005) } => "AccessDenied",
        _ => "Unavailable",
    };
}

using PhoneLink.Application.Features.Diagnostics;
using PhoneLink.DeviceCommunication.Discovery;
using Windows.Devices.Enumeration;
using Windows.Management.Deployment;

namespace PhoneLink.DeviceCommunication.Diagnostics;

public static class WindowsConnectionDiagnosticProbe
{
    private static readonly string[] Properties =
        ["System.Devices.HardwareIds", "System.Devices.DeviceHasProblem", "System.Devices.Present"];

    public static async Task<ConnectionDiagnosticSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<string> unavailable = [];
        int? usbCount = null, problemCount = null, unknownCount = null, endpointCount = null;
        bool? appleDevicesInstalled = null;
        string? version = null;
        try
        {
            var devices = await DeviceInformation.FindAllAsync(
                "System.Devices.Present:=System.StructuredQueryType.Boolean#True", Properties,
                DeviceInformationKind.Device).AsTask(cancellationToken).ConfigureAwait(false);
            usbCount = problemCount = unknownCount = 0;
            foreach (var device in devices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ids = device.Properties.TryGetValue("System.Devices.HardwareIds", out var hardwareIds)
                    && hardwareIds is string[] strings ? strings : [];
                if (!IsAppleUsbNode(device.Id, ids)) continue;
                usbCount++;
                if (device.Properties.TryGetValue("System.Devices.DeviceHasProblem", out var value) && value is bool problem)
                {
                    if (problem) problemCount++;
                }
                else unknownCount++;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            usbCount = problemCount = unknownCount = null;
            unavailable.Add("usb-query-unavailable");
        }
        try
        {
            (appleDevicesInstalled, version) = await Task.Run(() =>
            {
                foreach (var package in new PackageManager().FindPackagesForUser(string.Empty))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!string.Equals(package.Id.Name, "AppleInc.AppleDevices", StringComparison.OrdinalIgnoreCase)) continue;
                    var v = package.Id.Version;
                    return (true, $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}");
                }
                return (false, (string?)null);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException) { unavailable.Add("app-inventory-unavailable"); }
        try { endpointCount = LocalNetworkCatalog.SelectPairingAddresses(LocalNetworkCatalog.Capture()).Length; }
        catch (Exception exception) when (exception is not OutOfMemoryException) { unavailable.Add("network-query-unavailable"); }
        cancellationToken.ThrowIfCancellationRequested();
        return new(DateTimeOffset.UtcNow, usbCount, problemCount, unknownCount,
            appleDevicesInstalled, version, endpointCount, null, null, unavailable);
    }

    public static bool IsAppleUsbNode(string instanceId, IEnumerable<string> hardwareIds)
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        ArgumentNullException.ThrowIfNull(hardwareIds);
        static bool Match(string value) => value.StartsWith("USB\\VID_05AC&", StringComparison.OrdinalIgnoreCase);
        return Match(instanceId) || hardwareIds.Any(Match);
    }
}

using PhoneLink.Application.Features.Diagnostics;
using PhoneLink.DeviceCommunication.Discovery;
using System.Net.Sockets;
using System.Xml;
using Windows.Devices.Enumeration;
using Windows.Management.Deployment;

namespace PhoneLink.DeviceCommunication.Diagnostics;

public static class WindowsConnectionDiagnosticProbe
{
    // DEVPKEY_Device_ProblemCode, Windows SDK 10.0.26100 devpkey.h; custom WinRT property syntax.
    private const string ProblemCodeProperty = "{4340a6c5-93fa-4706-972c-7b648008a5a7} 3";
    private static readonly string[] Properties =
        ["System.Devices.HardwareIds", "System.Devices.DeviceHasProblem", "System.Devices.Present", ProblemCodeProperty];

    public static async Task<ConnectionDiagnosticSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<string> unavailable = [];
        int? usbCount = null, problemCount = null, unknownCount = null, endpointCount = null;
        bool? appleDevicesInstalled = null;
        string? version = null;
        Dictionary<uint, int> problemCodes = [];
        int? unavailableCodes = null;
        try
        {
            var devices = await DeviceInformation.FindAllAsync(
                "System.Devices.Present:=System.StructuredQueryType.Boolean#True", Properties,
                DeviceInformationKind.Device).AsTask(cancellationToken).ConfigureAwait(false);
            usbCount = problemCount = unknownCount = 0;
            unavailableCodes = 0;
            foreach (var device in devices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ids = device.Properties.TryGetValue("System.Devices.HardwareIds", out var hardwareIds)
                    && hardwareIds is string[] strings ? strings : [];
                if (!IsAppleUsbNode(device.Id, ids)) continue;
                usbCount++;
                var code = device.Properties.TryGetValue(ProblemCodeProperty, out var rawCode)
                    ? ParseProblemCode(rawCode) : null;
                if (code is { } numeric) problemCodes[numeric] = problemCodes.GetValueOrDefault(numeric) + 1;
                else unavailableCodes++;
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
            unavailableCodes = null;
            problemCodes.Clear();
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
        var usbTransport = await CaptureUsbTransportAsync(ExperimentalUsbMuxProbe.ProbeAsync, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new(DateTimeOffset.UtcNow, usbCount, problemCount, unknownCount,
            appleDevicesInstalled, version, endpointCount, null, null, unavailable)
        {
            UsbProblemCodes = usbCount is null ? null : problemCodes.OrderBy(item => item.Key)
                .Select(item => new UsbDeviceProblemCount(item.Key, item.Value)).ToArray(),
            UsbNodesWithUnavailableProblemCode = unavailableCodes,
            UsbTransport = usbTransport,
        };
    }

    internal static async Task<UsbTransportObservation> CaptureUsbTransportAsync(
        Func<CancellationToken, Task<UsbMuxInventory>> probe, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inventory = await probe(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new(UsbTransportState.Responded, inventory.UsbConnections);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(exception switch
            {
                SocketException { SocketErrorCode: SocketError.ConnectionRefused } => UsbTransportState.ConnectionRefused,
                OperationCanceledException or SocketException { SocketErrorCode: SocketError.TimedOut } => UsbTransportState.TimedOut,
                InvalidDataException or EndOfStreamException or XmlException => UsbTransportState.InvalidReply,
                _ => UsbTransportState.Unavailable,
            });
        }
    }

    // SDK declares UInt32; accept nonnegative Int32 too, as documentation describes Int32.
    // Do not coerce strings, booleans, negative values, or missing properties into healthy zero.
    public static uint? ParseProblemCode(object? value) => value switch
    {
        uint code => code,
        int code when code >= 0 => (uint)code,
        _ => null,
    };

    public static bool IsAppleUsbNode(string instanceId, IEnumerable<string> hardwareIds)
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        ArgumentNullException.ThrowIfNull(hardwareIds);
        static bool Match(string value) => value.StartsWith("USB\\VID_05AC&", StringComparison.OrdinalIgnoreCase);
        return Match(instanceId) || hardwareIds.Any(Match);
    }
}

using System.Globalization;

namespace PhoneLink.Application.Features.Diagnostics;

public static class UsbProblemGuidance
{
    public static Uri Reference { get; } = new("https://learn.microsoft.com/en-us/windows-hardware/drivers/install/device-manager-error-messages");

    public static DiagnosticFinding Create(UsbDeviceProblemCount observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var (meaning, next) = observation.Code switch
        {
            10 => ("Windows could not start the device", "Reconnect using a known data-capable cable and direct USB port, then check official Windows/Apple updates if the problem persists."),
            14 => ("Windows reports that a computer restart is required", "Save your work and review Device Manager before choosing whether to restart Windows. The app will not restart it."),
            22 => ("Windows reports that the device is disabled", "Review the matching Apple USB device in Device Manager. Enable it only if you intentionally want that device enabled; the app will not do so automatically."),
            28 => ("Windows reports that device drivers are not installed", "Check official Apple Devices and Windows updates. Avoid third-party driver packs; the app will not install or replace drivers."),
            43 => ("A Windows driver reported a device failure", "Try a known data-capable cable and a direct USB port, then compare Device Manager after reconnecting. This code alone cannot identify a damaged cable, phone component or driver."),
            _ => ("Windows reported a device problem", "Review this numeric code in Device Manager and Microsoft's official code reference before selecting a repair. No generic reset or driver replacement was attempted."),
        };
        var code = observation.Code.ToString(CultureInfo.InvariantCulture);
        return new($"usb-problem-{code}", $"Apple USB — Device Manager code {code}", DiagnosticOutcome.Attention,
            $"{observation.NodeCount.ToString(CultureInfo.InvariantCulture)} node(s): {meaning}. Apple-vendor nodes are not necessarily separate iPhones; this is not a hardware-fault diagnosis.", next, Reference);
    }
}

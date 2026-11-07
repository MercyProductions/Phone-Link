using System.Text.Json;
using PhoneLink.Application.Features.Diagnostics;
using PhoneLink.Integrations.AppleBluetooth.Calling;
using PhoneLink.Integrations.AppleBluetooth.Capabilities;
using PhoneLink.DeviceCommunication.Diagnostics;
using PhoneLink.DeviceCommunication.Tunnels;

if (args.Length == 2 && args[0] == "--forward" && ushort.TryParse(args[1], out var port) && port > 0)
{
    using var lifetime = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; lifetime.Cancel(); };
    Console.CancelKeyPress += cancel;
    try
    {
        using var setup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connector = await PinnedUsbDeviceConnector.SelectSingleAsync(setup.Token);
        await using var tunnel = connector.StartMonitoredForwarder(port, lifetime.Token);
        Console.WriteLine($"Loopback forwarder: 127.0.0.1:{tunnel.LocalPort} -> pinned USB device port {port}. Ctrl+C stops it.");
        Console.WriteLine("Two consecutive inventories without exactly one matching USB target also stop it.");
        Console.WriteLine("Forwarding does not install a server, authenticate SSH, or bypass device permissions. Use normal SSH host-key verification.");
        await tunnel.Completion;
        await tunnel.DisposeAsync();
        if (tunnel.TargetState == TunnelTargetState.StoppedAfterTargetLoss)
            Console.WriteLine("Stopped automatically: pinned USB device was no longer uniquely present. No port is listening.");
        Console.WriteLine(JsonSerializer.Serialize(tunnel.CaptureStatistics()));
        return 0;
    }
    catch (Exception exception) when (exception is not OutOfMemoryException)
    {
        Console.Error.WriteLine($"Forwarder stopped ({exception.GetType().Name}); no target replacement or login was attempted.");
        return 1;
    }
    finally { Console.CancelKeyPress -= cancel; }
}

ushort sshPort = 22;
var explicitSshPort = args.Length == 2 && args[0] is "--ssh-check" or "--verify-ssh-tunnel" &&
    ushort.TryParse(args[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out sshPort) && sshPort > 0;
if (!explicitSshPort && (args.Length != 1 || args[0] is not ("--inventory" or "--info" or "--ssh-check" or "--verify-ssh-tunnel" or "--verify-tunnel" or "--developer-mode-status" or "--connection-check" or "--call-transport" or "--bluetooth-profile-check" or "--bluetooth-battery-inventory")))
{
    Console.Error.WriteLine("Probes: --inventory, --info, --ssh-check [device-port], --verify-ssh-tunnel [device-port], --verify-tunnel, --developer-mode-status, --connection-check, --call-transport, --bluetooth-profile-check, --bluetooth-battery-inventory. Managed transport: --forward <device-port>. No logins or state-changing phone commands are sent automatically.");
    return 2;
}
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
try
{
    object result = args[0] switch
    {
        "--inventory" => await ExperimentalUsbMuxProbe.ProbeAsync(cancellation.Token),
        "--bluetooth-profile-check" => await WindowsClassicPhoneProfiles.InspectAsync(cancellation.Token),
        "--bluetooth-battery-inventory" => new
        {
            PairedBluetoothLeDevices = (await BluetoothBatteryProbe.ListPairedAsync(cancellation.Token)).Count,
            BatteryReadAttempted = false,
        },
        "--call-transport" => (await WindowsCallTransportProbe.CaptureAsync(cancellation.Token)).ToDisplayText(),
        "--info" => await ExperimentalIphoneInformationProbe.ProbeAsync(cancellation.Token),
        "--ssh-check" => await ExperimentalSshAvailabilityProbe.ProbeAsync(sshPort, cancellation.Token),
        "--verify-ssh-tunnel" => await ForwardedSshAvailabilityProbe.VerifyUsbAsync(sshPort, cancellation.Token),
        "--developer-mode-status" => await ExperimentalIphoneInformationProbe.ProbeDeveloperModeAsync(cancellation.Token),
        "--connection-check" => ConnectionDiagnosticEvaluator.Evaluate(
            await WindowsConnectionDiagnosticProbe.CaptureAsync(cancellation.Token)).ToShareableText(),
        _ => await UsbTunnelVerifier.VerifyAsync(cancellation.Token),
    };
    Console.WriteLine(result is string text ? text : JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception exception) when (exception is not OutOfMemoryException)
{
    // No raw XML, pairing material, or device identifiers in diagnostic errors.
    Console.Error.WriteLine($"Read-only probe could not complete ({exception.GetType().Name}). Check the connection and iPhone's trust/lock state; no repair was attempted.");
    return 1;
}

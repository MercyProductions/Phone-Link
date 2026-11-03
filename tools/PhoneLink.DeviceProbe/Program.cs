using System.Text.Json;
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
        await using var tunnel = LoopbackDeviceTunnel.Start(token => connector.OpenChannelAsync(port, token), cancellationToken: lifetime.Token);
        Console.WriteLine($"Loopback forwarder: 127.0.0.1:{tunnel.LocalPort} -> pinned USB device port {port}. Ctrl+C stops it.");
        Console.WriteLine("Forwarding does not install a server, authenticate SSH, or bypass device permissions. Use normal SSH host-key verification.");
        await tunnel.Completion;
        await tunnel.DisposeAsync();
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

if (args.Length != 1 || args[0] is not ("--inventory" or "--info" or "--ssh-check" or "--verify-tunnel"))
{
    Console.Error.WriteLine("Probes: --inventory, --info, --ssh-check, --verify-tunnel. Managed transport: --forward <device-port>. No logins or commands are sent automatically.");
    return 2;
}
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
try
{
    object result = args[0] switch
    {
        "--inventory" => await ExperimentalUsbMuxProbe.ProbeAsync(cancellation.Token),
        "--info" => await ExperimentalIphoneInformationProbe.ProbeAsync(cancellation.Token),
        "--ssh-check" => await ExperimentalSshAvailabilityProbe.ProbeAsync(cancellation.Token),
        _ => await UsbTunnelVerifier.VerifyAsync(cancellation.Token),
    };
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception exception) when (exception is not OutOfMemoryException)
{
    // No raw XML, pairing material, or device identifiers in diagnostic errors.
    Console.Error.WriteLine($"Read-only probe could not complete ({exception.GetType().Name}). Check the connection and iPhone's trust/lock state; no repair was attempted.");
    return 1;
}

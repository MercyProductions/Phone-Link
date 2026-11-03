using System.Text.Json;
using PhoneLink.DeviceCommunication.Diagnostics;

if (args.Length != 1 || args[0] is not ("--inventory" or "--info" or "--ssh-check"))
{
    Console.Error.WriteLine("Explicit probes: --inventory, --info, or --ssh-check. No logins or arbitrary commands are accepted.");
    return 2;
}
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
try
{
    object result = args[0] switch
    {
        "--inventory" => await ExperimentalUsbMuxProbe.ProbeAsync(cancellation.Token),
        "--info" => await ExperimentalIphoneInformationProbe.ProbeAsync(cancellation.Token),
        _ => await ExperimentalSshAvailabilityProbe.ProbeAsync(cancellation.Token),
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

using System.Net;
using System.Net.Sockets;
using PhoneLink.DeviceCommunication.Tunnels;

namespace PhoneLink.DeviceCommunication.Diagnostics;

/// <summary>Identification only, through this exact forwarder. No credentials, keys or commands.</summary>
public static class ForwardedSshAvailabilityProbe
{
    public static async Task<SshAvailability> ProbeAsync(LoopbackDeviceTunnel tunnel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tunnel);
        cancellationToken.ThrowIfCancellationRequested();
        if (!tunnel.IsRunning || tunnel.StoppingToken.IsCancellationRequested)
            return Stopped();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, tunnel.StoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        using var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, tunnel.LocalPort, timeout.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            await stream.WriteAsync("SSH-2.0-PhoneLinkProbe_1\r\n"u8.ToArray(), timeout.Token).ConfigureAwait(false);
            var result = await ExperimentalSshAvailabilityProbe.ReadIdentificationAsync(stream, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            return tunnel.IsRunning ? result : Stopped();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return tunnel.StoppingToken.IsCancellationRequested ? Stopped() :
                new(false, "The forwarded SSH identification check timed out. Server availability is unknown; no login was attempted.", null, null);
        }
        catch (Exception exception) when (exception is IOException or SocketException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return tunnel.StoppingToken.IsCancellationRequested ? Stopped() :
                new(false, "The forwarded channel failed before a supported SSH identification was verified. Inspect tunnel failure counters; no login was attempted.", null, null);
        }
    }

    private static SshAvailability Stopped() => new(false,
        "The selected forwarder stopped. Any earlier SSH observation is stale; no login was attempted.", null, null);
}

using System.Net;
using System.Net.Sockets;
using PhoneLink.DeviceCommunication.Tunnels;

namespace PhoneLink.DeviceCommunication.Diagnostics;

public sealed record UsbSshTunnelVerification(ushort DevicePort, SshAvailability Availability, TunnelStatistics Statistics);

/// <summary>Identification only, through this exact forwarder. No credentials, keys or commands.</summary>
public static class ForwardedSshAvailabilityProbe
{
    public static async Task<UsbSshTunnelVerification> VerifyUsbAsync(ushort devicePort, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfZero(devicePort);
        var connector = await PinnedUsbDeviceConnector.SelectSingleAsync(cancellationToken).ConfigureAwait(false);
        return await VerifyUsbAsync(devicePort, connector.OpenChannelAsync, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<UsbSshTunnelVerification> VerifyUsbAsync(ushort devicePort,
        Func<ushort, CancellationToken, Task<TunnelConnection>> connect, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfZero(devicePort);
        ArgumentNullException.ThrowIfNull(connect);
        await using var tunnel = LoopbackDeviceTunnel.Start(token => connect(devicePort, token),
            maximumConnections: 1, cancellationToken: cancellationToken);
        var availability = await ProbeAsync(tunnel, cancellationToken).ConfigureAwait(false);
        await tunnel.DisposeAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var stats = tunnel.CaptureStatistics();
        if (!availability.ServerIdentified && stats.LastUsbResponseCode is { } code)
            availability = new(false,
                "The temporary forwarder recorded a USB channel rejection. No SSH identification or login was established.",
                null, code);
        return new(devicePort, availability, stats);
    }

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

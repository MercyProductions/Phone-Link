using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using PhoneLink.DeviceCommunication.Diagnostics;

namespace PhoneLink.DeviceCommunication.Tunnels;

public sealed record UsbTunnelVerification(bool Verified, TunnelStatistics Statistics);

public static class UsbTunnelVerifier
{
    public static async Task<UsbTunnelVerification> VerifyAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var connector = await PinnedUsbDeviceConnector.SelectSingleAsync(timeout.Token).ConfigureAwait(false);
        await using var tunnel = LoopbackDeviceTunnel.Start(
            token => connector.OpenChannelAsync(62078, token), cancellationToken: timeout.Token);
        bool verified;
        using (var local = new TcpClient(AddressFamily.InterNetwork))
        {
            await local.ConnectAsync(IPAddress.Loopback, tunnel.LocalPort, timeout.Token).ConfigureAwait(false);
            var stream = local.GetStream();
            var request = ExperimentalIphoneInformationProbe.BuildReadRequest("ProductVersion");
            var header = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(header, request.Length);
            await stream.WriteAsync(header, timeout.Token).ConfigureAwait(false);
            await stream.WriteAsync(request, timeout.Token).ConfigureAwait(false);
            await stream.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (length is 0 or > 65_536) throw new InvalidDataException("The tunneled response exceeded the limit.");
            var response = new byte[checked((int)length)];
            try
            {
                await stream.ReadExactlyAsync(response, timeout.Token).ConfigureAwait(false);
                verified = ExperimentalIphoneInformationProbe.ParseReadResponse(response) is not null;
            }
            finally { CryptographicOperations.ZeroMemory(response); }
        }
        await tunnel.DisposeAsync().ConfigureAwait(false);
        return new(verified, tunnel.CaptureStatistics());
    }
}

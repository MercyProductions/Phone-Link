using System.Text;

namespace PhoneLink.DeviceCommunication.Diagnostics;

public sealed record SshAvailability(bool ServerIdentified, string Status, string? ProtocolVersion, int? TransportResponseCode);

public static class ExperimentalSshAvailabilityProbe
{
    public static async Task<SshAvailability> ProbeAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            using var connection = await ExperimentalIphoneInformationProbe.OpenSingleUsbChannelAsync(22, timeout.Token).ConfigureAwait(false);
            var stream = connection.GetStream();
            await stream.WriteAsync("SSH-2.0-PhoneLinkProbe_1\r\n"u8.ToArray(), timeout.Token).ConfigureAwait(false);
            return await ReadIdentificationAsync(stream, timeout.Token).ConfigureAwait(false);
        }
        catch (UsbMuxChannelUnavailableException exception)
        {
            return new(false, "Standard SSH port was not available through the USB transport. No login was attempted.", null, exception.ResponseCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "SSH availability check timed out; no server or login was established.", null, null);
        }
    }

    public static async Task<SshAvailability> ReadIdentificationAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var one = new byte[1];
        for (var lineIndex = 0; lineIndex < 8; lineIndex++)
        {
            var line = new StringBuilder();
            var terminated = false;
            for (var length = 0; length < 255; length++)
            {
                if (await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 0)
                    return new(false, "Channel closed before an SSH identification was received.", null, null);
                if (one[0] == (byte)'\n') { terminated = true; break; }
                if (one[0] == (byte)'\r') continue;
                if (one[0] is < 32 or > 126)
                    return new(false, "The standard port did not provide a supported SSH identification.", null, null);
                line.Append((char)one[0]);
            }
            if (!terminated) return new(false, "The identification line exceeded the bounded SSH response limit.", null, null);
            var text = line.ToString();
            if (text.StartsWith("SSH-2.0-", StringComparison.Ordinal) && text.Length > 8)
                return new(true, "SSH 2.0 identification observed. Host key and authentication are not verified; no login or command was attempted.", "2.0", null);
            if (text.StartsWith("SSH-", StringComparison.Ordinal))
                return new(false, "An unsupported SSH protocol identification was received; no login was attempted.", null, null);
        }
        return new(false, "No supported SSH identification was found within the bounded response.", null, null);
    }
}

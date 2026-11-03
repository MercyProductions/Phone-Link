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
            var line = new byte[255];
            var count = 0;
            var terminated = false;
            for (var length = 0; length < 255; length++)
            {
                if (await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 0)
                    return new(false, "Channel closed before an SSH identification was received; no login was attempted.", null, null);
                line[count++] = one[0];
                if (one[0] == (byte)'\n') { terminated = true; break; }
            }
            if (!terminated) return new(false, "The identification line exceeded the bounded SSH response limit; no login was attempted.", null, null);
            // Preamble is bounded and ignored, never retained/displayed (it may be UTF-8).
            if (!line.AsSpan(0, count).StartsWith("SSH-"u8)) continue;
            var hasCarriageReturn = count >= 2 && line[count - 2] == (byte)'\r';
            var contentLength = count - (hasCarriageReturn ? 2 : 1);
            var content = line.AsSpan(0, contentLength);
            if (content.ContainsAnyExceptInRange((byte)32, (byte)126))
                return InvalidIdentification();
            var text = Encoding.ASCII.GetString(content);
            var prefixLength = text.StartsWith("SSH-2.0-", StringComparison.Ordinal) ? 8 :
                text.StartsWith("SSH-1.99-", StringComparison.Ordinal) ? 9 : 0;
            if (prefixLength == 0 || (prefixLength == 8 && !hasCarriageReturn)) return InvalidIdentification();
            var software = text[prefixLength..].Split(' ', 2)[0];
            if (software.Length == 0 || software.Any(character => character is < '!' or > '~' or '-'))
                return InvalidIdentification();
            return new(true, "SSH 2.0-compatible identification observed. Host key and authentication are not verified; no login or command was attempted.", prefixLength == 8 ? "2.0" : "1.99", null);
        }
        return new(false, "No supported SSH identification was found within the bounded response; no login was attempted.", null, null);
    }

    private static SshAvailability InvalidIdentification() => new(false,
        "An unsupported or malformed SSH identification was received; no login was attempted.", null, null);
}

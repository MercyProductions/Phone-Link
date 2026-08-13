using System.Net;
using PhoneLink.Protocol.Validation;

namespace PhoneLink.Protocol.Pairing;

public static class PairingOfferValidator
{
    private static readonly TimeSpan MaximumOfferLifetime = TimeSpan.FromMinutes(3);

    public static void ValidateForClient(PairingOffer offer, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(offer);

        if (offer.Version != ProtocolConstants.CurrentVersion)
        {
            throw new PhoneLinkProtocolException(
                "pairing.offer.unsupported-version",
                $"Pairing offer version {offer.Version} is not supported.");
        }

        if (offer.ExpiresAt <= now)
        {
            throw new PhoneLinkProtocolException(
                "pairing.offer.expired",
                "The pairing offer has expired.");
        }

        if (offer.ExpiresAt - now > MaximumOfferLifetime)
        {
            throw new PhoneLinkProtocolException(
                "pairing.offer.invalid-expiry",
                "The pairing offer lifetime is longer than allowed.");
        }

        foreach (var endpoint in offer.Endpoints)
        {
            if (!IsLocalEndpoint(endpoint.Host))
            {
                throw new PhoneLinkProtocolException(
                    "pairing.offer.public-endpoint",
                    "Version 1 pairing accepts only local/private network endpoints.");
            }
        }
    }

    public static bool IsLocalEndpoint(string host)
    {
        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(host, out var address) || IPAddress.IsLoopback(address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return bytes[0] == 10 ||
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168) ||
                (bytes[0] == 169 && bytes[1] == 254);
        }

        return (bytes[0] & 0xFE) == 0xFC ||
            (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80);
    }
}

using PhoneLink.Domain.Devices;

namespace PhoneLink.Application.Ports;

public sealed record DeviceConnectionContext
{
    private readonly byte[] _tlsSpkiSha256;

    public DeviceConnectionContext(
        DeviceId deviceId,
        Uri endpoint,
        ReadOnlySpan<byte> tlsSpkiSha256)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!string.Equals(endpoint.Scheme, "wss", StringComparison.Ordinal))
        {
            throw new ArgumentException("A device endpoint must use wss.", nameof(endpoint));
        }

        if (tlsSpkiSha256.Length != 32)
        {
            throw new ArgumentException("A TLS SPKI SHA-256 pin must be 32 bytes.", nameof(tlsSpkiSha256));
        }

        DeviceId = deviceId;
        Endpoint = endpoint;
        _tlsSpkiSha256 = tlsSpkiSha256.ToArray();
    }

    public DeviceId DeviceId { get; }

    public Uri Endpoint { get; }

    public ReadOnlyMemory<byte> TlsSpkiSha256 => _tlsSpkiSha256;
}


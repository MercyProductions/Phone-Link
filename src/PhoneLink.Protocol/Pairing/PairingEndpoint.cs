namespace PhoneLink.Protocol.Pairing;

public sealed record PairingEndpoint
{
    public PairingEndpoint(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        if (host.Length > 253)
        {
            throw new ArgumentOutOfRangeException(nameof(host), "An endpoint host is too long.");
        }

        if (port is < 1 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "An endpoint port must be between 1 and 65535.");
        }

        Host = host;
        Port = port;
    }

    public string Host { get; }

    public int Port { get; }
}

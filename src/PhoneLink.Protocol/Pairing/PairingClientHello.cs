namespace PhoneLink.Protocol.Pairing;

public sealed record PairingClientHello
{
    public required string Kind { get; init; }

    public required Guid SessionId { get; init; }

    public required Guid ClientDeviceId { get; init; }

    public required string ClientDisplayName { get; init; }

    public required string ClientIdentityPublicKey { get; init; }

    public required string ClientNonce { get; init; }

    public required string Proof { get; init; }

    public required string AppVersion { get; init; }
}

namespace PhoneLink.Protocol.Pairing;

public sealed record PairingServerProof
{
    public required string Kind { get; init; }

    public required Guid SessionId { get; init; }

    public required string ServerNonce { get; init; }

    public required string Signature { get; init; }
}

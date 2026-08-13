namespace PhoneLink.Protocol.Pairing;

public sealed record PairingConfirmation
{
    public required string Kind { get; init; }

    public required Guid SessionId { get; init; }

    public required bool Approved { get; init; }
}

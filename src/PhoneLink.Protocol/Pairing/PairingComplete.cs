namespace PhoneLink.Protocol.Pairing;

public sealed record PairingComplete
{
    public required string Kind { get; init; }

    public required Guid SessionId { get; init; }

    public required bool Paired { get; init; }

    public required Guid ServerDeviceId { get; init; }

    public required string? ErrorCode { get; init; }
}

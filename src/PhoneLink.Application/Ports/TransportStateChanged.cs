namespace PhoneLink.Application.Ports;

public sealed record TransportStateChanged(
    TransportState Previous,
    TransportState Current,
    DateTimeOffset OccurredAt,
    string? ReasonCode = null);


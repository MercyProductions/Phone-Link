namespace PhoneLink.DeviceCommunication.Pairing;

public sealed record PairingOfferDisplay(
    Guid SessionId,
    DateTimeOffset ExpiresAt,
    string QrPayload);

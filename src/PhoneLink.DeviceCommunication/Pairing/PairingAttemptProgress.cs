namespace PhoneLink.DeviceCommunication.Pairing;

public enum PairingAttemptStage
{
    RequestReceived,
    AwaitingConfirmation,
    Trusted,
    Declined,
    Expired,
    Interrupted,
    Failed,
}

// Local UI correlation only. No QR material, peer identity, or exception text.
public sealed record PairingAttemptProgress(Guid SessionId, PairingAttemptStage Stage);

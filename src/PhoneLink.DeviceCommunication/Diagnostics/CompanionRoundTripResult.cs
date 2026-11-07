namespace PhoneLink.DeviceCommunication.Diagnostics;

public enum CompanionRoundTripOutcome { Responded, NoReply, SessionChanged, Busy, Unavailable }

public sealed record CompanionRoundTripResult(CompanionRoundTripOutcome Outcome, TimeSpan? Elapsed = null);

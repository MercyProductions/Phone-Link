using System.Globalization;

namespace PhoneLink.Application.Features.Calling;

public enum CallTransportQueryState { Completed, ApiUnavailable, AccessDenied, Unavailable }
public enum CallAudioRoutingObservation { Unknown, CanRouteToPc, CannotRouteToPc }

public sealed record CallTransportObservation(bool? AppRegistered,
    CallAudioRoutingObservation AudioRouting, bool AccessDenied);

// No endpoint IDs, device names, numbers, callers or call history.
public sealed record CallTransportInspection(DateTimeOffset CapturedAt,
    CallTransportQueryState QueryState, int? EnumeratedEndpoints,
    IReadOnlyList<CallTransportObservation> Observations)
{
    public string ToDisplayText()
    {
        if (QueryState != CallTransportQueryState.Completed)
            return $"Observed at {CapturedAt:O}. Windows Bluetooth call-transport query: {QueryState}. Endpoint presence, registration and routing remain unknown. No access request, registration, connection or call was attempted.";
        var registered = Observations.Count(item => item.AppRegistered == true);
        var unregistered = Observations.Count(item => item.AppRegistered == false);
        var unknownRegistration = Observations.Count(item => item.AppRegistered is null);
        var canRoute = Observations.Count(item => item.AudioRouting == CallAudioRoutingObservation.CanRouteToPc);
        var cannotRoute = Observations.Count(item => item.AudioRouting == CallAudioRoutingObservation.CannotRouteToPc);
        var unknownRoute = Observations.Count(item => item.AudioRouting == CallAudioRoutingObservation.Unknown);
        return string.Create(CultureInfo.InvariantCulture, $"""
            Observed at {CapturedAt:O}. This is a read-only snapshot, not continuous monitoring.
            Windows enumerated {EnumeratedEndpoints} Bluetooth phone-line endpoint(s); inspected {Observations.Count} (maximum 32). These are not authenticated iPhone identities.
            This app registration — yes: {registered}; no: {unregistered}; unknown: {unknownRegistration}.
            Windows audio-routing observation — can route to PC: {canRoute}; cannot: {cannotRoute}; unknown: {unknownRoute}. Access denied during inspection: {Observations.Count(item => item.AccessDenied)}.
            No access request, registration, connection, audio change or call was attempted. Visibility, registration and routing observations do not prove usable call control or audible call audio. Restricted capabilities and a separate explicit, verified connection path are still required; do not disable security to force access.
            """);
    }
}

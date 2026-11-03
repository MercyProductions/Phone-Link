using PhoneLink.Application.Features.Calling;

namespace PhoneLink.Application.Tests;

public sealed class CallTransportInspectionTests
{
    [Theory]
    [InlineData(CallTransportQueryState.ApiUnavailable)]
    [InlineData(CallTransportQueryState.AccessDenied)]
    [InlineData(CallTransportQueryState.Unavailable)]
    public void FailedEnumerationDoesNotClaimNoPhoneOrHealthyRouting(CallTransportQueryState state)
    {
        var text = new CallTransportInspection(DateTimeOffset.UtcNow, state, null, []).ToDisplayText();
        Assert.Contains("remain unknown", text);
        Assert.DoesNotContain("enumerated 0", text);
        Assert.Contains("No access request", text);
    }

    [Fact]
    public void ZeroEnumeratedEndpointsDoesNotMeanPhoneHasNoCallSupport()
    {
        var text = new CallTransportInspection(DateTimeOffset.UtcNow, CallTransportQueryState.Completed, 0, []).ToDisplayText();
        Assert.Contains("enumerated 0", text);
        Assert.Contains("do not prove usable call control", text);
        Assert.Contains("not authenticated iPhone identities", text);
    }

    [Fact]
    public void RegistrationAndRoutingAreIndependentObservationsNotCallSuccess()
    {
        var result = new CallTransportInspection(DateTimeOffset.UtcNow, CallTransportQueryState.Completed, 4,
            [new(true, CallAudioRoutingObservation.CanRouteToPc, false),
             new(false, CallAudioRoutingObservation.CannotRouteToPc, false),
             new(null, CallAudioRoutingObservation.Unknown, true)]);
        var text = result.ToDisplayText();
        Assert.Contains(result.CapturedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture), text);
        Assert.Contains("inspected 3", text);
        Assert.Contains("yes: 1; no: 1; unknown: 1", text);
        Assert.Contains("can route to PC: 1; cannot: 1; unknown: 1", text);
        Assert.Contains("Access denied during inspection: 1", text);
        Assert.Contains("do not prove usable call control or audible call audio", text);
    }
}

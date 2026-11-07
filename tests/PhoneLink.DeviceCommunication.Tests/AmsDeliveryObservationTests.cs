using PhoneLink.Integrations.AppleBluetooth.Media;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class AmsDeliveryObservationTests
{
    [Fact]
    public async Task OnlyRequestedValidatedStateSignalsDeliveryAndNoValueIsReturned()
    {
        var check = new AmsDeliveryObservation();
        Assert.Equal(new byte[] { 0, 1 }, AmsDeliveryObservation.CreateRegistration());
        check.Observe([0, 1, 0, .. "1,1,1"u8]);
        Assert.Equal(AmsDeliveryOutcome.NoUsableUpdate, (await check.WaitAsync(TimeSpan.Zero, CancellationToken.None)).Outcome);
        check.Arm();
        check.Observe([2, 2, 0, .. "unsolicited-fixture-title"u8]);
        check.Observe([0, 1, 1, .. "1,1,1"u8]);
        check.Observe([0, 1, 0, .. "1,NaN,1"u8]);
        check.Observe([0, 1, 0, .. "1,1,1"u8]);
        var result = await check.WaitAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.Equal(new AmsDeliveryResult(AmsDeliveryOutcome.PlaybackStateReceived, 2), result);
        Assert.DoesNotContain("unsolicited", result.ToString());
    }

    [Fact]
    public async Task SilenceAndSessionEndAreDifferentAndLateFramesCannotReviveAnEndedCheck()
    {
        var check = new AmsDeliveryObservation();
        check.Arm();
        Assert.Equal(AmsDeliveryOutcome.NoUsableUpdate,
            (await check.WaitAsync(TimeSpan.Zero, CancellationToken.None)).Outcome);
        check.End();
        check.Observe([0, 1, 0, .. "1,1,1"u8]);
        Assert.Equal(AmsDeliveryOutcome.Ended,
            (await check.WaitAsync(TimeSpan.Zero, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task CancellationSuppressesAlreadyReceivedEvidence()
    {
        var check = new AmsDeliveryObservation();
        check.Arm();
        check.Observe([0, 1, 0, .. "1,1,1"u8]);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check.WaitAsync(TimeSpan.Zero, cancellation.Token));
    }
}

namespace PhoneLink.Integrations.AppleBluetooth.Media;

public enum AmsDeliveryOutcome { PlaybackStateReceived, NoUsableUpdate, Ended, NotExposed, SubscriptionRejected, RegistrationRejected }
public sealed record AmsDeliveryResult(AmsDeliveryOutcome Outcome, int UnusableUpdates);

internal sealed class AmsDeliveryObservation
{
    private readonly TaskCompletionSource<AmsDeliveryOutcome> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _unusable;
    private int _armed;

    internal static byte[] CreateRegistration() => [(byte)AmsEntity.Player, 1];
    internal void Arm() => Volatile.Write(ref _armed, 1);

    internal void Observe(ReadOnlySpan<byte> payload)
    {
        if (Volatile.Read(ref _armed) == 0 || _completed.Task.IsCompleted) return;
        // Only the requested playback-state field is relevant. Don't decode or retain unsolicited metadata.
        if (payload.Length >= 2 && (payload[0] != (byte)AmsEntity.Player || payload[1] != 1)) return;
        try
        {
            if (AmsProtocol.ParsePlayback(AmsProtocol.ParseUpdate(payload, DateTimeOffset.UtcNow)) is not null)
            {
                _completed.TrySetResult(AmsDeliveryOutcome.PlaybackStateReceived);
                return;
            }
        }
        catch (InvalidDataException) { }
        Reject();
    }

    internal void Reject()
    {
        if (Volatile.Read(ref _armed) != 0 && !_completed.Task.IsCompleted)
            Interlocked.Increment(ref _unusable);
    }
    internal void End() => _completed.TrySetResult(AmsDeliveryOutcome.Ended);
    internal AmsDeliveryResult Report(AmsDeliveryOutcome outcome) => new(outcome, Volatile.Read(ref _unusable));

    internal async Task<AmsDeliveryResult> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var outcome = await _completed.Task.WaitAsync(timeout, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Report(outcome);
        }
        catch (TimeoutException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Report(AmsDeliveryOutcome.NoUsableUpdate);
        }
    }
}

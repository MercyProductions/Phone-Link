using System.Runtime.CompilerServices;

namespace PhoneLink.Integrations.AppleBluetooth.Media;

public enum AmsCommandOutcome { NotSent, WriteAccepted, SubmittedUnacknowledged, DeviceRejected, OutcomeUnknown }
public sealed record AmsWriteReceipt(bool Accepted, bool ResponseRequested, byte? ProtocolError = null);
public sealed record AmsCommandResult(AmsCommandOutcome Outcome, bool WriteAttempted, byte? ProtocolError = null);

public sealed class AmsCommandSender(SemaphoreSlim gate, Func<AmsCommandRequest, bool> isCurrent,
    Func<byte[], CancellationToken, Task<AmsWriteReceipt>> write, CancellationToken sessionLifetime)
{
    private readonly ConditionalWeakTable<AmsCommandRequest, Attempt> _attempts = new();
    public async Task<AmsCommandResult> SendAsync(AmsCommandRequest request, Func<bool> isAuthorized, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(isAuthorized);
        if (sessionLifetime.IsCancellationRequested) return new(AmsCommandOutcome.NotSent, false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sessionLifetime);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var entered = false;
        var attempted = false;
        try
        {
            await gate.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            timeout.Token.ThrowIfCancellationRequested();
            if (!isAuthorized() || !isCurrent(request)) return new(AmsCommandOutcome.NotSent, false);
            var attempt = _attempts.GetValue(request, _ => new Attempt());
            if (Interlocked.Exchange(ref attempt.Started, 1) != 0) return new(AmsCommandOutcome.NotSent, false);
            var command = AmsProtocol.CreateCommand(request.Command);
            attempted = true;
            var receipt = await write(command, timeout.Token).ConfigureAwait(false);
            return new(receipt.Accepted ? receipt.ResponseRequested ? AmsCommandOutcome.WriteAccepted : AmsCommandOutcome.SubmittedUnacknowledged :
                receipt.ProtocolError is not null ? AmsCommandOutcome.DeviceRejected : AmsCommandOutcome.OutcomeUnknown, true, receipt.ProtocolError);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(attempted ? AmsCommandOutcome.OutcomeUnknown : AmsCommandOutcome.NotSent, attempted); }
        finally { if (entered) gate.Release(); }
    }
    private sealed class Attempt { public int Started; }
}

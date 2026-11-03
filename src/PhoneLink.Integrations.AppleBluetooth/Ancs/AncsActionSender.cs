using System.Runtime.CompilerServices;

namespace PhoneLink.Integrations.AppleBluetooth.Ancs;

public enum AncsNotificationAction : byte { Positive = 0, Negative = 1 }
public enum AncsActionOutcome { NotSent, WriteAccepted, DeviceRejected, OutcomeUnknown }
public sealed record AncsActionResult(AncsActionOutcome Outcome, bool WriteAttempted, byte? ProtocolError = null);
public sealed record AncsActionWriteResult(bool Accepted, byte? ProtocolError = null);

// Owns no transport: the session supplies its shared Control Point gate and lifetime.
public sealed class AncsActionSender(SemaphoreSlim commandGate,
    Func<AncsNotification, bool> isCurrent, Func<byte[], CancellationToken, Task<AncsActionWriteResult>> write,
    CancellationToken sessionLifetime)
{
    private readonly ConditionalWeakTable<AncsNotification, Attempt> _attempts = new();

    public static string GetLabel(AncsNotification notification, AncsNotificationAction action)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var value = action switch
        {
            AncsNotificationAction.Positive when (notification.Flags & 8) != 0 => notification.PositiveActionLabel,
            AncsNotificationAction.Negative when (notification.Flags & 16) != 0 => notification.NegativeActionLabel,
            _ => string.Empty,
        };
        // Do not invent a label, render control characters, or hide a truncated meaning.
        return value.Length is > 0 and <= 256 && !string.IsNullOrWhiteSpace(value) &&
            !value.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format) ? value : string.Empty;
    }

    public async Task<AncsActionResult> SendAsync(AncsNotification notification, AncsNotificationAction action,
        Func<bool> isAuthorized, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(isAuthorized);
        if (GetLabel(notification, action).Length == 0) return new(AncsActionOutcome.NotSent, false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sessionLifetime);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var entered = false;
        var attempted = false;
        try
        {
            await commandGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            timeout.Token.ThrowIfCancellationRequested();
            if (!isAuthorized() || !isCurrent(notification)) return new(AncsActionOutcome.NotSent, false);
            var attempt = _attempts.GetValue(notification, _ => new Attempt());
            if (Interlocked.Exchange(ref attempt.Started, 1) != 0) return new(AncsActionOutcome.NotSent, false);
            var command = AncsProtocolParser.CreatePerformNotificationActionCommand(notification.NotificationUid, action);
            attempted = true;
            var result = await write(command, timeout.Token).ConfigureAwait(false);
            return new(result.Accepted ? AncsActionOutcome.WriteAccepted :
                result.ProtocolError is not null ? AncsActionOutcome.DeviceRejected : AncsActionOutcome.OutcomeUnknown,
                true, result.ProtocolError);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A timed-out/failed write may have reached the phone. Never automatically retry.
            return new(attempted ? AncsActionOutcome.OutcomeUnknown : AncsActionOutcome.NotSent, attempted);
        }
        finally { if (entered) commandGate.Release(); }
    }

    private sealed class Attempt { public int Started; }
}

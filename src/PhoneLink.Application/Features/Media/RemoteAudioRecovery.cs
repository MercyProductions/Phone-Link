namespace PhoneLink.Application.Features.Media;

public enum RemoteAudioRecoveryOutcome { NotEligible, CleanupUnverified, Reopened, ReopenNotVerified }

public sealed record RemoteAudioRecoveryResult(RemoteAudioRecoveryOutcome Outcome, RemoteAudioSession? Session = null,
    RemoteAudioOpenOutcome? OpenOutcome = null);

/// <summary>One opt-in reconnect for the original source after a confirmed open followed by a closed state.</summary>
public sealed class RemoteAudioRecovery(Func<RemoteAudioSession> createSameSource, TimeSpan settleDelay)
{
    private readonly Func<RemoteAudioSession> _createSameSource =
        createSameSource ?? throw new ArgumentNullException(nameof(createSameSource));
    private int _spent;

    public async Task<RemoteAudioRecoveryResult> TryRecoverAsync(RemoteAudioSession previous,
        Func<bool> contextIsCurrent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(contextIsCurrent);
        cancellationToken.ThrowIfCancellationRequested();
        if (!contextIsCurrent() || !previous.WasOpened || previous.State != RemoteAudioState.Closed ||
            Interlocked.Exchange(ref _spent, 1) != 0)
            return new(RemoteAudioRecoveryOutcome.NotEligible);

        if (!TryRelease(previous)) return new(RemoteAudioRecoveryOutcome.CleanupUnverified);
        await Task.Delay(settleDelay, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!contextIsCurrent()) return new(RemoteAudioRecoveryOutcome.NotEligible);

        var replacement = _createSameSource();
        try
        {
            var result = await replacement.OpenAsync(contextIsCurrent, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (contextIsCurrent() && result == RemoteAudioOpenOutcome.Opened && replacement.State == RemoteAudioState.Opened)
                return new(RemoteAudioRecoveryOutcome.Reopened, replacement, result);
            return new(TryRelease(replacement) ? RemoteAudioRecoveryOutcome.ReopenNotVerified :
                RemoteAudioRecoveryOutcome.CleanupUnverified, OpenOutcome: result);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (!TryRelease(replacement)) return new(RemoteAudioRecoveryOutcome.CleanupUnverified);
            throw;
        }
    }

    private static bool TryRelease(RemoteAudioSession session)
    {
        try { session.Dispose(); return session.State == RemoteAudioState.Released; }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return false; }
    }
}

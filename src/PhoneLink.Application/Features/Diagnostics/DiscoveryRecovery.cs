namespace PhoneLink.Application.Features.Diagnostics;

public sealed record DiscoveryRecoverySnapshot(bool IsLocked, int TrustedCompanions,
    int EligibleAddresses, bool AdvertisementRegistered);

public enum DiscoveryRecoveryOutcome { NoActionRequired, RecoveredLocally, NotApplicable, Unverified, Failed }

public sealed record DiscoveryRecoveryResult(DiscoveryRecoveryOutcome Outcome,
    DiscoveryRecoverySnapshot Before, DiscoveryRecoverySnapshot? After, bool ActionAttempted,
    string Summary);

public sealed class DiscoveryRecoveryRetryBudget(TimeProvider? timeProvider = null)
{
    private const int BurstLimit = 3;
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private int _failures;
    private long _lastFailure;

    public bool CanRetry
    {
        get
        {
            lock (_gate)
                return _failures < BurstLimit || _clock.GetElapsedTime(_lastFailure) >= RetryCooldown;
        }
    }

    public void Reset()
    {
        lock (_gate) _failures = 0;
    }

    public void RecordFailure()
    {
        lock (_gate)
        {
            if (_failures < BurstLimit) _failures++;
            _lastFailure = _clock.GetTimestamp();
        }
    }
    public void Record(DiscoveryRecoveryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome is DiscoveryRecoveryOutcome.RecoveredLocally or DiscoveryRecoveryOutcome.NoActionRequired) Reset();
        else if (result.ActionAttempted) RecordFailure();
    }
}

// The caller serializes snapshot/action/snapshot with its discovery lifecycle lock.
// Success means local registration only, never companion reachability or phone repair.
public static class DiscoveryRecovery
{
    public static async Task<DiscoveryRecoveryResult> RunAsync(
        Func<CancellationToken, Task<DiscoveryRecoverySnapshot>> capture,
        Func<CancellationToken, Task> register,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(register);
        var before = await capture(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (before.IsLocked || before.TrustedCompanions <= 0 || before.EligibleAddresses <= 0)
            return new(DiscoveryRecoveryOutcome.NotApplicable, before, null, false,
                "No action: unlock Phone Link, pair a companion, and provide an eligible local network before discovery recovery. USB detection does not satisfy companion pairing.");
        if (before.AdvertisementRegistered)
            return new(DiscoveryRecoveryOutcome.NoActionRequired, before, before, false,
                "Local advertisement is already registered. It was not restarted; this does not prove the iPhone can discover or connect to the PC.");

        bool failed;
        try { await register(cancellationToken).ConfigureAwait(false); failed = false; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException) { failed = true; }
        cancellationToken.ThrowIfCancellationRequested();
        DiscoveryRecoverySnapshot after;
        try { after = await capture(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(DiscoveryRecoveryOutcome.Unverified, before, null, true,
                "Registration recovery was attempted, but its final state could not be checked. No successful repair is claimed.");
        }
        var recovered = !failed && !after.IsLocked && after.TrustedCompanions > 0 &&
            after.EligibleAddresses > 0 && after.AdvertisementRegistered;
        return new(recovered ? DiscoveryRecoveryOutcome.RecoveredLocally :
            failed ? DiscoveryRecoveryOutcome.Failed : DiscoveryRecoveryOutcome.Unverified, before, after, true,
            recovered ? "Missing local discovery advertisement restored and registration verified. Companion discovery and authentication still need a phone-side connection check. No trust, phone data, settings or drivers were changed."
                : "Discovery recovery did not produce a verified usable local registration. No successful repair is claimed; check network availability and companion pairing without disabling the firewall.");
    }
}

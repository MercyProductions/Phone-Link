namespace PhoneLink.Application.Features.Security;

/// <summary>UI-thread policy using monotonic time; activity never unlocks a locked app.</summary>
public sealed class InactivityLockPolicy
{
    private readonly TimeProvider _clock;
    private readonly TimeSpan _threshold;
    private long _lastActivity;

    public InactivityLockPolicy(TimeProvider clock, TimeSpan threshold)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(threshold, TimeSpan.Zero);
        _clock = clock;
        _threshold = threshold;
        _lastActivity = clock.GetTimestamp();
    }

    public bool Enabled { get; private set; }
    public bool IsLocked { get; private set; }
    public long VerificationVersion { get; private set; }

    public void Configure(bool enabled, bool requireVerification)
    {
        Enabled = enabled;
        IsLocked = enabled && requireVerification;
        VerificationVersion++;
        _lastActivity = _clock.GetTimestamp();
    }

    public void RecordActivity()
    {
        // Evaluate the deadline before accepting input, including after resume.
        Evaluate();
        if (!IsLocked) { _lastActivity = _clock.GetTimestamp(); }
    }

    public bool Evaluate()
    {
        if (Enabled && !IsLocked && _clock.GetElapsedTime(_lastActivity) >= _threshold)
        {
            IsLocked = true;
            VerificationVersion++;
        }
        return IsLocked;
    }

    public void RequireVerification()
    {
        if (Enabled)
        {
            IsLocked = true;
            VerificationVersion++;
        }
    }

    public bool TryCompleteVerification(long expectedVersion)
    {
        if (!Enabled || !IsLocked || expectedVersion != VerificationVersion) { return false; }
        IsLocked = false;
        _lastActivity = _clock.GetTimestamp();
        return true;
    }
}

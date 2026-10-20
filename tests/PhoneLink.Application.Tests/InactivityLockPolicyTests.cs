using PhoneLink.Application.Features.Security;

namespace PhoneLink.Application.Tests;

public sealed class InactivityLockPolicyTests
{
    [Fact]
    public void StartupRequiresVerificationAndActivityCannotUnlock()
    {
        var clock = new ManualClock();
        var policy = new InactivityLockPolicy(clock, TimeSpan.FromMinutes(5));
        policy.Configure(enabled: true, requireVerification: true);
        Assert.True(policy.IsLocked);
        policy.RecordActivity();
        Assert.True(policy.IsLocked);
        Assert.True(policy.TryCompleteVerification(policy.VerificationVersion));
        Assert.False(policy.IsLocked);
    }

    [Fact]
    public void InputAfterDeadlineCannotResetTimerBeforeLocking()
    {
        var clock = new ManualClock();
        var policy = new InactivityLockPolicy(clock, TimeSpan.FromMinutes(5));
        policy.Configure(enabled: true, requireVerification: false);
        clock.Advance(TimeSpan.FromMinutes(4));
        policy.RecordActivity();
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.False(policy.Evaluate());
        clock.Advance(TimeSpan.FromMinutes(1));
        policy.RecordActivity();
        Assert.True(policy.IsLocked);
    }

    [Fact]
    public void SessionLockRequiresNewVerificationAndDisabledPolicyDoesNotLock()
    {
        var policy = new InactivityLockPolicy(new ManualClock(), TimeSpan.FromMinutes(5));
        policy.Configure(enabled: true, requireVerification: false);
        policy.RequireVerification();
        Assert.True(policy.IsLocked);
        policy.Configure(enabled: false, requireVerification: false);
        policy.RequireVerification();
        Assert.False(policy.Evaluate());
    }

    [Fact]
    public void VerificationStartedBeforeANewSessionLockCannotUnlock()
    {
        var policy = new InactivityLockPolicy(new ManualClock(), TimeSpan.FromMinutes(5));
        policy.Configure(enabled: true, requireVerification: true);
        var oldVersion = policy.VerificationVersion;
        policy.RequireVerification();
        Assert.False(policy.TryCompleteVerification(oldVersion));
        Assert.True(policy.IsLocked);
        Assert.True(policy.TryCompleteVerification(policy.VerificationVersion));
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }
}

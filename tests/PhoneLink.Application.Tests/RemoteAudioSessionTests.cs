using PhoneLink.Application.Features.Media;

namespace PhoneLink.Application.Tests;

public sealed class RemoteAudioSessionTests
{
    [Fact]
    public async Task OpenEnablesThenOpensOnceAndOwnsConnectionUntilStopped()
    {
        var native = new FakeConnection();
        using var session = new RemoteAudioSession(native);
        Assert.Equal(RemoteAudioOpenOutcome.Opened, await session.OpenAsync(() => true, CancellationToken.None));
        Assert.Equal(["enable", "open"], native.Calls);
        Assert.Equal(RemoteAudioState.Opened, session.State);
        Assert.Equal(0, native.Releases);
        session.Dispose();
        session.Dispose();
        Assert.Equal(1, native.Releases);
        Assert.Equal(RemoteAudioState.Released, session.State);
    }

    [Theory]
    [InlineData(RemoteAudioOpenOutcome.TimedOut)]
    [InlineData(RemoteAudioOpenOutcome.DeniedBySystem)]
    [InlineData(RemoteAudioOpenOutcome.UnknownFailure)]
    public async Task FailedOpenReleasesWithoutRetry(RemoteAudioOpenOutcome outcome)
    {
        var native = new FakeConnection { Outcome = outcome };
        using var session = new RemoteAudioSession(native);
        Assert.Equal(outcome, await session.OpenAsync(() => true, CancellationToken.None));
        Assert.Equal(["enable", "open", "release"], native.Calls);
        Assert.Equal(1, native.Releases);
    }

    [Fact]
    public async Task ContextInvalidBeforeEnableMakesNoNativeAttempt()
    {
        var native = new FakeConnection();
        using var session = new RemoteAudioSession(native);
        await Assert.ThrowsAsync<OperationCanceledException>(() => session.OpenAsync(() => false, CancellationToken.None));
        Assert.Equal(["release"], native.Calls);
    }

    [Fact]
    public async Task ContextChangedDuringEnablePreventsOpen()
    {
        var current = true;
        var native = new FakeConnection { EnableAction = () => { current = false; return Task.CompletedTask; } };
        using var session = new RemoteAudioSession(native);
        await Assert.ThrowsAsync<OperationCanceledException>(() => session.OpenAsync(() => current, CancellationToken.None));
        Assert.Equal(["enable", "release"], native.Calls);
    }

    [Fact]
    public async Task AlreadyCancelledMakesNoNativeAttempt()
    {
        var native = new FakeConnection();
        using var session = new RemoteAudioSession(native);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.OpenAsync(() => true, cancellation.Token));
        Assert.Equal(["release"], native.Calls);
    }

    [Fact]
    public async Task CancellationDuringEnableReleasesEvenIfNativeIgnoresToken()
    {
        var enabled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new FakeConnection { EnableAction = () => enabled.Task };
        using var session = new RemoteAudioSession(native);
        using var cancellation = new CancellationTokenSource();
        var pending = session.OpenAsync(() => true, cancellation.Token);
        await cancellation.CancelAsync();
        Assert.Equal(1, native.Releases);
        enabled.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(["enable", "release"], native.Calls);
    }

    [Fact]
    public async Task CancellationDuringOpenCannotReturnLateSuccess()
    {
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new FakeConnection { OpenAction = () => opened.Task };
        using var session = new RemoteAudioSession(native);
        using var cancellation = new CancellationTokenSource();
        var pending = session.OpenAsync(() => true, cancellation.Token);
        await cancellation.CancelAsync();
        opened.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, native.Releases);
        Assert.Equal(RemoteAudioState.Released, session.State);
    }

    [Fact]
    public async Task ExplicitStopDuringOpenCannotReturnLateSuccess()
    {
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new FakeConnection { OpenAction = () => opened.Task };
        using var session = new RemoteAudioSession(native);
        var pending = session.OpenAsync(() => true, CancellationToken.None);
        session.Dispose();
        opened.SetResult();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
        Assert.Equal(1, native.Releases);
    }

    [Fact]
    public async Task ContextChangedDuringOpenCannotReturnSuccess()
    {
        var current = true;
        var native = new FakeConnection { OpenAction = () => { current = false; return Task.CompletedTask; } };
        using var session = new RemoteAudioSession(native);
        await Assert.ThrowsAsync<OperationCanceledException>(() => session.OpenAsync(() => current, CancellationToken.None));
        Assert.Equal(1, native.Releases);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeExceptionsReleaseWithoutRetry(bool enabling)
    {
        static Task Fail() => throw new InvalidOperationException("native unavailable");
        var native = new FakeConnection();
        if (enabling) native.EnableAction = Fail; else native.OpenAction = Fail;
        using var session = new RemoteAudioSession(native);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.OpenAsync(() => true, CancellationToken.None));
        Assert.Equal(1, native.Releases);
    }

    [Fact]
    public async Task ASecondAttemptRequiresNewSession()
    {
        var native = new FakeConnection();
        using var session = new RemoteAudioSession(native);
        await session.OpenAsync(() => true, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.OpenAsync(() => true, CancellationToken.None));
        Assert.Equal(["enable", "open"], native.Calls);
    }

    [Fact]
    public async Task DisposedSessionCannotEnable()
    {
        var native = new FakeConnection();
        using var session = new RemoteAudioSession(native);
        session.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.OpenAsync(() => true, CancellationToken.None));
        Assert.Equal(["release"], native.Calls);
    }

    [Fact]
    public void FailedReleaseRemainsUnknownAndIsNotRetried()
    {
        var native = new FakeConnection { ReleaseFails = true };
        using var session = new RemoteAudioSession(native);
        Assert.Throws<InvalidOperationException>(() => session.Dispose());
        Assert.Equal(RemoteAudioState.Unknown, session.State);
        session.Dispose();
        Assert.Equal(1, native.Releases);
    }

    [Fact]
    public void StateFailureIsUnknownNotAnOpenConnection()
    {
        var native = new FakeConnection { StateFails = true };
        using var session = new RemoteAudioSession(native);
        Assert.Equal(RemoteAudioState.Unknown, session.State);
    }

    [Fact]
    public async Task InProgressReleaseIsUnknownUntilNativeCleanupReturns()
    {
        using var allowRelease = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new FakeConnection { ReleaseAction = () => { entered.SetResult(); allowRelease.Wait(TimeSpan.FromSeconds(5)); } };
        using var session = new RemoteAudioSession(native);
        var release = Task.Run(session.Dispose);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RemoteAudioState.Unknown, session.State);
        }
        finally { allowRelease.Set(); await release; }
        Assert.Equal(RemoteAudioState.Released, session.State);
    }

    [Fact]
    public async Task RecoveryReleasesBeforeCreatingOneReplacementAndNeverRetriesIt()
    {
        var original = new FakeConnection();
        using var previous = new RemoteAudioSession(original);
        var fresh = new FakeConnection();
        var attempts = 0;
        var recovery = new RemoteAudioRecovery(() =>
        {
            Assert.Equal(RemoteAudioState.Released, previous.State);
            attempts++;
            return new(fresh);
        }, TimeSpan.Zero);
        await previous.OpenAsync(() => true, CancellationToken.None);
        original.ReportedState = RemoteAudioState.Closed;
        var result = await recovery.TryRecoverAsync(previous, () => true, CancellationToken.None);
        using var replacement = Assert.IsType<RemoteAudioSession>(result.Session);
        Assert.Equal(RemoteAudioRecoveryOutcome.Reopened, result.Outcome);
        Assert.Equal(["enable", "open"], fresh.Calls);
        fresh.ReportedState = RemoteAudioState.Closed;
        Assert.Equal(RemoteAudioRecoveryOutcome.NotEligible,
            (await recovery.TryRecoverAsync(replacement, () => true, CancellationToken.None)).Outcome);
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData(RemoteAudioState.Unknown, true)]
    [InlineData(RemoteAudioState.Opened, true)]
    [InlineData(RemoteAudioState.Closed, false)]
    public async Task RecoveryRequiresConfirmedOpenThenDefiniteClosure(RemoteAudioState current, bool openFirst)
    {
        var original = new FakeConnection();
        using var previous = new RemoteAudioSession(original);
        var recovery = new RemoteAudioRecovery(() => throw new InvalidOperationException("Unexpected recovery"), TimeSpan.Zero);
        if (openFirst) await previous.OpenAsync(() => true, CancellationToken.None);
        original.ReportedState = current;
        Assert.Equal(RemoteAudioRecoveryOutcome.NotEligible,
            (await recovery.TryRecoverAsync(previous, () => true, CancellationToken.None)).Outcome);
        Assert.Equal(0, original.Releases);
    }

    [Fact]
    public async Task RecoveryDoesNotReplaceAReceiverWhoseReleaseFailed()
    {
        var original = new FakeConnection { ReleaseFails = true };
        using var previous = new RemoteAudioSession(original);
        await previous.OpenAsync(() => true, CancellationToken.None);
        original.ReportedState = RemoteAudioState.Closed;
        var recovery = new RemoteAudioRecovery(() => throw new InvalidOperationException("Unexpected replacement"), TimeSpan.Zero);
        Assert.Equal(RemoteAudioRecoveryOutcome.CleanupUnverified,
            (await recovery.TryRecoverAsync(previous, () => true, CancellationToken.None)).Outcome);
        Assert.Equal(1, original.Releases);
    }

    [Fact]
    public async Task StopDuringRecoveryDelayPreventsCreatingANewReceiver()
    {
        var original = new FakeConnection();
        using var previous = new RemoteAudioSession(original);
        using var cancellation = new CancellationTokenSource();
        await previous.OpenAsync(() => true, CancellationToken.None);
        original.ReportedState = RemoteAudioState.Closed;
        var recovery = new RemoteAudioRecovery(() => throw new InvalidOperationException("Unexpected replacement"), TimeSpan.FromSeconds(20));
        var pending = recovery.TryRecoverAsync(previous, () => true, cancellation.Token);
        Assert.Equal(1, original.Releases);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task CancelledRecoveryReleasesTheReplacementAndCannotReportLateSuccess()
    {
        var original = new FakeConnection();
        using var previous = new RemoteAudioSession(original);
        using var cancellation = new CancellationTokenSource();
        var nativeOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fresh = new FakeConnection { OpenAction = () => nativeOpen.Task };
        await previous.OpenAsync(() => true, CancellationToken.None);
        original.ReportedState = RemoteAudioState.Closed;
        var recovery = new RemoteAudioRecovery(() => new(fresh), TimeSpan.Zero);
        var pending = recovery.TryRecoverAsync(previous, () => true, cancellation.Token);
        await cancellation.CancelAsync();
        nativeOpen.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, fresh.Releases);
    }

    [Fact]
    public async Task DeniedReplacementDoesNotReturnAnActiveSession()
    {
        var original = new FakeConnection();
        using var previous = new RemoteAudioSession(original);
        await previous.OpenAsync(() => true, CancellationToken.None);
        original.ReportedState = RemoteAudioState.Closed;
        var fresh = new FakeConnection { Outcome = RemoteAudioOpenOutcome.DeniedBySystem };
        var recovery = new RemoteAudioRecovery(() => new(fresh), TimeSpan.Zero);
        var result = await recovery.TryRecoverAsync(previous, () => true, CancellationToken.None);
        Assert.Equal(RemoteAudioRecoveryOutcome.ReopenNotVerified, result.Outcome);
        Assert.Equal(RemoteAudioOpenOutcome.DeniedBySystem, result.OpenOutcome);
        Assert.Null(result.Session);
        Assert.Equal(1, fresh.Releases);
    }

    private sealed class FakeConnection : IRemoteAudioConnection
    {
        public List<string> Calls { get; } = [];
        public int Releases { get; private set; }
        public bool ReleaseFails { get; init; }
        public bool StateFails { get; init; }
        public RemoteAudioOpenOutcome Outcome { get; init; } = RemoteAudioOpenOutcome.Opened;
        public Func<Task>? EnableAction { get; set; }
        public Func<Task>? OpenAction { get; set; }
        public Action? ReleaseAction { get; init; }
        public RemoteAudioState ReportedState { get; set; } = RemoteAudioState.Opened;
        public RemoteAudioState State => StateFails ? throw new InvalidOperationException() : ReportedState;
        public async Task EnableAsync(CancellationToken cancellationToken)
        { Calls.Add("enable"); if (EnableAction is { } action) await action(); }
        public async Task<RemoteAudioOpenOutcome> OpenAsync(CancellationToken cancellationToken)
        { Calls.Add("open"); if (OpenAction is { } action) await action(); return Outcome; }
        public void Dispose()
        { Calls.Add("release"); Releases++; ReleaseAction?.Invoke(); if (ReleaseFails) throw new InvalidOperationException(); }
    }
}

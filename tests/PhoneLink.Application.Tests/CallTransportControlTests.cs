using PhoneLink.Application.Features.Calling;

namespace PhoneLink.Application.Tests;

public sealed class CallTransportControlTests
{
    [Fact]
    public async Task RegisterRequestsAccessThenChecksAndVerifiesRegistrationWithoutConnecting()
    {
        var port = new FakeTransport();
        var result = await Run(port, CallTransportAction.Register);
        Assert.Equal(CallTransportControlOutcome.Registered, result.Outcome);
        Assert.Equal(["inspect", "access", "inspect", "register", "inspect"], port.Calls);
        Assert.True(result.RegistrationAfter);
        Assert.True(result.AccessRequestAttempted);
        Assert.True(result.NativeActionAttempted);
    }

    [Theory]
    [InlineData(CallTransportAccess.DeniedByUser, CallTransportControlOutcome.AccessDenied)]
    [InlineData(CallTransportAccess.DeniedBySystem, CallTransportControlOutcome.AccessDenied)]
    [InlineData(CallTransportAccess.Unknown, CallTransportControlOutcome.AccessUnknown)]
    public async Task DeniedOrUnknownAccessNeverRegisters(CallTransportAccess access, CallTransportControlOutcome outcome)
    {
        var port = new FakeTransport { Access = access };
        var result = await Run(port, CallTransportAction.Register);
        Assert.Equal(outcome, result.Outcome);
        Assert.False(result.NativeActionAttempted);
        Assert.Null(result.RegistrationAfter);
        Assert.Equal(["inspect", "access"], port.Calls);
    }

    [Theory]
    [InlineData(CallTransportAction.Register, true)]
    [InlineData(CallTransportAction.Unregister, false)]
    public async Task AlreadyDesiredRegistrationIsNoOp(CallTransportAction action, bool registered)
    {
        var port = new FakeTransport { Registered = registered };
        var result = await Run(port, action);
        Assert.Equal(CallTransportControlOutcome.NoActionRequired, result.Outcome);
        Assert.False(result.AccessRequestAttempted);
        Assert.False(result.NativeActionAttempted);
        Assert.Equal(["inspect"], port.Calls);
    }

    [Fact]
    public async Task RegistrationChangedDuringAccessPromptIsNotOverwritten()
    {
        var port = new FakeTransport();
        port.AfterAccess = () => port.Registered = true;
        var result = await Run(port, CallTransportAction.Register);
        Assert.Equal(CallTransportControlOutcome.NoActionRequired, result.Outcome);
        Assert.False(result.NativeActionAttempted);
        Assert.Equal(["inspect", "access", "inspect"], port.Calls);
    }

    [Fact]
    public async Task UnregisterVerifiesFalseAndDoesNotRequestAccessOrConnect()
    {
        var port = new FakeTransport { Registered = true };
        var result = await Run(port, CallTransportAction.Unregister);
        Assert.Equal(CallTransportControlOutcome.Unregistered, result.Outcome);
        Assert.False(result.RegistrationAfter);
        Assert.Equal(["inspect", "unregister", "inspect"], port.Calls);
    }

    [Theory]
    [InlineData(CallTransportAction.Register, false)]
    [InlineData(CallTransportAction.Unregister, true)]
    public async Task SuccessfulReturnWithoutStateChangeIsNotVerified(CallTransportAction action, bool registered)
    {
        var port = new FakeTransport { Registered = registered, IgnoreMutation = true };
        var result = await Run(port, action);
        Assert.Equal(CallTransportControlOutcome.Unverified, result.Outcome);
        Assert.True(result.NativeActionAttempted);
    }

    [Theory]
    [InlineData(true, CallTransportControlOutcome.ConnectionReported)]
    [InlineData(false, CallTransportControlOutcome.ConnectionRejected)]
    public async Task ConnectRequiresExistingRegistrationAndNeverImplicitlyRegisters(bool connected, CallTransportControlOutcome outcome)
    {
        var port = new FakeTransport { Registered = true, ConnectedResult = connected };
        var result = await Run(port, CallTransportAction.Connect);
        Assert.Equal(outcome, result.Outcome);
        Assert.False(result.AccessRequestAttempted);
        Assert.Equal(["inspect", "connect", "inspect"], port.Calls);
        Assert.Contains("not that a phone call", result.ToDisplayText());
    }

    [Fact]
    public async Task UnregisteredConnectIsBlockedWithoutAccessPrompt()
    {
        var port = new FakeTransport();
        var result = await Run(port, CallTransportAction.Connect);
        Assert.Equal(CallTransportControlOutcome.NotRegistered, result.Outcome);
        Assert.False(result.NativeActionAttempted);
        Assert.Equal(["inspect"], port.Calls);
    }

    [Fact]
    public async Task ChangedContextAfterAccessPreventsRegister()
    {
        var current = true;
        var port = new FakeTransport { AfterAccess = () => current = false };
        var result = await new CallTransportController(port).ExecuteAsync(CallTransportAction.Register, () => current, CancellationToken.None);
        Assert.Equal(CallTransportControlOutcome.Stopped, result.Outcome);
        Assert.True(result.AccessRequestAttempted);
        Assert.False(result.NativeActionAttempted);
        Assert.Equal(["inspect", "access"], port.Calls);
    }

    [Fact]
    public async Task CancelAfterRegisterDoesNotInventRollbackOrSuccessfulReadback()
    {
        using var cancellation = new CancellationTokenSource();
        var port = new FakeTransport { AfterMutation = cancellation.Cancel };
        var result = await new CallTransportController(port).ExecuteAsync(CallTransportAction.Register, () => true, cancellation.Token);
        Assert.Equal(CallTransportControlOutcome.Stopped, result.Outcome);
        Assert.True(result.NativeActionAttempted);
        Assert.Null(result.RegistrationAfter);
        Assert.True(port.Registered);
        Assert.DoesNotContain("unregister", port.Calls);
        Assert.Contains("may already have changed", result.ToDisplayText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidInitialContextOrPreCancellationDoesNotTouchNativePort(bool cancelled)
    {
        using var cancellation = new CancellationTokenSource();
        if (cancelled) await cancellation.CancelAsync();
        var port = new FakeTransport();
        var result = await new CallTransportController(port).ExecuteAsync(CallTransportAction.Register, () => cancelled, cancellation.Token);
        Assert.Equal(CallTransportControlOutcome.Stopped, result.Outcome);
        Assert.Empty(port.Calls);
    }

    [Fact]
    public async Task ExceptionAfterMutationIsUnverifiedAndDoesNotExposeRawException()
    {
        var port = new FakeTransport { AfterMutation = () => throw new IOException("private endpoint data") };
        var result = await Run(port, CallTransportAction.Register);
        Assert.Equal(CallTransportControlOutcome.Unverified, result.Outcome);
        Assert.Null(result.RegistrationAfter);
        Assert.True(port.Registered);
        Assert.DoesNotContain("private endpoint data", result.ToDisplayText());
        Assert.DoesNotContain("unregister", port.Calls);
    }

    [Fact]
    public async Task ConcurrentActionIsRejectedAndBudgetIsReleasedAfterFinish()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var port = new FakeTransport { AccessWait = async () => { entered.SetResult(); await release.Task; } };
        var controller = new CallTransportController(port);
        var first = controller.ExecuteAsync(CallTransportAction.Register, () => true, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ExecuteAsync(CallTransportAction.Unregister, () => true, CancellationToken.None));
        release.SetResult();
        Assert.Equal(CallTransportControlOutcome.Registered, (await first).Outcome);
        Assert.Equal(CallTransportControlOutcome.Unregistered, (await controller.ExecuteAsync(CallTransportAction.Unregister, () => true, CancellationToken.None)).Outcome);
    }

    private static Task<CallTransportControlResult> Run(FakeTransport port, CallTransportAction action) =>
        new CallTransportController(port).ExecuteAsync(action, () => true, CancellationToken.None);

    [Theory]
    [InlineData(CallTransportAction.Register, false, 3)]
    [InlineData(CallTransportAction.Unregister, true, 2)]
    [InlineData(CallTransportAction.Connect, true, 2)]
    public async Task FailedReadbackDoesNotConvertNativeReturnToVerifiedState(CallTransportAction action, bool registered, int failAt)
    {
        var port = new FakeTransport { Registered = registered, FailInspectionAt = failAt, ConnectedResult = true };
        var result = await Run(port, action);
        Assert.Equal(CallTransportControlOutcome.Unverified, result.Outcome);
        Assert.True(result.NativeActionAttempted);
        Assert.Null(result.RegistrationAfter);
    }

    [Fact]
    public async Task ChangedContextAfterConnectDoesNotPublishConnectedOrAttemptDisconnect()
    {
        var current = true;
        var port = new FakeTransport { Registered = true, ConnectedResult = true, AfterConnect = () => current = false };
        var result = await new CallTransportController(port).ExecuteAsync(CallTransportAction.Connect, () => current, CancellationToken.None);
        Assert.Equal(CallTransportControlOutcome.Stopped, result.Outcome);
        Assert.True(result.NativeActionAttempted);
        Assert.Null(result.RegistrationAfter);
        Assert.Equal(["inspect", "connect"], port.Calls);
    }

    [Fact]
    public async Task InitialAccessDenialIsNotInterpretedAsUnregistered()
    {
        var port = new FakeTransport { FailInspectionAt = 1, InspectionDenied = true };
        var result = await Run(port, CallTransportAction.Register);
        Assert.Equal(CallTransportControlOutcome.AccessDenied, result.Outcome);
        Assert.False(result.AccessRequestAttempted);
        Assert.False(result.NativeActionAttempted);
        Assert.Null(result.RegistrationAfter);
    }

    private sealed class FakeTransport : ICallTransportControl
    {
        public List<string> Calls { get; } = [];
        public bool Registered { get; set; }
        public bool IgnoreMutation { get; init; }
        public bool ConnectedResult { get; init; }
        public CallTransportAccess Access { get; init; } = CallTransportAccess.Allowed;
        public Action? AfterAccess { get; set; }
        public Action? AfterMutation { get; init; }
        public Func<Task>? AccessWait { get; init; }
        public Action? AfterConnect { get; init; }
        public int FailInspectionAt { get; init; }
        public bool InspectionDenied { get; init; }
        private int _inspections;
        public bool IsRegistered()
        {
            Calls.Add("inspect");
            if (++_inspections == FailInspectionAt)
            {
                if (InspectionDenied) throw new UnauthorizedAccessException("private endpoint data");
                throw new IOException("private endpoint data");
            }
            return Registered;
        }
        public async Task<CallTransportAccess> RequestAccessAsync(CancellationToken cancellationToken)
        {
            Calls.Add("access");
            if (AccessWait is not null) await AccessWait();
            AfterAccess?.Invoke(); return Access;
        }
        public void Register() { Calls.Add("register"); if (!IgnoreMutation) Registered = true; AfterMutation?.Invoke(); }
        public void Unregister() { Calls.Add("unregister"); if (!IgnoreMutation) Registered = false; AfterMutation?.Invoke(); }
        public Task<bool> ConnectAsync(CancellationToken cancellationToken) { Calls.Add("connect"); AfterConnect?.Invoke(); return Task.FromResult(ConnectedResult); }
    }
}

namespace PhoneLink.Application.Features.Calling;

public enum CallTransportAction { Register, Connect, Unregister }
public enum CallTransportAccess { Unknown, Allowed, DeniedByUser, DeniedBySystem }
public enum CallTransportControlOutcome
{
    Registered, Unregistered, ConnectionReported, ConnectionRejected, NoActionRequired,
    NotRegistered, AccessDenied, AccessUnknown, Stopped, Unavailable, Unverified,
}

public interface ICallTransportControl
{
    bool IsRegistered();
    Task<CallTransportAccess> RequestAccessAsync(CancellationToken cancellationToken);
    void Register();
    void Unregister();
    Task<bool> ConnectAsync(CancellationToken cancellationToken);
}

// No endpoint identity, number or call history is retained in operation results.
public sealed record CallTransportControlResult(CallTransportAction Action, CallTransportControlOutcome Outcome,
    bool AccessRequestAttempted, bool NativeActionAttempted, bool? RegistrationAfter)
{
    public string ToDisplayText() => $"Action: {Action}. Result: {Outcome}. " +
        $"Access request attempted: {AccessRequestAttempted}. Native action attempted: {NativeActionAttempted}. " +
        $"Registration observed afterward: {(RegistrationAfter is { } registered ? registered ? "yes" : "no" : "unknown")}.\n" +
        "Registration/access can persist after cancellation, navigation, lock or app exit. A stopped or unverified operation may already have changed Windows state; inspect again before a new explicit action. No automatic rollback or retry occurs. " +
        "ConnectionReported means Windows returned true at that time, not that a phone call, audio route or microphone works. No number, dial, answer, hang-up or audio-routing command was sent.";
}

// Caller confirms the exact selected endpoint/action first. Each step rechecks that UI/lifetime context.
public sealed class CallTransportController(ICallTransportControl transport)
{
    private readonly ICallTransportControl _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    private int _busy;

    public async Task<CallTransportControlResult> ExecuteAsync(CallTransportAction action,
        Func<bool> contextIsCurrent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contextIsCurrent);
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) throw new InvalidOperationException("A transport action is already in progress.");
        var accessAttempted = false;
        var actionAttempted = false;
        bool? after = null;
        try
        {
            CheckContext();
            var registered = _transport.IsRegistered();
            CheckContext();
            if (action == CallTransportAction.Register)
            {
                if (registered) { after = true; return Result(CallTransportControlOutcome.NoActionRequired); }
                accessAttempted = true;
                var access = await _transport.RequestAccessAsync(cancellationToken);
                CheckContext();
                if (access != CallTransportAccess.Allowed)
                    return Result(access is CallTransportAccess.DeniedByUser or CallTransportAccess.DeniedBySystem
                        ? CallTransportControlOutcome.AccessDenied : CallTransportControlOutcome.AccessUnknown);
                // A prompt can outlive another actor's registration. Never assume the old observation is current.
                registered = _transport.IsRegistered();
                CheckContext();
                if (registered) { after = true; return Result(CallTransportControlOutcome.NoActionRequired); }
                actionAttempted = true;
                _transport.Register();
                CheckContext();
                after = _transport.IsRegistered();
                CheckContext();
                return Result(after == true ? CallTransportControlOutcome.Registered : CallTransportControlOutcome.Unverified);
            }
            if (action == CallTransportAction.Unregister)
            {
                if (!registered) { after = false; return Result(CallTransportControlOutcome.NoActionRequired); }
                actionAttempted = true;
                _transport.Unregister();
                CheckContext();
                after = _transport.IsRegistered();
                CheckContext();
                return Result(after == false ? CallTransportControlOutcome.Unregistered : CallTransportControlOutcome.Unverified);
            }
            if (!registered) { after = false; return Result(CallTransportControlOutcome.NotRegistered); }
            actionAttempted = true;
            var connected = await _transport.ConnectAsync(cancellationToken);
            CheckContext();
            after = _transport.IsRegistered();
            CheckContext();
            return Result(connected ? CallTransportControlOutcome.ConnectionReported : CallTransportControlOutcome.ConnectionRejected);
        }
        catch (OperationCanceledException) { after = null; return Result(CallTransportControlOutcome.Stopped); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            after = null;
            return Result(actionAttempted ? CallTransportControlOutcome.Unverified :
                exception.HResult == unchecked((int)0x80070005) ? CallTransportControlOutcome.AccessDenied : CallTransportControlOutcome.Unavailable);
        }
        finally { Volatile.Write(ref _busy, 0); }

        CallTransportControlResult Result(CallTransportControlOutcome outcome) => new(action, outcome, accessAttempted, actionAttempted, after);
        void CheckContext()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!contextIsCurrent()) throw new OperationCanceledException("Selected transport context changed.");
        }
    }
}

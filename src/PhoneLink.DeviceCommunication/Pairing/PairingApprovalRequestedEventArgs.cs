namespace PhoneLink.DeviceCommunication.Pairing;

public sealed class PairingApprovalRequestedEventArgs : EventArgs
{
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal PairingApprovalRequestedEventArgs(
        Guid sessionId,
        Guid clientDeviceId,
        string clientDisplayName,
        string comparisonPhrase,
        DateTimeOffset expiresAt,
        CancellationToken attemptEnded)
    {
        SessionId = sessionId;
        ClientDeviceId = clientDeviceId;
        ClientDisplayName = clientDisplayName;
        ComparisonPhrase = comparisonPhrase;
        ExpiresAt = expiresAt;
        AttemptEnded = attemptEnded;
    }

    public Guid SessionId { get; }

    public Guid ClientDeviceId { get; }

    public string ClientDisplayName { get; }

    public string ComparisonPhrase { get; }

    public DateTimeOffset ExpiresAt { get; }
    public CancellationToken AttemptEnded { get; }

    public bool Submit(bool approved) => !AttemptEnded.IsCancellationRequested && _completion.TrySetResult(approved);

    internal Task<bool> WaitAsync(CancellationToken cancellationToken)
    {
        var remaining = ExpiresAt - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero
            ? _completion.Task.WaitAsync(remaining, cancellationToken)
            : Task.FromResult(false);
    }
}

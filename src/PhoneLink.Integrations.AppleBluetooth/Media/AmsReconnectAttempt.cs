using Windows.Devices.Enumeration;

namespace PhoneLink.Integrations.AppleBluetooth.Media;

/// <summary>One explicitly opted-in AMS reconnect to the original selected paired device.</summary>
public sealed class AmsReconnectAttempt
{
    private readonly AmsDeviceCandidate _candidate;
    private int _used;

    public AmsReconnectAttempt(AmsDeviceCandidate candidate) =>
        _candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));

    public Task<AmsSession?> TryReconnectAsync(AmsSessionEndCause cause, Func<bool> contextIsCurrent,
        Func<Task> release, CancellationToken cancellationToken) =>
        TryReconnectAsync(cause, contextIsCurrent, release, AmsSession.ReopenWithExistingAccessAsync,
            TimeSpan.FromSeconds(2), cancellationToken);

    internal async Task<T?> TryReconnectAsync<T>(AmsSessionEndCause cause, Func<bool> contextIsCurrent, Func<Task> release,
        Func<AmsDeviceCandidate, CancellationToken, Task<T>> reopen, TimeSpan delay, CancellationToken cancellationToken)
        where T : class, IAsyncDisposable
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (cause != AmsSessionEndCause.BluetoothDisconnected || !contextIsCurrent() ||
            Interlocked.Exchange(ref _used, 1) != 0) return null;

        // Do not race cleanup against new subscriptions, including when Stop arrives while draining.
        await release();
        await Task.Delay(delay, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!contextIsCurrent()) return null;
        var replacement = await reopen(_candidate, cancellationToken);
        if (cancellationToken.IsCancellationRequested || !contextIsCurrent())
        {
            await replacement.DisposeAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
        return replacement;
    }

    internal static void RequireExistingAccess(DeviceAccessStatus status)
    {
        if (status != DeviceAccessStatus.Allowed)
            throw new InvalidOperationException($"Windows did not report existing Bluetooth access ({status}). No access was requested.");
    }
}

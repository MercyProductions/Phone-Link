namespace PhoneLink.Application.Features.Media;

public enum RemoteAudioState { Closed, Opened, Released, Unknown }
public enum RemoteAudioOpenOutcome { Opened, TimedOut, DeniedBySystem, UnknownFailure }

public interface IRemoteAudioConnection : IDisposable
{
    RemoteAudioState State { get; }
    Task EnableAsync(CancellationToken cancellationToken);
    Task<RemoteAudioOpenOutcome> OpenAsync(CancellationToken cancellationToken);
}

/// <summary>Owns one explicitly selected connection and one enable/open attempt.</summary>
public sealed class RemoteAudioSession(IRemoteAudioConnection connection) : IDisposable
{
    private readonly IRemoteAudioConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    private int _attempted;
    private int _disposed;
    private int _openSucceeded;
    private int _wasOpened;
    public bool WasOpened => Volatile.Read(ref _wasOpened) != 0;

    public RemoteAudioState State
    {
        get
        {
            var release = Volatile.Read(ref _disposed);
            if (release != 0) return release == 2 ? RemoteAudioState.Released : RemoteAudioState.Unknown;
            try
            {
                var state = _connection.State;
                release = Volatile.Read(ref _disposed);
                if (release == 0 && state == RemoteAudioState.Opened && Volatile.Read(ref _openSucceeded) != 0)
                    Volatile.Write(ref _wasOpened, 1);
                return release == 0 ? state : release == 2 ? RemoteAudioState.Released : RemoteAudioState.Unknown;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException) { return RemoteAudioState.Unknown; }
        }
    }

    public async Task<RemoteAudioOpenOutcome> OpenAsync(Func<bool> contextIsCurrent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contextIsCurrent);
        if (Interlocked.Exchange(ref _attempted, 1) != 0) throw new InvalidOperationException("Create a new session for a new manual attempt.");
        try
        {
            CheckContext();
            using var registration = cancellationToken.Register(ReleaseOnCancellation);
            CheckContext();
            await _connection.EnableAsync(cancellationToken);
            CheckContext();
            var outcome = await _connection.OpenAsync(cancellationToken);
            CheckContext();
            if (outcome != RemoteAudioOpenOutcome.Opened) Dispose();
            else
            {
                Volatile.Write(ref _openSucceeded, 1);
                _ = State;
            }
            return outcome;
        }
        catch
        {
            Dispose();
            throw;
        }

        void CheckContext()
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!contextIsCurrent()) throw new OperationCanceledException("Playback context changed.");
        }
    }

    private void ReleaseOnCancellation()
    {
        try { Dispose(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { /* State remains Unknown. No retry. */ }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        try { _connection.Dispose(); Volatile.Write(ref _disposed, 2); }
        catch { Volatile.Write(ref _disposed, 3); throw; }
    }
}

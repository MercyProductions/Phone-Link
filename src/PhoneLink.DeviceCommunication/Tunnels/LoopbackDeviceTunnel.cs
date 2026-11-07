using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Xml;
using PhoneLink.DeviceCommunication.Diagnostics;

namespace PhoneLink.DeviceCommunication.Tunnels;

public sealed record TunnelStatistics(int ActiveConnections, long BytesToDevice, long BytesFromDevice,
    int FailedConnections, string? LastFailureCategory)
{
    public long RejectedAtCapacity { get; init; }
    public int? LastUsbResponseCode { get; init; }
}

public sealed class TunnelConnection(Stream stream, IDisposable? owner = null, Action? completeWriting = null) : IAsyncDisposable
{
    public Stream Stream { get; } = stream ?? throw new ArgumentNullException(nameof(stream));
    public void CompleteWriting() => completeWriting?.Invoke();
    public async ValueTask DisposeAsync()
    {
        try { await Stream.DisposeAsync().ConfigureAwait(false); }
        finally { owner?.Dispose(); }
    }
}

public enum TunnelTargetState { Unknown, Present, MissingOrAmbiguous, StoppedAfterTargetLoss }

public sealed class LoopbackDeviceTunnel : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<CancellationToken, Task<TunnelConnection>> _connect;
    private readonly CancellationTokenSource _stop;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<long, Task> _connections = new();
    private readonly object _disposeGate = new();
    private readonly Task _acceptTask;
    private readonly Task _monitorTask;
    private Task? _disposeTask;
    private long _connectionId, _sent, _received, _rejected;
    private int _active, _failed, _targetState;
    private ConnectionFailure? _lastFailure;
    private sealed record ConnectionFailure(string Category, int? UsbResponseCode);

    private LoopbackDeviceTunnel(Func<CancellationToken, Task<TunnelConnection>> connect,
        int localPort, int maximumConnections, CancellationToken cancellationToken,
        Func<CancellationToken, Task<bool>>? isTargetPresent = null, TimeSpan monitorInterval = default)
    {
        _connect = connect;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        StoppingToken = _stop.Token;
        _slots = new SemaphoreSlim(maximumConnections, maximumConnections);
        _listener = new TcpListener(IPAddress.Loopback, localPort);
        try
        {
            _listener.ExclusiveAddressUse = true;
            _listener.Start(maximumConnections);
        }
        catch { _listener.Dispose(); _stop.Dispose(); _slots.Dispose(); throw; }
        LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptTask = AcceptAsync();
        _monitorTask = isTargetPresent is null ? Task.CompletedTask : MonitorTargetAsync(isTargetPresent, monitorInterval);
    }

    public int LocalPort { get; }
    public bool IsRunning => !StoppingToken.IsCancellationRequested && !_acceptTask.IsCompleted;
    public Task Completion => _acceptTask;
    public CancellationToken StoppingToken { get; }
    public TunnelTargetState TargetState => (TunnelTargetState)Volatile.Read(ref _targetState);

    public static LoopbackDeviceTunnel Start(Func<CancellationToken, Task<TunnelConnection>> connect,
        int localPort = 0, int maximumConnections = 4, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentOutOfRangeException.ThrowIfNegative(localPort);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(localPort, 65_535);
        if (maximumConnections is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(maximumConnections));
        cancellationToken.ThrowIfCancellationRequested();
        return new(connect, localPort, maximumConnections, cancellationToken);
    }

    internal static LoopbackDeviceTunnel StartWithTargetMonitoring(Func<CancellationToken, Task<TunnelConnection>> connect,
        Func<CancellationToken, Task<bool>> isTargetPresent, TimeSpan interval, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentNullException.ThrowIfNull(isTargetPresent);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        return new(connect, 0, 4, cancellationToken, isTargetPresent, interval);
    }

    private async Task MonitorTargetAsync(Func<CancellationToken, Task<bool>> isTargetPresent, TimeSpan interval)
    {
        var misses = 0;
        try
        {
            while (true)
            {
                await Task.Delay(interval, StoppingToken).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(StoppingToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                TunnelTargetState state;
                try
                {
                    var present = await isTargetPresent(deadline.Token).ConfigureAwait(false);
                    deadline.Token.ThrowIfCancellationRequested();
                    misses = present ? 0 : misses + 1;
                    state = present ? TunnelTargetState.Present : misses == 1 ?
                        TunnelTargetState.MissingOrAmbiguous : TunnelTargetState.StoppedAfterTargetLoss;
                }
                catch (Exception exception) when (exception is IOException or SocketException or
                    InvalidDataException or XmlException or OperationCanceledException)
                {
                    StoppingToken.ThrowIfCancellationRequested();
                    misses = 0;
                    state = TunnelTargetState.Unknown;
                }
                StoppingToken.ThrowIfCancellationRequested();
                Volatile.Write(ref _targetState, (int)state);
                if (state == TunnelTargetState.StoppedAfterTargetLoss)
                {
                    await _stop.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (StoppingToken.IsCancellationRequested) { }
    }

    public TunnelStatistics CaptureStatistics()
    {
        var failure = Volatile.Read(ref _lastFailure);
        return new(Volatile.Read(ref _active), Interlocked.Read(ref _sent), Interlocked.Read(ref _received),
            Volatile.Read(ref _failed), failure?.Category)
        {
            RejectedAtCapacity = Interlocked.Read(ref _rejected),
            LastUsbResponseCode = failure?.UsbResponseCode,
        };
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) { return new(_disposeTask ??= StopAsync()); }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var local = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                if (!_slots.Wait(0)) { Interlocked.Increment(ref _rejected); local.Dispose(); continue; }
                var id = Interlocked.Increment(ref _connectionId);
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _connections[id] = completion.Task;
                Interlocked.Increment(ref _active);
                _ = RunConnectionAsync(local, id, completion);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        finally
        {
            _listener.Stop();
            if (!_stop.IsCancellationRequested) await _stop.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task RunConnectionAsync(TcpClient local, long id, TaskCompletionSource completion)
    {
        TunnelConnection? remote = null;
        Task? outgoing = null, incoming = null;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        try
        {
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                remote = await _connect(connectTimeout.Token).ConfigureAwait(false);
            }
            var localStream = local.GetStream();
            outgoing = PumpAsync(localStream, remote.Stream, toDevice: true, () => remote.CompleteWriting(), lifetime.Token);
            incoming = PumpAsync(remote.Stream, localStream, toDevice: false,
                () => local.Client.Shutdown(SocketShutdown.Send), lifetime.Token);
            var first = await Task.WhenAny(outgoing, incoming).ConfigureAwait(false);
            await first.ConfigureAwait(false);
            // Preserve a response after a local half-close, but never retain a stuck drain indefinitely.
            await Task.WhenAll(outgoing, incoming).WaitAsync(TimeSpan.FromSeconds(10), lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception) when (_stop.IsCancellationRequested &&
            exception is IOException or SocketException or ObjectDisposedException) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Interlocked.Increment(ref _failed);
            Volatile.Write(ref _lastFailure, new(exception.GetType().Name,
                remote is null && exception is UsbMuxChannelUnavailableException rejected ? rejected.ResponseCode : null));
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            local.Dispose();
            if (remote is not null)
            {
                try { await remote.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { Volatile.Write(ref _lastFailure, new(exception.GetType().Name, null)); }
            }
            try { await Task.WhenAll(new[] { outgoing, incoming }.OfType<Task>()).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { /* Observe failed/cancelled pumps; no payload logging. */ }
            Interlocked.Decrement(ref _active);
            _slots.Release();
            _connections.TryRemove(id, out _);
            completion.TrySetResult();
        }
    }

    private async Task PumpAsync(Stream source, Stream destination, bool toDevice,
        Action completeWriting, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    try { completeWriting(); }
                    catch (SocketException exception) when (exception.SocketErrorCode is SocketError.NotConnected or SocketError.Shutdown) { }
                    return;
                }
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (toDevice) Interlocked.Add(ref _sent, count);
                else Interlocked.Add(ref _received, count);
            }
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private async Task StopAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try { await _acceptTask.ConfigureAwait(false); }
        finally
        {
            try { await Task.WhenAll(_connections.Values.Append(_monitorTask).ToArray()).ConfigureAwait(false); }
            finally { _slots.Dispose(); _stop.Dispose(); }
        }
    }
}

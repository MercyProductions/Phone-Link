using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using PhoneLink.DeviceCommunication.Tunnels;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class LoopbackDeviceTunnelTests
{
    [Fact]
    public async Task ForwardsBothDirectionsAndSharesConcurrentStop()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var remoteListener = new TcpListener(IPAddress.Loopback, 0);
        remoteListener.Start();
        try
        {
            var port = ((IPEndPoint)remoteListener.LocalEndpoint).Port;
            var remoteWork = EchoAsync(remoteListener, deadline.Token);
            await using var tunnel = LoopbackDeviceTunnel.Start(token => ConnectAsync(port, token));
            using var local = new TcpClient();
            await local.ConnectAsync(IPAddress.Loopback, tunnel.LocalPort, deadline.Token);
            var bytes = RandomNumberGenerator.GetBytes(180_000);
            await local.GetStream().WriteAsync(bytes, deadline.Token);
            var received = new byte[bytes.Length];
            await local.GetStream().ReadExactlyAsync(received, deadline.Token);
            Assert.Equal(bytes, received);
            await Task.WhenAll(tunnel.DisposeAsync().AsTask(), tunnel.DisposeAsync().AsTask());
            var stats = tunnel.CaptureStatistics();
            Assert.Equal(0, stats.ActiveConnections);
            Assert.Equal(bytes.Length, stats.BytesToDevice);
            Assert.Equal(bytes.Length, stats.BytesFromDevice);
            Assert.False(tunnel.IsRunning);
            await remoteWork;
        }
        finally { remoteListener.Stop(); }
    }

    [Fact]
    public async Task PreservesResponseAfterClientHalfClosesItsWriteSide()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var remoteWork = ReplyAfterEofAsync(listener, deadline.Token);
            await using var tunnel = LoopbackDeviceTunnel.Start(token => ConnectAsync(port, token));
            using var local = new TcpClient();
            await local.ConnectAsync(IPAddress.Loopback, tunnel.LocalPort, deadline.Token);
            var localStream = local.GetStream();
            await localStream.WriteAsync("request"u8.ToArray(), deadline.Token);
            local.Client.Shutdown(SocketShutdown.Send);
            var reply = new byte[8];
            await localStream.ReadExactlyAsync(reply, deadline.Token);
            Assert.Equal("response", Encoding.ASCII.GetString(reply));
            await remoteWork;
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task StopCancelsAnInFlightConnectorAndReleasesListeningPort()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var tunnel = LoopbackDeviceTunnel.Start(async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable");
        });
        using var local = new TcpClient();
        await local.ConnectAsync(IPAddress.Loopback, tunnel.LocalPort, deadline.Token);
        await entered.Task.WaitAsync(deadline.Token);
        await tunnel.DisposeAsync().AsTask().WaitAsync(deadline.Token);
        using var rejected = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(async () =>
            await rejected.ConnectAsync(IPAddress.Loopback, tunnel.LocalPort, deadline.Token));
        Assert.Equal(0, tunnel.CaptureStatistics().ActiveConnections);
    }

    [Fact]
    public async Task LimitsConcurrentConnections()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await using var tunnel = LoopbackDeviceTunnel.Start(async token =>
        {
            Interlocked.Increment(ref count);
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable");
        }, maximumConnections: 1);
        using var first = new TcpClient();
        await first.ConnectAsync(IPAddress.Loopback, tunnel.LocalPort, deadline.Token);
        await entered.Task.WaitAsync(deadline.Token);
        using var excess = new TcpClient();
        await excess.ConnectAsync(IPAddress.Loopback, tunnel.LocalPort, deadline.Token);
        Assert.Equal(0, await excess.GetStream().ReadAsync(new byte[1], deadline.Token));
        Assert.Equal(1, count);
        Assert.Equal(1, tunnel.CaptureStatistics().ActiveConnections);
    }

    [Fact]
    public async Task ConnectorFailureClosesClientAndDoesNotRetainSensitiveMessage()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var tunnel = LoopbackDeviceTunnel.Start(_ =>
            throw new InvalidOperationException("private-device-identifier-must-not-be-retained"));
        using var local = new TcpClient();
        await local.ConnectAsync(IPAddress.Loopback, tunnel.LocalPort, deadline.Token);
        Assert.Equal(0, await local.GetStream().ReadAsync(new byte[1], deadline.Token));
        await tunnel.DisposeAsync().AsTask().WaitAsync(deadline.Token);
        var stats = tunnel.CaptureStatistics();
        Assert.Equal(0, stats.ActiveConnections);
        Assert.Equal(1, stats.FailedConnections);
        Assert.Equal(nameof(InvalidOperationException), stats.LastFailureCategory);
        Assert.DoesNotContain("private-device", stats.ToString());
    }

    [Fact]
    public async Task ParentCancellationStopsAcceptingAndClosesActiveStreams()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var lifetime = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            await using var tunnel = LoopbackDeviceTunnel.Start(token => ConnectAsync(port, token), cancellationToken: lifetime.Token);
            using var local = new TcpClient();
            await local.ConnectAsync(IPAddress.Loopback, tunnel.LocalPort, deadline.Token);
            using var remote = await listener.AcceptTcpClientAsync(deadline.Token);
            var stream = remote.GetStream();
            await lifetime.CancelAsync();
            await tunnel.Completion.WaitAsync(deadline.Token);
            await tunnel.DisposeAsync().AsTask().WaitAsync(deadline.Token);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], deadline.Token));
            Assert.Equal(0, tunnel.CaptureStatistics().ActiveConnections);
            Assert.Equal(0, tunnel.CaptureStatistics().FailedConnections);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public void RejectsInvalidListenerOptions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LoopbackDeviceTunnel.Start(_ => throw new InvalidOperationException(), localPort: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => LoopbackDeviceTunnel.Start(_ => throw new InvalidOperationException(), maximumConnections: 0));
    }

    [Fact]
    public void PinsNormalizedUsbIdentityWithoutPublishingRawIdentifier()
    {
        static Dictionary<string, XElement> Inventory(string serial, string type, int id) => new()
        {
            ["DeviceList"] = new XElement("array", new XElement("dict", new XElement("key", "DeviceID"), new XElement("integer", id),
                new XElement("key", "Properties"), new XElement("dict", new XElement("key", "ConnectionType"), new XElement("string", type),
                    new XElement("key", "SerialNumber"), new XElement("string", serial)))),
        };
        var first = Assert.Single(PinnedUsbDeviceConnector.ParseTargets(Inventory("ABCD-12345678", "USB", 1)));
        var same = Assert.Single(PinnedUsbDeviceConnector.ParseTargets(Inventory("abcd12345678", "USB", 2)));
        var other = Assert.Single(PinnedUsbDeviceConnector.ParseTargets(Inventory("ABCD-87654321", "USB", 1)));
        Assert.Equal(first.IdentityHash, same.IdentityHash);
        Assert.NotEqual(first.IdentityHash, other.IdentityHash);
        Assert.Empty(PinnedUsbDeviceConnector.ParseTargets(Inventory("ABCD-12345678", "Network", 1)));
        Assert.Throws<InvalidDataException>(() => PinnedUsbDeviceConnector.ParseTargets(Inventory("", "USB", 1)));
        Assert.DoesNotContain("ABCD", first.ToString());
    }

    private static async Task<TunnelConnection> ConnectAsync(int port, CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            return new(client.GetStream(), client, () => client.Client.Shutdown(SocketShutdown.Send));
        }
        catch { client.Dispose(); throw; }
    }

    private static async Task EchoAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var remote = await listener.AcceptTcpClientAsync(cancellationToken);
        var stream = remote.GetStream();
        var buffer = new byte[16_384];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) != 0)
            await stream.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
    }

    private static async Task ReplyAfterEofAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var remote = await listener.AcceptTcpClientAsync(cancellationToken);
        using var request = new MemoryStream();
        var stream = remote.GetStream();
        await stream.CopyToAsync(request, cancellationToken);
        Assert.Equal("request", Encoding.ASCII.GetString(request.ToArray()));
        await stream.WriteAsync("response"u8.ToArray(), cancellationToken);
    }
}

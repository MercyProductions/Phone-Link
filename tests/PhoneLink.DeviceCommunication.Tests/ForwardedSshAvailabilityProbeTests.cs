using System.Net;
using System.Net.Sockets;
using System.Text;
using PhoneLink.DeviceCommunication.Diagnostics;
using PhoneLink.DeviceCommunication.Tunnels;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class ForwardedSshAvailabilityProbeTests
{
    [Fact]
    public async Task OneShotCheckUsesOnlySelectedPortAndClosesTheTunnel()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var fixturePort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var fixture = ServeIdentificationAsync(listener, "SSH-2.0-Fixture_1\r\n", deadline.Token);
        var connections = 0;
        var result = await ForwardedSshAvailabilityProbe.VerifyUsbAsync(2222, (port, token) =>
        {
            Assert.Equal(2222, port);
            Interlocked.Increment(ref connections);
            return ConnectAsync(fixturePort, token);
        }, deadline.Token);
        await fixture.WaitAsync(deadline.Token);
        Assert.Equal(2222, result.DevicePort);
        Assert.True(result.Availability.ServerIdentified);
        Assert.Null(result.Availability.TransportResponseCode);
        Assert.Null(result.Statistics.LastUsbResponseCode);
        Assert.Equal(1, connections);
        Assert.Equal(0, result.Statistics.ActiveConnections);
        Assert.Equal(0, result.Statistics.FailedConnections);
        Assert.Equal("SSH-2.0-PhoneLinkProbe_1\r\n"u8.Length, result.Statistics.BytesToDevice);
    }

    [Fact]
    public async Task OneShotCheckDistinguishesUsbRejectionFromAnSshServer()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await ForwardedSshAvailabilityProbe.VerifyUsbAsync(22,
            (_, _) => throw new UsbMuxChannelUnavailableException(3), deadline.Token);
        Assert.False(result.Availability.ServerIdentified);
        Assert.Equal(3, result.Availability.TransportResponseCode);
        Assert.Contains("USB channel rejection", result.Availability.Status);
        Assert.Equal(3, result.Statistics.LastUsbResponseCode);
        Assert.Equal(nameof(UsbMuxChannelUnavailableException), result.Statistics.LastFailureCategory);
        Assert.Equal(1, result.Statistics.FailedConnections);
        Assert.Equal(0, result.Statistics.ActiveConnections);
        Assert.Equal(0, result.Statistics.BytesToDevice);
    }

    [Theory]
    [InlineData("SSH-2.0-Fixture_1\r\n", true)]
    [InlineData("HTTP/1.1 200 OK\r\n", false)]
    public async Task ChecksTheActualForwardedEndpointAndSendsOnlyIdentification(string identification, bool expected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = ServeIdentificationAsync(listener, identification, deadline.Token);
            await using var tunnel = LoopbackDeviceTunnel.Start(token => ConnectAsync(port, token));
            var result = await ForwardedSshAvailabilityProbe.ProbeAsync(tunnel, deadline.Token);
            Assert.Equal(expected, result.ServerIdentified);
            Assert.Contains("no login", result.Status, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Fixture", result.ToString());
            await server.WaitAsync(deadline.Token);
            await tunnel.DisposeAsync().AsTask().WaitAsync(deadline.Token);
            var stats = tunnel.CaptureStatistics();
            Assert.Equal(0, stats.ActiveConnections);
            Assert.Equal("SSH-2.0-PhoneLinkProbe_1\r\n"u8.Length, stats.BytesToDevice);
            Assert.Equal(Encoding.ASCII.GetByteCount(identification), stats.BytesFromDevice);
            Assert.Equal(0, stats.FailedConnections);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task StoppedTunnelIsNeverProbedOrReplaced()
    {
        var connections = 0;
        await using var tunnel = LoopbackDeviceTunnel.Start(_ =>
        {
            Interlocked.Increment(ref connections);
            throw new InvalidOperationException("Should not connect");
        });
        await tunnel.DisposeAsync();
        var result = await ForwardedSshAvailabilityProbe.ProbeAsync(tunnel, CancellationToken.None);
        Assert.False(result.ServerIdentified);
        Assert.Contains("stopped", result.Status);
        Assert.Equal(0, connections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TunnelStopAndCallerCancellationInterruptSilentPeer(bool callerCancels)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            await using var tunnel = LoopbackDeviceTunnel.Start(token => ConnectAsync(port, token));
            var probe = ForwardedSshAvailabilityProbe.ProbeAsync(tunnel, caller.Token);
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            var clientIdentification = new byte["SSH-2.0-PhoneLinkProbe_1\r\n"u8.Length];
            await peer.GetStream().ReadExactlyAsync(clientIdentification, deadline.Token);
            if (callerCancels)
            {
                await caller.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.WaitAsync(deadline.Token));
                await tunnel.DisposeAsync().AsTask().WaitAsync(deadline.Token);
            }
            else
            {
                await tunnel.DisposeAsync().AsTask().WaitAsync(deadline.Token);
                var result = await probe.WaitAsync(deadline.Token);
                Assert.False(result.ServerIdentified);
                Assert.Contains("stopped", result.Status);
            }
            Assert.Equal(0, tunnel.CaptureStatistics().ActiveConnections);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task SilentEndpointTimesOutWithoutClaimingAbsenceOrStoppingTheForwarder()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            await using var tunnel = LoopbackDeviceTunnel.Start(token => ConnectAsync(port, token));
            var probe = ForwardedSshAvailabilityProbe.ProbeAsync(tunnel, deadline.Token);
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            var result = await probe.WaitAsync(deadline.Token);
            Assert.False(result.ServerIdentified);
            Assert.Contains("timed out", result.Status);
            Assert.Contains("unknown", result.Status);
            Assert.True(tunnel.IsRunning);
            await tunnel.DisposeAsync().AsTask().WaitAsync(deadline.Token);
            Assert.Equal(0, tunnel.CaptureStatistics().ActiveConnections);
        }
        finally { listener.Stop(); }
    }

    private static async Task ServeIdentificationAsync(TcpListener listener, string identification, CancellationToken token)
    {
        using var peer = await listener.AcceptTcpClientAsync(token);
        var stream = peer.GetStream();
        var clientIdentification = new byte["SSH-2.0-PhoneLinkProbe_1\r\n"u8.Length];
        await stream.ReadExactlyAsync(clientIdentification, token);
        Assert.Equal("SSH-2.0-PhoneLinkProbe_1\r\n", Encoding.ASCII.GetString(clientIdentification));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(identification), token);
        // Non-SSH closes after its line; SSH fixture waits for EOF to prove no auth/command was sent.
        if (identification.StartsWith("SSH-", StringComparison.Ordinal))
            Assert.Equal(0, await stream.ReadAsync(new byte[1], token));
    }

    private static async Task<TunnelConnection> ConnectAsync(int port, CancellationToken token)
    {
        var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, token);
            return new(client.GetStream(), client, () => client.Client.Shutdown(SocketShutdown.Send));
        }
        catch { client.Dispose(); throw; }
    }
}

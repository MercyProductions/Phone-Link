using System.Buffers.Binary;
using System.Text;
using PhoneLink.DeviceCommunication.Diagnostics;
using Xunit.Abstractions;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class ExperimentalUsbMuxProbeTests(ITestOutputHelper output)
{
    [Fact]
    public void CountsTransportsWithoutReturningDeviceIdentifiers()
    {
        var inventory = ExperimentalUsbMuxProbe.ParseInventory(Encoding.UTF8.GetBytes("""
            <?xml version="1.0"?><!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict><key>DeviceList</key><array>
            <dict><key>Properties</key><dict><key>ConnectionType</key><string>USB</string><key>SerialNumber</key><string>PRIVATE</string></dict></dict>
            <dict><key>Properties</key><dict><key>ConnectionType</key><string>Network</string></dict></dict>
            </array></dict></plist>
            """));
        Assert.Equal(new UsbMuxInventory(1, 1, 0), inventory);
        Assert.DoesNotContain("PRIVATE", inventory.ToString());
    }

    [Theory]
    [InlineData("<plist><dict><key>Number</key><integer>1</integer></dict></plist>")]
    [InlineData("<plist><dict><key>DeviceList</key><array/><key>DeviceList</key><array/></dict></plist>")]
    [InlineData("<plist><dict><key>DeviceList</key></dict></plist>")]
    [InlineData("<plist><dict><key>DeviceList</key><array><dict/></array></dict></plist>")]
    public void RejectsMalformedOrUnsupportedReplies(string xml)
    {
        Assert.Throws<InvalidDataException>(() => ExperimentalUsbMuxProbe.ParseInventory(Encoding.UTF8.GetBytes(xml)));
    }

    [Fact]
    public async Task SendsOnlyListDevicesAndHandlesFragmentedReads()
    {
        var body = Encoding.UTF8.GetBytes("<plist><dict><key>DeviceList</key><array/></dict></plist>");
        var reply = new byte[16 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(reply, reply.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(8), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(12), 1);
        body.CopyTo(reply, 16);
        await using var stream = new TestStream(reply);
        Assert.Equal(new UsbMuxInventory(0, 0, 0), await ExperimentalUsbMuxProbe.ExchangeAsync(stream, CancellationToken.None));
        var request = Encoding.UTF8.GetString(stream.Written.ToArray()[16..]);
        Assert.Contains("ListDevices", request);
        Assert.DoesNotContain("PairRecord", request);
        Assert.DoesNotContain("Connect</string>", request);
    }

    [Fact]
    public async Task RejectsOversizedFrameBeforeAllocatingBody()
    {
        var reply = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(reply, uint.MaxValue);
        await using var stream = new TestStream(reply);
        await Assert.ThrowsAsync<InvalidDataException>(() => ExperimentalUsbMuxProbe.ExchangeAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    public async Task RejectsWrongVersionMessageOrCorrelationTag(int offset)
    {
        var reply = new byte[17];
        BinaryPrimitives.WriteUInt32LittleEndian(reply, 17);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(8), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(offset), 99);
        await using var stream = new TestStream(reply);
        await Assert.ThrowsAsync<InvalidDataException>(() => ExperimentalUsbMuxProbe.ExchangeAsync(stream, CancellationToken.None));
    }

    [Fact]
    public void DoesNotResolveExternalEntities()
    {
        var payload = Encoding.UTF8.GetBytes("""
            <!DOCTYPE plist [<!ENTITY private SYSTEM "file:///C:/Windows/win.ini">]>
            <plist><dict><key>DeviceList</key><array/><key>Untrusted</key><string>&private;</string></dict></plist>
            """);
        Assert.Throws<System.Xml.XmlException>(() => ExperimentalUsbMuxProbe.ParseInventory(payload));
    }

    [Fact]
    public async Task ReadOnlyLocalServiceProbe()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var inventory = await ExperimentalUsbMuxProbe.ProbeAsync(cancellation.Token);
            output.WriteLine($"Local Apple transport replied: USB={inventory.UsbConnections}, network={inventory.NetworkConnections}, other={inventory.OtherConnections}.");
            Assert.InRange(inventory.UsbConnections + inventory.NetworkConnections + inventory.OtherConnections, 0, 256);
        }
        catch (Exception exception) when (exception is System.Net.Sockets.SocketException or OperationCanceledException or InvalidDataException or EndOfStreamException)
        {
            // A missing/different local service is a supported host state, not proof of phone failure.
            output.WriteLine($"Experimental local transport unavailable: {exception.GetType().Name}.");
        }
    }

    private sealed class TestStream(byte[] reply) : Stream
    {
        private readonly MemoryStream _reply = new(reply, writable: false);
        public MemoryStream Written { get; } = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _reply.Read(buffer, offset, Math.Min(count, 3));
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _reply.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            Written.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _reply.Dispose(); Written.Dispose(); }
            base.Dispose(disposing);
        }
    }
}

using System.Text.Json;
using PhoneLink.DeviceCommunication.Diagnostics;
using PhoneLink.DeviceCommunication.Sessions;
using PhoneLink.Domain.Devices;
using PhoneLink.Protocol.Messages;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class CompanionRuntimeFeedTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private static bool Current(DeviceId device, Guid session) => device.Value != Guid.Empty && session != Guid.Empty;

    [Fact]
    public void BothArmingAndCurrentAuthenticatedSessionAreRequired()
    {
        var feed = new CompanionRuntimeFeed();
        var request = Request(Now);
        var server = Guid.NewGuid();
        Assert.False(Accepted(feed.Handle(request, server, Now, Current)));
        feed.SetArmed(true);
        Assert.Null(feed.Handle(request, server, Now, static (_, _) => false));
        var receipt = feed.Handle(request, server, Now, Current);
        Assert.True(Accepted(receipt));
        Assert.Equal(server, receipt!.OriginDeviceId);
        Assert.Equal(request.SessionId, receipt.SessionId);
        Assert.Equal(request.Envelope.MessageId, receipt.CorrelationId);
        Assert.DoesNotContain("0x", receipt.Payload.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("0x120", feed.Get(Now, Current)?.MarkerAddressHex);
        feed.SetArmed(false);
        Assert.Null(feed.Get(Now, Current));
    }

    [Theory]
    [InlineData(-6, "0x120")]
    [InlineData(3, "0x120")]
    [InlineData(0, "0x121")]
    public void RejectsStaleFutureAndInconsistentSamples(int seconds, string marker)
    {
        var feed = new CompanionRuntimeFeed();
        feed.SetArmed(true);
        Assert.False(Accepted(feed.Handle(Request(Now.AddSeconds(seconds), marker), Guid.NewGuid(), Now, Current)));
        Assert.Null(feed.Get(Now, Current));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClearsOnExpiryOrSessionReplacement(bool expired)
    {
        var feed = new CompanionRuntimeFeed();
        feed.SetArmed(true);
        Assert.True(Accepted(feed.Handle(Request(Now), Guid.NewGuid(), Now, Current)));
        Assert.Null(feed.Get(Now.AddSeconds(expired ? 6 : 1), (_, _) => expired));
    }

    [Fact]
    public void ReplayAndAnotherCurrentPhoneCannotReplaceTheSample()
    {
        var feed = new CompanionRuntimeFeed();
        feed.SetArmed(true);
        var request = Request(Now);
        var server = Guid.NewGuid();
        Assert.True(Accepted(feed.Handle(request, server, Now, Current)));
        Assert.False(Accepted(feed.Handle(request, server, Now.AddSeconds(1), Current)));
        Assert.False(Accepted(feed.Handle(Request(Now.AddSeconds(1)), server, Now.AddSeconds(1), Current)));
        Assert.Equal(Now, feed.Get(Now.AddSeconds(1), Current)?.CapturedAt);
    }

    private static bool Accepted(ProtocolEnvelope? receipt) => receipt?.Payload.GetProperty("accepted").GetBoolean() == true;

    private static ReceivedDeviceEnvelope Request(DateTimeOffset capturedAt, string marker = "0x120")
    {
        var device = DeviceId.New();
        return new(device, new ProtocolEnvelope
        {
            Kind = "diagnostics.runtime.sample", ProtocolVersion = 1, MessageId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(), OriginDeviceId = device.Value, OriginSequence = 0, SentAt = capturedAt,
            RequiresAck = true, Payload = JsonSerializer.SerializeToElement(new
            {
                schemaVersion = 1, capturedAt, scope = "companion-own-marker-image", imageUUID = Guid.NewGuid(),
                cpuType = 0x100000c, cpuSubtype = 0, preferredTextAddress = "0x80", loadedHeaderAddress = "0x100",
                textVirtualSize = "0x200", slide = "0x80", markerRuntimeAddress = marker, markerImageOffset = "0x20",
            }),
        });
    }
}

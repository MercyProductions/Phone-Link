using PhoneLink.Integrations.AppleBluetooth.Ancs;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class AncsActionTests
{
    [Theory]
    [InlineData(GattCommunicationStatus.AccessDenied, false, false, true)]
    [InlineData(GattCommunicationStatus.Unreachable, false, false, false)]
    [InlineData(GattCommunicationStatus.Success, true, true, false)]
    public void ServiceDiscoveryDoesNotOverrideCharacteristicAccess(GattCommunicationStatus status, bool notifications, bool actions, bool denied)
    {
        var properties = new Dictionary<Guid, GattCharacteristicProperties>
        {
            [Guid.Parse("9fbf120d-6301-42d9-8c58-25e699a21dbd")] = GattCharacteristicProperties.Notify,
            [Guid.Parse("69d1d8f3-45e1-49a8-9821-9bbdfdaad9d9")] = GattCharacteristicProperties.Write,
            [Guid.Parse("22eac6e9-24d6-4bb5-be44-b36ace7c7bfb")] = GattCharacteristicProperties.Notify,
        };
        var support = AncsCharacteristicSupport.Evaluate(status, properties);
        Assert.Equal(notifications, support.Notifications);
        Assert.Equal(actions, support.Actions);
        Assert.Equal(denied, support.AccessDenied);
    }

    [Fact]
    public void MissingOptionalWritableControlPointCannotAdvertiseActionReadiness()
    {
        var properties = new Dictionary<Guid, GattCharacteristicProperties>
        {
            [Guid.Parse("9fbf120d-6301-42d9-8c58-25e699a21dbd")] = GattCharacteristicProperties.Notify,
            [Guid.Parse("69d1d8f3-45e1-49a8-9821-9bbdfdaad9d9")] = GattCharacteristicProperties.Read,
        };
        var support = AncsCharacteristicSupport.Evaluate(GattCommunicationStatus.Success, properties);
        Assert.True(support.Notifications);
        Assert.False(support.Actions);
    }

    private static AncsNotification Notification(uint uid = 0x12345678, byte flags = 24) =>
        new(uid, AncsCategory.Other, flags, "example.test", "Test notification", "", "", "", "Open", "Dismiss", DateTimeOffset.UtcNow);
    private static AncsNotificationEvent Event(uint uid, AncsEventKind kind = AncsEventKind.Added) => new(kind, 24, AncsCategory.Other, 1, uid);

    [Fact]
    public async Task ConcurrentConfirmationSubmissionsProduceOnlyOneWrite()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        var sender = new AncsActionSender(gate, _ => true, async (_, _) =>
        { writes++; await finished.Task; return new(true); }, CancellationToken.None);
        var notification = Notification();
        var first = sender.SendAsync(notification, AncsNotificationAction.Positive, () => true, CancellationToken.None);
        var duplicate = sender.SendAsync(notification, AncsNotificationAction.Negative, () => true, CancellationToken.None);
        finished.TrySetResult();
        var results = await Task.WhenAll(first, duplicate).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, writes);
        Assert.Single(results, result => result.Outcome == AncsActionOutcome.WriteAccepted);
        Assert.Single(results, result => result.Outcome == AncsActionOutcome.NotSent);
    }

    [Fact]
    public async Task MissingActionFlagCannotReachTransport()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var sender = new AncsActionSender(gate, _ => true, (_, _) => throw new InvalidOperationException("No write allowed"), CancellationToken.None);
        var result = await sender.SendAsync(Notification(flags: 0), AncsNotificationAction.Positive, () => true, CancellationToken.None);
        Assert.Equal(AncsActionOutcome.NotSent, result.Outcome);
        Assert.False(result.WriteAttempted);
    }

    [Fact]
    public async Task LostTransportAcknowledgmentLeavesOutcomeUnknown()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var sender = new AncsActionSender(gate, _ => true, (_, _) => Task.FromResult(new AncsActionWriteResult(false)), CancellationToken.None);
        Assert.Equal(AncsActionOutcome.OutcomeUnknown,
            (await sender.SendAsync(Notification(), AncsNotificationAction.Positive, () => true, CancellationToken.None)).Outcome);
    }

    [Theory]
    [InlineData(AncsNotificationAction.Positive, 0)]
    [InlineData(AncsNotificationAction.Negative, 1)]
    public void EncodesOnlyTheSixByteActionCommand(AncsNotificationAction action, byte value)
    {
        Assert.Equal(new byte[] { 2, 0x78, 0x56, 0x34, 0x12, value },
            AncsProtocolParser.CreatePerformNotificationActionCommand(0x12345678, action));
        Assert.Throws<ArgumentOutOfRangeException>(() => AncsProtocolParser.CreatePerformNotificationActionCommand(1, (AncsNotificationAction)2));
    }

    [Fact]
    public void ActionLabelsRequireBothFlagAndUsablePhoneSuppliedLabel()
    {
        Assert.Equal("Open", AncsActionSender.GetLabel(Notification(), AncsNotificationAction.Positive));
        Assert.Equal("Dismiss", AncsActionSender.GetLabel(Notification(), AncsNotificationAction.Negative));
        Assert.Empty(AncsActionSender.GetLabel(Notification(flags: 0), AncsNotificationAction.Positive));
        Assert.Empty(AncsActionSender.GetLabel(Notification() with { PositiveActionLabel = "" }, AncsNotificationAction.Positive));
        Assert.Empty(AncsActionSender.GetLabel(Notification() with { PositiveActionLabel = "Bad\nlabel" }, AncsNotificationAction.Positive));
        Assert.Empty(AncsActionSender.GetLabel(Notification() with { PositiveActionLabel = new string('x', 257) }, AncsNotificationAction.Positive));
        Assert.Empty(AncsActionSender.GetLabel(Notification(), (AncsNotificationAction)255));
    }

    [Fact]
    public void RemovedAndReusedUidCannotBeRevivedByAnOldAttributeReply()
    {
        var state = new AncsNotificationState();
        var originalEvent = Event(1);
        var original = Notification(1);
        state.Apply(originalEvent);
        Assert.True(state.TryPublish(originalEvent, original));
        state.Apply(Event(1, AncsEventKind.Removed));
        Assert.False(state.IsCurrent(original));
        Assert.False(state.TryPublish(originalEvent, original));
        var replacementEvent = Event(1);
        state.Apply(replacementEvent);
        Assert.False(state.TryPublish(originalEvent, original));
        Assert.True(state.TryPublish(replacementEvent, Notification(1)));
        Assert.Single(state.Snapshot());
    }

    [Fact]
    public void ModifiedEventInvalidatesOldLabelsEvenWhenRecordValuesMatch()
    {
        var state = new AncsNotificationState();
        var originalEvent = Event(1);
        var original = Notification(1);
        state.Apply(originalEvent);
        Assert.True(state.TryPublish(originalEvent, original));
        var modified = originalEvent with { };
        state.Apply(modified);
        Assert.False(state.IsCurrent(original));
        Assert.False(state.IsCurrentEvent(originalEvent));
        Assert.False(state.TryPublish(originalEvent, original));
        Assert.Empty(state.Snapshot());
        var replacement = original with { };
        Assert.True(state.TryPublish(modified, replacement));
        Assert.False(state.IsCurrent(original));
        Assert.True(state.IsCurrent(replacement));
    }

    [Fact]
    public void EndedSessionCannotAcceptQueuedEventsOrReplies()
    {
        var state = new AncsNotificationState();
        var notificationEvent = Event(1);
        state.Apply(notificationEvent);
        Assert.True(state.TryPublish(notificationEvent, Notification(1)));
        state.End();
        state.Apply(notificationEvent);
        Assert.False(state.TryPublish(notificationEvent, Notification(1)));
        Assert.False(state.IsCurrentEvent(notificationEvent));
        Assert.Empty(state.Snapshot());
    }

    [Fact]
    public async Task AcceptedWriteIsNotEffectVerificationAndCannotBeRepeatedOrReversedOnSameRevision()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var notification = Notification();
        var writes = 0;
        var sender = new AncsActionSender(gate, current => ReferenceEquals(current, notification), (command, _) =>
        {
            writes++;
            Assert.Equal(new byte[] { 2, 0x78, 0x56, 0x34, 0x12, 0 }, command);
            return Task.FromResult(new AncsActionWriteResult(true));
        }, CancellationToken.None);
        var result = await sender.SendAsync(notification, AncsNotificationAction.Positive, () => true, CancellationToken.None);
        Assert.Equal(AncsActionOutcome.WriteAccepted, result.Outcome);
        Assert.True(result.WriteAttempted);
        Assert.Equal(AncsActionOutcome.NotSent, (await sender.SendAsync(notification, AncsNotificationAction.Positive, () => true, CancellationToken.None)).Outcome);
        Assert.Equal(AncsActionOutcome.NotSent, (await sender.SendAsync(notification, AncsNotificationAction.Negative, () => true, CancellationToken.None)).Outcome);
        Assert.Equal(1, writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RechecksStalenessAndAuthorizationAfterWaitingForAttributeCommand(bool authorizationChanged)
    {
        using var gate = new SemaphoreSlim(0, 1);
        var current = true;
        var authorized = true;
        var writes = 0;
        var sender = new AncsActionSender(gate, _ => current, (_, _) =>
        { writes++; return Task.FromResult(new AncsActionWriteResult(true)); }, CancellationToken.None);
        var pending = sender.SendAsync(Notification(), AncsNotificationAction.Positive, () => authorized, CancellationToken.None);
        if (authorizationChanged) authorized = false; else current = false;
        gate.Release();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(AncsActionOutcome.NotSent, result.Outcome);
        Assert.False(result.WriteAttempted);
        Assert.Equal(0, writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerOrSessionCancellationWhileQueuedSendsNothing(bool cancelSession)
    {
        using var gate = new SemaphoreSlim(0, 1);
        using var caller = new CancellationTokenSource();
        using var lifetime = new CancellationTokenSource();
        var sender = new AncsActionSender(gate, _ => true, (_, _) => throw new InvalidOperationException("Must not write"), lifetime.Token);
        var pending = sender.SendAsync(Notification(), AncsNotificationAction.Positive, () => true, caller.Token);
        if (cancelSession) lifetime.Cancel(); else caller.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(AncsActionOutcome.NotSent, result.Outcome);
        Assert.False(result.WriteAttempted);
    }

    [Theory]
    [InlineData(0xA0)]
    [InlineData(0xA3)]
    public async Task ReportsProtocolRejectionWithoutClaimingAnActionEffect(byte error)
    {
        using var gate = new SemaphoreSlim(1, 1);
        var sender = new AncsActionSender(gate, _ => true, (_, _) => Task.FromResult(new AncsActionWriteResult(false, error)), CancellationToken.None);
        var result = await sender.SendAsync(Notification(), AncsNotificationAction.Positive, () => true, CancellationToken.None);
        Assert.Equal(AncsActionOutcome.DeviceRejected, result.Outcome);
        Assert.Equal(error, result.ProtocolError);
    }

    [Fact]
    public async Task CancellationAfterWriteStartsIsAmbiguousAndNeverRetried()
    {
        using var gate = new SemaphoreSlim(1, 1);
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        var sender = new AncsActionSender(gate, _ => true, async (_, token) =>
        {
            writes++;
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new(true);
        }, CancellationToken.None);
        var notification = Notification();
        var pending = sender.SendAsync(notification, AncsNotificationAction.Positive, () => true, caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        caller.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(AncsActionOutcome.OutcomeUnknown, result.Outcome);
        Assert.True(result.WriteAttempted);
        Assert.Equal(AncsActionOutcome.NotSent, (await sender.SendAsync(notification, AncsNotificationAction.Positive, () => true, CancellationToken.None)).Outcome);
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task TransportFailureCannotBeSilentlyResent()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var writes = 0;
        var sender = new AncsActionSender(gate, _ => true, (_, _) =>
        { writes++; throw new IOException("private notification details"); }, CancellationToken.None);
        var notification = Notification();
        var result = await sender.SendAsync(notification, AncsNotificationAction.Positive, () => true, CancellationToken.None);
        Assert.Equal(AncsActionOutcome.OutcomeUnknown, result.Outcome);
        Assert.DoesNotContain("private", result.ToString());
        await sender.SendAsync(notification, AncsNotificationAction.Positive, () => true, CancellationToken.None);
        Assert.Equal(1, writes);
    }
}

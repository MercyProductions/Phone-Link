using System.Text;
using PhoneLink.Integrations.AppleBluetooth.Media;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class AmsTests
{
    [Theory]
    [InlineData(GattCommunicationStatus.Success, GattCommunicationStatus.Success, 1)]
    [InlineData(GattCommunicationStatus.AccessDenied, GattCommunicationStatus.AccessDenied, 1)]
    [InlineData(GattCommunicationStatus.ProtocolError, GattCommunicationStatus.ProtocolError, 1)]
    [InlineData(GattCommunicationStatus.Unreachable, GattCommunicationStatus.Success, 2)]
    [InlineData(GattCommunicationStatus.Unreachable, GattCommunicationStatus.AccessDenied, 2)]
    [InlineData(GattCommunicationStatus.Unreachable, GattCommunicationStatus.Unreachable, 2)]
    public async Task CharacteristicRecoveryRetriesOnlyAnInitialUnreachable(
        GattCommunicationStatus first, GattCommunicationStatus last, int expectedReads)
    {
        var reads = 0;
        var result = await AmsSetupRecovery.ReadCharacteristicsAsync(
            _ => Task.FromResult(++reads == 1 ? first : last), status => status, CancellationToken.None);
        Assert.Equal(expectedReads, reads);
        Assert.Equal(last, result.Result);
        Assert.Equal(expectedReads == 2, result.Retried);
    }

    [Fact]
    public async Task CancellationPreventsPendingCharacteristicRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        var operation = AmsSetupRecovery.ReadCharacteristicsAsync(
            _ => { reads++; return Task.FromResult(GattCommunicationStatus.Unreachable); },
            status => status, cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void PlaybackShortcutsRequireAnExactCurrentAdvertisement()
    {
        var state = new AmsSessionState();
        Assert.False(state.CanSend(AmsRemoteCommand.Play));
        state.UpdateCommands([(byte)AmsRemoteCommand.TogglePlayPause, (byte)AmsRemoteCommand.NextTrack]);
        Assert.True(state.CanSend(AmsRemoteCommand.NextTrack));
        Assert.False(state.CanSend(AmsRemoteCommand.Play));
        Assert.False(state.CanSend(AmsRemoteCommand.Pause));
        state.Invalidate();
        Assert.False(state.CanSend(AmsRemoteCommand.NextTrack));
        state.UpdateCommands([(byte)AmsRemoteCommand.Play]);
        Assert.True(state.CanSend(AmsRemoteCommand.Play));
        state.End();
        state.UpdateCommands([(byte)AmsRemoteCommand.Play]);
        Assert.False(state.CanSend(AmsRemoteCommand.Play));
    }

    [Fact]
    public async Task EndedAndDisposedSessionLifetimeCannotSubmitANewCommand()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var lifetime = new CancellationTokenSource();
        var state = Ready();
        var sender = new AmsCommandSender(gate, state.IsCurrent, (_, _) => throw new IOException("Must not write"), lifetime.Token);
        lifetime.Cancel();
        lifetime.Dispose();
        var result = await sender.SendAsync(state.CreateRequest(AmsRemoteCommand.Play), () => true, CancellationToken.None);
        Assert.Equal(AmsCommandOutcome.NotSent, result.Outcome);
        Assert.False(result.WriteAttempted);
    }

    [Theory]
    [InlineData(AmsRemoteCommand.Play, 0)]
    [InlineData(AmsRemoteCommand.Pause, 1)]
    [InlineData(AmsRemoteCommand.TogglePlayPause, 2)]
    [InlineData(AmsRemoteCommand.NextTrack, 3)]
    [InlineData(AmsRemoteCommand.PreviousTrack, 4)]
    [InlineData(AmsRemoteCommand.VolumeUp, 5)]
    [InlineData(AmsRemoteCommand.VolumeDown, 6)]
    [InlineData(AmsRemoteCommand.AdvanceRepeatMode, 7)]
    [InlineData(AmsRemoteCommand.AdvanceShuffleMode, 8)]
    [InlineData(AmsRemoteCommand.SkipForward, 9)]
    [InlineData(AmsRemoteCommand.SkipBackward, 10)]
    [InlineData(AmsRemoteCommand.LikeTrack, 11)]
    [InlineData(AmsRemoteCommand.DislikeTrack, 12)]
    [InlineData(AmsRemoteCommand.BookmarkTrack, 13)]
    public void CommandWireValuesMatchDefinedAmsCommands(AmsRemoteCommand command, byte wireValue) =>
        Assert.Equal(new[] { wireValue }, AmsProtocol.CreateCommand(command));

    [Fact]
    public void SubscriptionRequestsAndUnknownCommandsAreBounded()
    {
        Assert.Equal(new byte[] { 0, 0, 1, 2 }, AmsProtocol.CreateSubscription(AmsEntity.Player));
        Assert.Equal(new byte[] { 1, 0, 1, 2, 3 }, AmsProtocol.CreateSubscription(AmsEntity.Queue));
        Assert.Equal(new byte[] { 2, 0, 1, 2, 3 }, AmsProtocol.CreateSubscription(AmsEntity.Track));
        Assert.Throws<ArgumentOutOfRangeException>(() => AmsProtocol.CreateSubscription((AmsEntity)3));
        Assert.Throws<ArgumentOutOfRangeException>(() => AmsProtocol.CreateCommand((AmsRemoteCommand)14));
        Assert.Equal(new[] { AmsRemoteCommand.Play, AmsRemoteCommand.VolumeUp }, AmsProtocol.ParseSupportedCommands([5, 255, 0, 5, 14]));
        Assert.Empty(AmsProtocol.ParseSupportedCommands([]));
        Assert.Throws<InvalidDataException>(() => AmsProtocol.ParseSupportedCommands(new byte[257]));
    }

    [Fact]
    public void EntityUpdatesRetainTruncationAndRejectMalformedOrUnboundedPayloads()
    {
        var update = Update(AmsEntity.Track, 2, "Test 🎵", flags: 1);
        Assert.Equal("Test 🎵", update.Value);
        Assert.True(update.IsTruncated);
        Assert.False(update.IsComplete);
        Assert.True(Update(AmsEntity.Player, 0, "Example player", flags: 2).HasUnknownFlags);
        byte[][] bad = [[], [0, 1], [3, 0, 0], [0, 3, 0], [1, 4, 0], [2, 2, 0, 255], new byte[4_100]];
        Assert.All(bad, payload => Assert.Throws<InvalidDataException>(() => AmsProtocol.ParseUpdate(payload, DateTimeOffset.UtcNow)));
    }

    [Theory]
    [InlineData("0", 0d)]
    [InlineData("1", 1d)]
    [InlineData("0.75", .75d)]
    [InlineData("-0.1", null)]
    [InlineData("1.1", null)]
    [InlineData("NaN", null)]
    [InlineData("Infinity", null)]
    [InlineData("1e999", null)]
    [InlineData("0,5", null)]
    public void VolumeRequiresCompleteFiniteInvariantRange(string text, double? expected)
    {
        Assert.Equal(expected, AmsProtocol.ParseVolume(Update(AmsEntity.Player, 2, text)));
        Assert.Null(AmsProtocol.ParseVolume(Update(AmsEntity.Player, 2, text, flags: 1)));
        Assert.Null(AmsProtocol.ParseVolume(Update(AmsEntity.Player, 2, text, flags: 2)));
    }

    [Theory]
    [InlineData("4,1,12")]
    [InlineData("1,NaN,12")]
    [InlineData("1,1,-1")]
    [InlineData("1,1,Infinity")]
    [InlineData("1,1")]
    [InlineData("1,1,12,extra")]
    public void InvalidPlaybackInfoIsNotInvented(string value) => Assert.Null(AmsProtocol.ParsePlayback(Update(AmsEntity.Player, 1, value)));

    [Fact]
    public void PlaybackValuesAreReceivedObservationsNotAComputedProgressClock()
    {
        Assert.Equal(new AmsPlaybackInfo(1, 1, 42.5), AmsProtocol.ParsePlayback(Update(AmsEntity.Player, 1, "1,1,42.5")));
        Assert.Equal(new AmsPlaybackInfo(2, -1, 30), AmsProtocol.ParsePlayback(Update(AmsEntity.Player, 1, "2,-1,30")));
        Assert.Null(AmsProtocol.ParsePlayback(Update(AmsEntity.Player, 1, "1,1,42.5", flags: 1)));
    }

    [Fact]
    public void NoInitialOrEmptyCommandListNeverImpliesAllCommandsAvailable()
    {
        var state = new AmsSessionState();
        Assert.False(state.Capture().CommandListReceived);
        Assert.Throws<InvalidOperationException>(() => state.CreateRequest(AmsRemoteCommand.Play));
        state.UpdateCommands([]);
        Assert.True(state.Capture().CommandListReceived);
        Assert.Throws<InvalidOperationException>(() => state.CreateRequest(AmsRemoteCommand.Play));
        state.UpdateCommands([0]);
        Assert.True(state.IsCurrent(state.CreateRequest(AmsRemoteCommand.Play)));
        Assert.Throws<InvalidOperationException>(() => state.CreateRequest(AmsRemoteCommand.NextTrack));
    }

    [Theory]
    [InlineData(AmsEntity.Player, 0)]
    [InlineData(AmsEntity.Track, 0)]
    [InlineData(AmsEntity.Track, 1)]
    [InlineData(AmsEntity.Track, 2)]
    [InlineData(AmsEntity.Track, 3)]
    [InlineData(AmsEntity.Queue, 0)]
    public void ChangedContextInvalidatesPendingConfirmation(AmsEntity entity, byte attribute)
    {
        var state = Ready();
        var request = state.CreateRequest(AmsRemoteCommand.Play);
        state.Update(Update(entity, attribute, "new context"));
        Assert.False(state.IsCurrent(request));
    }

    [Fact]
    public void RoutineElapsedAndVolumeUpdatesDoNotContinuouslyInvalidateControlRequests()
    {
        var state = Ready();
        var request = state.CreateRequest(AmsRemoteCommand.Play);
        state.Update(Update(AmsEntity.Player, 1, "1,1,43"));
        state.Update(Update(AmsEntity.Player, 2, "0.5"));
        state.UpdateCommands([0]);
        Assert.True(state.IsCurrent(request));
        var other = Ready();
        Assert.False(other.IsCurrent(request));
    }

    [Fact]
    public void EndOrInvalidationClearsPrivateObservationsAndCommandKnowledge()
    {
        var state = Ready();
        state.Update(Update(AmsEntity.Track, 2, "Own sample title"));
        var request = state.CreateRequest(AmsRemoteCommand.Play);
        state.Invalidate();
        Assert.Empty(state.Capture().Attributes);
        Assert.False(state.IsCurrent(request));
        state.UpdateCommands([0]);
        state.End();
        state.UpdateCommands([0]);
        state.Update(Update(AmsEntity.Track, 2, "Late title"));
        Assert.True(state.Capture().Ended);
        Assert.Empty(state.Capture().Attributes);
        Assert.Empty(state.Capture().Commands);
        Assert.Throws<InvalidOperationException>(() => state.CreateRequest(AmsRemoteCommand.Play));
    }

    [Theory]
    [InlineData(true, true, null, AmsCommandOutcome.WriteAccepted)]
    [InlineData(true, false, null, AmsCommandOutcome.SubmittedUnacknowledged)]
    [InlineData(false, true, (byte)0xA1, AmsCommandOutcome.DeviceRejected)]
    [InlineData(false, true, null, AmsCommandOutcome.OutcomeUnknown)]
    public async Task TransportReceiptsNeverClaimPlayerEffect(bool accepted, bool response, byte? error, AmsCommandOutcome expected)
    {
        using var gate = new SemaphoreSlim(1, 1);
        var state = Ready();
        var sender = new AmsCommandSender(gate, state.IsCurrent, (command, _) =>
        { Assert.Equal(new byte[] { 0 }, command); return Task.FromResult(new AmsWriteReceipt(accepted, response, error)); }, CancellationToken.None);
        var result = await sender.SendAsync(state.CreateRequest(AmsRemoteCommand.Play), () => true, CancellationToken.None);
        Assert.Equal(expected, result.Outcome);
        Assert.True(result.WriteAttempted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevalidatesSupportedSetAndAuthorizationAfterQueuedWait(bool authorizationChanged)
    {
        using var gate = new SemaphoreSlim(0, 1);
        var state = Ready();
        var authorized = true;
        var sender = new AmsCommandSender(gate, state.IsCurrent, (_, _) => throw new IOException("Must not write"), CancellationToken.None);
        var pending = sender.SendAsync(state.CreateRequest(AmsRemoteCommand.Play), () => authorized, CancellationToken.None);
        if (authorizationChanged) authorized = false; else state.UpdateCommands([1]);
        gate.Release();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(AmsCommandOutcome.NotSent, result.Outcome);
        Assert.False(result.WriteAttempted);
    }

    [Fact]
    public async Task ConcurrentDuplicateRequestIsSingleUseButNewUserRequestMayRepeat()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var state = Ready();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        var sender = new AmsCommandSender(gate, state.IsCurrent, async (_, _) =>
        { writes++; await release.Task; return new(true, true); }, CancellationToken.None);
        var request = state.CreateRequest(AmsRemoteCommand.Play);
        var first = sender.SendAsync(request, () => true, CancellationToken.None);
        var duplicate = sender.SendAsync(request, () => true, CancellationToken.None);
        release.TrySetResult();
        var results = await Task.WhenAll(first, duplicate).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, writes);
        Assert.Single(results, result => result.Outcome == AmsCommandOutcome.NotSent);
        await sender.SendAsync(state.CreateRequest(AmsRemoteCommand.Play), () => true, CancellationToken.None);
        Assert.Equal(2, writes);
    }

    [Fact]
    public async Task CancelledQueuedRequestWritesNothing()
    {
        using var gate = new SemaphoreSlim(0, 1);
        using var lifetime = new CancellationTokenSource();
        var state = Ready();
        var sender = new AmsCommandSender(gate, state.IsCurrent, (_, _) => throw new IOException("Must not write"), lifetime.Token);
        var pending = sender.SendAsync(state.CreateRequest(AmsRemoteCommand.Play), () => true, CancellationToken.None);
        lifetime.Cancel();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(AmsCommandOutcome.NotSent, result.Outcome);
        Assert.False(result.WriteAttempted);
    }

    [Fact]
    public async Task CancelledInFlightWriteIsAmbiguousAndSameRequestIsNeverResent()
    {
        using var gate = new SemaphoreSlim(1, 1);
        using var caller = new CancellationTokenSource();
        var state = Ready();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        var sender = new AmsCommandSender(gate, state.IsCurrent, async (_, token) =>
        { writes++; entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return new(true, true); }, CancellationToken.None);
        var request = state.CreateRequest(AmsRemoteCommand.Play);
        var pending = sender.SendAsync(request, () => true, caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        caller.Cancel();
        Assert.Equal(AmsCommandOutcome.OutcomeUnknown, (await pending.WaitAsync(TimeSpan.FromSeconds(3))).Outcome);
        Assert.Equal(AmsCommandOutcome.NotSent, (await sender.SendAsync(request, () => true, CancellationToken.None)).Outcome);
        Assert.Equal(1, writes);
    }

    [Fact]
    public void ExtendedReadRequiresTheSameTruncatedObservationAndCompatiblePrefix()
    {
        var state = Ready();
        var truncated = new AmsAttributeObservation(AmsEntity.Track, 2, 1, "Song", DateTimeOffset.UtcNow);
        state.Update(truncated);
        var revision = state.Capture().ContextRevision;
        Assert.True(state.IsExpandable(truncated, revision));
        Assert.False(state.TryExpand(truncated, revision, "Different track", DateTimeOffset.UtcNow));
        Assert.True(state.TryExpand(truncated, revision, "Song title", DateTimeOffset.UtcNow));
        var complete = Assert.Single(state.Capture().Attributes);
        Assert.True(complete.IsComplete);
        Assert.Equal("Song title", complete.Value);
        Assert.False(state.IsExpandable(truncated, revision));
    }

    [Fact]
    public void TrackContextAndUnknownFlagsPreventApplyingExtendedValue()
    {
        var state = Ready();
        var truncated = new AmsAttributeObservation(AmsEntity.Track, 2, 1, "Song", DateTimeOffset.UtcNow);
        state.Update(truncated);
        var revision = state.Capture().ContextRevision;
        state.Update(new(AmsEntity.Queue, 0, 0, "2", DateTimeOffset.UtcNow));
        Assert.False(state.TryExpand(truncated, revision, "Song title", DateTimeOffset.UtcNow));
        var unknown = truncated with { Flags = 3 };
        state.Update(unknown);
        Assert.False(state.IsExpandable(unknown, state.Capture().ContextRevision));
    }

    [Fact]
    public void ExtendedValueRejectsInvalidUtf8AndOversizedData()
    {
        Assert.Equal("Music 🎵", AmsProtocol.ParseExtendedValue("Music 🎵"u8));
        Assert.Throws<InvalidDataException>(() => AmsProtocol.ParseExtendedValue([0xff]));
        Assert.Throws<InvalidDataException>(() => AmsProtocol.ParseExtendedValue(new byte[4_097]));
    }

    private static AmsSessionState Ready() { var state = new AmsSessionState(); state.UpdateCommands([0]); return state; }
    private static AmsAttributeObservation Update(AmsEntity entity, byte attribute, string text, byte flags = 0) =>
        AmsProtocol.ParseUpdate([(byte)entity, attribute, flags, .. Encoding.UTF8.GetBytes(text)], DateTimeOffset.UtcNow);
}

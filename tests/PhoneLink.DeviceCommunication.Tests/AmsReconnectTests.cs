using PhoneLink.Integrations.AppleBluetooth.Media;
using Windows.Devices.Enumeration;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class AmsReconnectTests
{
    [Fact]
    public async Task OneReopenUsesTheOriginalSelectionAndWaitsForRelease()
    {
        var target = new AmsDeviceCandidate("fixture", "Fixture");
        var attempt = new AmsReconnectAttempt(target);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opens = 0;
        var fixture = new Fixture();
        var pending = Run();
        Assert.Equal(0, opens);
        release.SetResult();
        Assert.Same(fixture, await pending);
        Assert.Null(await Run());
        Assert.Equal(1, opens);

        Task<Fixture?> Run() => attempt.TryReconnectAsync(AmsSessionEndCause.BluetoothDisconnected, () => true,
            () => release.Task, (actual, _) =>
            {
                Assert.Same(target, actual);
                Assert.True(release.Task.IsCompletedSuccessfully);
                opens++;
                return Task.FromResult(fixture);
            }, TimeSpan.Zero, CancellationToken.None);
    }

    [Theory]
    [InlineData(AmsSessionEndCause.None, true)]
    [InlineData(AmsSessionEndCause.ServicesChanged, true)]
    [InlineData(AmsSessionEndCause.Stopped, true)]
    [InlineData(AmsSessionEndCause.BluetoothDisconnected, false)]
    public async Task UnknownOrIneligibleContextNeverReleasesOrReopens(AmsSessionEndCause cause, bool current)
    {
        var attempt = new AmsReconnectAttempt(new("fixture", "Fixture"));
        Assert.Null(await attempt.TryReconnectAsync<Fixture>(cause, () => current,
            () => throw new InvalidOperationException("Unexpected release"),
            (_, _) => throw new InvalidOperationException("Unexpected reopen"), TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public async Task FailedReleaseDoesNotReopenAndConsumesTheBudget()
    {
        var attempt = new AmsReconnectAttempt(new("fixture", "Fixture"));
        var releases = 0;
        Task<Fixture?> Run() => attempt.TryReconnectAsync<Fixture>(AmsSessionEndCause.BluetoothDisconnected, () => true,
            () => { releases++; throw new IOException("Fixture release failure"); },
            (_, _) => throw new InvalidOperationException("Unexpected reopen"), TimeSpan.Zero, CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(Run);
        Assert.Null(await Run());
        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task CancelledRecoveryDisposesLateReplacement()
    {
        var attempt = new AmsReconnectAttempt(new("fixture", "Fixture"));
        using var cancellation = new CancellationTokenSource();
        var open = new TaskCompletionSource<Fixture>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new Fixture();
        var pending = attempt.TryReconnectAsync(AmsSessionEndCause.BluetoothDisconnected, () => true,
            () => Task.CompletedTask, (_, _) => open.Task, TimeSpan.Zero, cancellation.Token);
        await cancellation.CancelAsync();
        open.SetResult(fixture);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(fixture.Released);
    }

    [Theory]
    [InlineData(DeviceAccessStatus.Allowed, true)]
    [InlineData(DeviceAccessStatus.Unspecified, false)]
    [InlineData(DeviceAccessStatus.DeniedBySystem, false)]
    [InlineData(DeviceAccessStatus.DeniedByUser, false)]
    public void OnlyExistingAllowedAccessCanProceed(DeviceAccessStatus status, bool allowed)
    {
        if (allowed) AmsReconnectAttempt.RequireExistingAccess(status);
        else Assert.Throws<InvalidOperationException>(() => AmsReconnectAttempt.RequireExistingAccess(status));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public bool Released { get; private set; }
        public ValueTask DisposeAsync() { Released = true; return ValueTask.CompletedTask; }
    }
}

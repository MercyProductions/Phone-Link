using PhoneLink.Application.Features.Diagnostics;

namespace PhoneLink.Application.Tests;

public sealed class DiscoveryRecoveryTests
{
    private static DiscoveryRecoverySnapshot Missing => new(false, 1, 2, false);

    [Fact]
    public void RetryBudgetPausesAfterThreeFailuresAndCanBeRearmed()
    {
        var budget = new DiscoveryRecoveryRetryBudget();
        budget.RecordFailure();
        budget.RecordFailure();
        Assert.True(budget.CanRetry);
        budget.RecordFailure();
        Assert.False(budget.CanRetry);
        budget.RecordFailure();
        Assert.False(budget.CanRetry);
        budget.Reset();
        Assert.True(budget.CanRetry);
    }

    [Theory]
    [InlineData(DiscoveryRecoveryOutcome.RecoveredLocally, true, true)]
    [InlineData(DiscoveryRecoveryOutcome.NoActionRequired, false, true)]
    [InlineData(DiscoveryRecoveryOutcome.NotApplicable, false, false)]
    [InlineData(DiscoveryRecoveryOutcome.Unverified, true, false)]
    public void OnlyVerifiedHealthyResultsRearmExhaustedBudget(DiscoveryRecoveryOutcome outcome, bool attempted, bool canRetry)
    {
        var budget = new DiscoveryRecoveryRetryBudget();
        Parallel.For(0, 100, _ => budget.RecordFailure());
        budget.Record(new(outcome, Missing, null, attempted, "fixture"));
        Assert.Equal(canRetry, budget.CanRetry);
    }

    [Fact]
    public async Task RestoresMissingRegistrationAndRequiresAfterState()
    {
        var state = Missing;
        var reads = 0;
        var actions = 0;
        var result = await DiscoveryRecovery.RunAsync(_ => { reads++; return Task.FromResult(state); },
            _ => { actions++; state = state with { AdvertisementRegistered = true }; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(2, reads);
        Assert.Equal(1, actions);
        Assert.Equal(DiscoveryRecoveryOutcome.RecoveredLocally, result.Outcome);
        Assert.False(result.Before.AdvertisementRegistered);
        Assert.True(result.After!.AdvertisementRegistered);
        Assert.True(result.ActionAttempted);
        Assert.Contains("phone-side connection check", result.Summary);
    }

    [Fact]
    public async Task DoesNotRestartHealthyAdvertisement()
    {
        var result = await DiscoveryRecovery.RunAsync(_ => Task.FromResult(Missing with { AdvertisementRegistered = true }),
            _ => throw new InvalidOperationException("Healthy service must not restart"), CancellationToken.None);
        Assert.Equal(DiscoveryRecoveryOutcome.NoActionRequired, result.Outcome);
        Assert.False(result.ActionAttempted);
    }

    [Theory]
    [InlineData(true, 1, 2)]
    [InlineData(false, 0, 2)]
    [InlineData(false, 1, 0)]
    public async Task MissingPrerequisitesNeverRunAction(bool locked, int trusted, int addresses)
    {
        var result = await DiscoveryRecovery.RunAsync(_ => Task.FromResult(new DiscoveryRecoverySnapshot(locked, trusted, addresses, false)),
            _ => throw new InvalidOperationException("Action prohibited"), CancellationToken.None);
        Assert.Equal(DiscoveryRecoveryOutcome.NotApplicable, result.Outcome);
        Assert.False(result.ActionAttempted);
        Assert.Null(result.After);
    }

    [Fact]
    public async Task ActionCompletionAloneIsNotSuccessfulRepair()
    {
        var result = await DiscoveryRecovery.RunAsync(_ => Task.FromResult(Missing), _ => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(DiscoveryRecoveryOutcome.Unverified, result.Outcome);
        Assert.True(result.ActionAttempted);
    }

    [Fact]
    public async Task FailedRegistrationDoesNotClaimRepairOrPublishExceptionText()
    {
        var result = await DiscoveryRecovery.RunAsync(_ => Task.FromResult(Missing),
            _ => throw new IOException("private name and address"), CancellationToken.None);
        Assert.Equal(DiscoveryRecoveryOutcome.Failed, result.Outcome);
        Assert.DoesNotContain("private name", result.ToString());
        Assert.NotNull(result.After);
    }

    [Theory]
    [InlineData(true, 1, 2)]
    [InlineData(false, 0, 2)]
    [InlineData(false, 1, 0)]
    public async Task ChangedPrerequisitesPreventSuccessClaim(bool locked, int trusted, int addresses)
    {
        var state = Missing;
        var result = await DiscoveryRecovery.RunAsync(_ => Task.FromResult(state), _ =>
        {
            state = new(locked, trusted, addresses, true);
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.Equal(DiscoveryRecoveryOutcome.Unverified, result.Outcome);
    }

    [Fact]
    public async Task UnavailableAfterProbeIsNotSuccess()
    {
        var reads = 0;
        var result = await DiscoveryRecovery.RunAsync(_ => ++reads == 1 ? Task.FromResult(Missing) : throw new IOException(),
            _ => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(DiscoveryRecoveryOutcome.Unverified, result.Outcome);
        Assert.Null(result.After);
    }

    [Fact]
    public async Task CancellationBeforeActionDoesNotPerformRecovery()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiscoveryRecovery.RunAsync(_ =>
        {
            cancellation.Cancel();
            return Task.FromResult(Missing);
        }, _ => throw new InvalidOperationException("No action after cancellation"), cancellation.Token));
    }

    [Fact]
    public async Task CancellationDuringRegistrationIsPropagated()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiscoveryRecovery.RunAsync(_ => Task.FromResult(Missing), token =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }, cancellation.Token));
    }
}

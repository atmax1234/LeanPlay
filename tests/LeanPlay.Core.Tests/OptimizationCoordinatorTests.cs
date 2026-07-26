using LeanPlay.Core.Domain;
using LeanPlay.Core.Engine;

namespace LeanPlay.Core.Tests;

public sealed class OptimizationCoordinatorTests
{
    private static readonly Guid Balanced = Guid.Parse(
        "381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid HighPerformance = Guid.Parse(
        "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    [Fact]
    public async Task NormalExitRestoresServiceAndPowerPlan()
    {
        var services = new FakeServiceController();
        services.Add("DiagTrack", ServiceObservedStatus.Running);
        var power = new FakePowerPlanController(Balanced);
        var journal = new InMemoryJournalStore();
        await using var coordinator = Create(services, power, journal);

        var activation = await coordinator.BeginSessionAsync(
            Profiles.Cs2(powerPlanGuid: HighPerformance.ToString("D")),
            4242);

        Assert.False(activation.AlreadyActive);
        Assert.Equal(ServiceObservedStatus.Stopped, services.StateOf("DiagTrack"));
        Assert.Equal(HighPerformance, power.ActiveScheme);
        Assert.Equal(RuntimePhase.Active, journal.Current?.Phase);
        Assert.Contains(
            journal.Saves,
            saved => saved.Mutations.Any(mutation =>
                mutation.Status == MutationStatus.IntentRecorded));

        var ended = await coordinator.EndSessionAsync(4242, 0, "wmi_process_stop");

        Assert.True(ended.SessionMatched);
        Assert.True(ended.Restored);
        Assert.True(ended.WasCleanExit);
        Assert.Equal(ServiceObservedStatus.Running, services.StateOf("DiagTrack"));
        Assert.Equal(Balanced, power.ActiveScheme);
        Assert.Null(journal.Current);
    }

    [Fact]
    public async Task NonzeroGameExitStillRestoresEverything()
    {
        var services = new FakeServiceController();
        services.Add("DiagTrack", ServiceObservedStatus.Running);
        var power = new FakePowerPlanController(Balanced);
        var journal = new InMemoryJournalStore();
        await using var coordinator = Create(services, power, journal);

        await coordinator.BeginSessionAsync(Profiles.Cs2(), 100);
        var ended = await coordinator.EndSessionAsync(100, -1, "game_crash");

        Assert.True(ended.SessionMatched);
        Assert.True(ended.Restored);
        Assert.False(ended.WasCleanExit);
        Assert.Equal(ServiceObservedStatus.Running, services.StateOf("DiagTrack"));
        Assert.Null(journal.Current);
    }

    [Fact]
    public async Task OptionalAccessDeniedIsReportedWithoutCrashingActivation()
    {
        var services = new FakeServiceController { ThrowBeforeStop = true };
        services.Add("DiagTrack", ServiceObservedStatus.Running);
        var journal = new InMemoryJournalStore();
        await using var coordinator = Create(
            services,
            new FakePowerPlanController(Balanced),
            journal);

        var activation = await coordinator.BeginSessionAsync(Profiles.Cs2(), 200);

        Assert.Single(activation.Warnings);
        Assert.Equal(ServiceObservedStatus.Running, services.StateOf("DiagTrack"));
        Assert.Equal(
            MutationStatus.ApplyFailed,
            Assert.Single(journal.Current!.Mutations).Status);

        var ended = await coordinator.EndSessionAsync(200, 0, "normal");
        Assert.True(ended.Restored);
        Assert.Equal(ServiceObservedStatus.Running, services.StateOf("DiagTrack"));
    }

    [Fact]
    public async Task FailedAcknowledgementAfterMutationIsRecoveredFromIntent()
    {
        var services = new FakeServiceController { ThrowAfterStop = true };
        services.Add("DiagTrack", ServiceObservedStatus.Running);
        var journal = new InMemoryJournalStore();
        await using (var firstCoordinator = Create(
                         services,
                         new FakePowerPlanController(Balanced),
                         journal))
        {
            var activation = await firstCoordinator.BeginSessionAsync(Profiles.Cs2(), 300);
            Assert.Single(activation.Warnings);
            Assert.Equal(ServiceObservedStatus.Stopped, services.StateOf("DiagTrack"));
            Assert.NotNull(journal.Current);
        }

        services.ThrowAfterStop = false;
        await using var recoveredCoordinator = Create(
            services,
            new FakePowerPlanController(Balanced),
            journal);
        var recovery = await recoveredCoordinator.RecoverIfRequiredAsync();

        Assert.True(recovery.Restored);
        Assert.Equal(ServiceObservedStatus.Running, services.StateOf("DiagTrack"));
        Assert.Null(journal.Current);
    }

    [Fact]
    public async Task RequiredMutationFailureRollsBackBeforeReturningError()
    {
        var services = new FakeServiceController { ThrowAfterStop = true };
        services.Add("DiagTrack", ServiceObservedStatus.Running);
        var journal = new InMemoryJournalStore();
        await using var coordinator = Create(
            services,
            new FakePowerPlanController(Balanced),
            journal);

        var exception = await Assert.ThrowsAsync<OptimizationActivationException>(
            () => coordinator.BeginSessionAsync(Profiles.Cs2(required: true), 400));

        Assert.Contains("captured state was restored", exception.Message);
        Assert.Equal(ServiceObservedStatus.Running, services.StateOf("DiagTrack"));
        Assert.Null(journal.Current);
    }

    [Fact]
    public async Task IncompleteRollbackRetainsJournalAndCanBeRetried()
    {
        var services = new FakeServiceController();
        services.Add("DiagTrack", ServiceObservedStatus.Running);
        var journal = new InMemoryJournalStore();
        await using var coordinator = Create(
            services,
            new FakePowerPlanController(Balanced),
            journal);

        await coordinator.BeginSessionAsync(Profiles.Cs2(), 500);
        services.RestoreFailuresRemaining = 1;

        var ended = await coordinator.EndSessionAsync(500, 9, "forced_stop");

        Assert.False(ended.Restored);
        Assert.Equal(RuntimePhase.RecoveryRequired, journal.Current?.Phase);
        Assert.Equal(ServiceObservedStatus.Stopped, services.StateOf("DiagTrack"));

        var recovery = await coordinator.RecoverIfRequiredAsync();
        Assert.True(recovery.Restored);
        Assert.Equal(ServiceObservedStatus.Running, services.StateOf("DiagTrack"));
        Assert.Null(journal.Current);
    }

    [Fact]
    public async Task DuplicateStartForSamePidIsIdempotent()
    {
        var services = new FakeServiceController();
        services.Add("DiagTrack", ServiceObservedStatus.Running);
        var journal = new InMemoryJournalStore();
        await using var coordinator = Create(
            services,
            new FakePowerPlanController(Balanced),
            journal);

        var first = await coordinator.BeginSessionAsync(Profiles.Cs2(), 600);
        var duplicate = await coordinator.BeginSessionAsync(Profiles.Cs2(), 600);

        Assert.Equal(first.SessionId, duplicate.SessionId);
        Assert.True(duplicate.AlreadyActive);
        Assert.Single(journal.Current!.Mutations);
    }

    [Fact]
    public async Task RunningDependentsCauseOptionalRuleToBeSkipped()
    {
        var services = new FakeServiceController();
        services.Add(
            "DiagTrack",
            ServiceObservedStatus.Running,
            "ExampleDependent");
        var journal = new InMemoryJournalStore();
        await using var coordinator = Create(
            services,
            new FakePowerPlanController(Balanced),
            journal);

        var activation = await coordinator.BeginSessionAsync(Profiles.Cs2(), 700);

        Assert.Single(activation.Warnings);
        Assert.Empty(journal.Current!.Mutations);
        Assert.Equal(ServiceObservedStatus.Running, services.StateOf("DiagTrack"));
        Assert.True((await coordinator.EndSessionAsync(700, 0, "normal")).Restored);
    }

    private static OptimizationCoordinator Create(
        FakeServiceController services,
        FakePowerPlanController power,
        InMemoryJournalStore journal) =>
        new(
            services,
            power,
            journal,
            new CollectingAuditSink(),
            new FakeClock());
}

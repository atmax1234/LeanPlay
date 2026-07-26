using LeanPlay.Core.Abstractions;
using LeanPlay.Core.Domain;

namespace LeanPlay.Core.Tests;

internal sealed class InMemoryJournalStore : IRecoveryJournalStore
{
    public RecoveryJournal? Current { get; private set; }

    public List<RecoveryJournal> Saves { get; } = new();

    public Task<RecoveryJournal?> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Current);
    }

    public Task SaveAsync(RecoveryJournal journal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Current = journal;
        Saves.Add(journal);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Current = null;
        return Task.CompletedTask;
    }
}

internal sealed class FakeServiceController : IServiceStateController
{
    private readonly Dictionary<string, ServiceObservedStatus> _states =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<string>> _dependents =
        new(StringComparer.OrdinalIgnoreCase);

    public bool ThrowBeforeStop { get; set; }

    public bool ThrowAfterStop { get; set; }

    public int RestoreFailuresRemaining { get; set; }

    public int RestoreCalls { get; private set; }

    public void Add(
        string serviceName,
        ServiceObservedStatus status,
        params string[] runningDependents)
    {
        _states[serviceName] = status;
        _dependents[serviceName] = runningDependents;
    }

    public ServiceObservedStatus StateOf(string serviceName) => _states[serviceName];

    public Task<ServiceSnapshot> CaptureAsync(
        string serviceName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_states.TryGetValue(serviceName, out var state))
        {
            throw new InvalidOperationException("Service not found.");
        }

        return Task.FromResult(
            new ServiceSnapshot(serviceName, state, _dependents[serviceName]));
    }

    public Task StopAsync(string serviceName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ThrowBeforeStop)
        {
            throw new UnauthorizedAccessException("Access denied.");
        }

        _states[serviceName] = ServiceObservedStatus.Stopped;
        if (ThrowAfterStop)
        {
            throw new TimeoutException("The status acknowledgement was lost.");
        }

        return Task.CompletedTask;
    }

    public Task RestoreAsync(
        ServiceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RestoreCalls++;
        if (RestoreFailuresRemaining > 0)
        {
            RestoreFailuresRemaining--;
            throw new UnauthorizedAccessException("Temporary restore failure.");
        }

        _states[snapshot.ServiceName] = snapshot.OriginalStatus;
        return Task.CompletedTask;
    }
}

internal sealed class FakePowerPlanController : IPowerPlanController
{
    public FakePowerPlanController(Guid initialScheme)
    {
        ActiveScheme = initialScheme;
    }

    public Guid ActiveScheme { get; private set; }

    public bool ThrowOnSet { get; set; }

    public int RestoreCalls { get; private set; }

    public Task<PowerPlanSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PowerPlanSnapshot(ActiveScheme));
    }

    public Task SetActiveAsync(Guid scheme, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ThrowOnSet)
        {
            throw new UnauthorizedAccessException("Power plan access denied.");
        }

        ActiveScheme = scheme;
        return Task.CompletedTask;
    }

    public Task RestoreAsync(
        PowerPlanSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RestoreCalls++;
        ActiveScheme = snapshot.ActiveScheme;
        return Task.CompletedTask;
    }
}

internal sealed class FakeClock : IClock
{
    private DateTimeOffset _now = new(2026, 7, 26, 0, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow
    {
        get
        {
            var value = _now;
            _now = _now.AddMilliseconds(1);
            return value;
        }
    }
}

internal sealed class CollectingAuditSink : IAuditSink
{
    public List<AuditRecord> Records { get; } = new();

    public ValueTask WriteAsync(
        AuditRecord record,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Records.Add(record);
        return ValueTask.CompletedTask;
    }
}

internal static class Profiles
{
    public static GameProfile Cs2(
        bool required = false,
        bool approved = true,
        string? powerPlanGuid = null,
        string serviceName = "DiagTrack") =>
        new(
            1,
            "Counter-Strike 2",
            "cs2.exe",
            powerPlanGuid,
            new[]
            {
                new ServiceRule(
                    serviceName,
                    ServiceDesiredState.Stop,
                    approved,
                    required)
            });
}

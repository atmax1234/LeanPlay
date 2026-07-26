using LeanPlay.Core.Abstractions;
using LeanPlay.Core.Domain;

namespace LeanPlay.Core.Engine;

public sealed class OptimizationCoordinator : IAsyncDisposable
{
    private readonly IServiceStateController _services;
    private readonly IPowerPlanController _power;
    private readonly IRecoveryJournalStore _journalStore;
    private readonly IAuditSink _audit;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OptimizationCoordinator(
        IServiceStateController services,
        IPowerPlanController power,
        IRecoveryJournalStore journalStore,
        IAuditSink? audit = null,
        IClock? clock = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _power = power ?? throw new ArgumentNullException(nameof(power));
        _journalStore = journalStore ?? throw new ArgumentNullException(nameof(journalStore));
        _audit = audit ?? new NullAuditSink();
        _clock = clock ?? new SystemClock();
    }

    public async Task<ActivationResult> BeginSessionAsync(
        GameProfile profile,
        int processId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);

        OptimizationPolicy.Validate(profile);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await _journalStore
                .LoadAsync(cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                if (existing.Session.ProcessId == processId &&
                    existing.Phase == RuntimePhase.Active)
                {
                    return new ActivationResult(
                        existing.Session.Id,
                        AlreadyActive: true,
                        Array.Empty<string>());
                }

                throw new OptimizationActivationException(
                    "A recovery journal already exists. Restore it before starting another session.");
            }

            var warnings = new List<string>();
            var serviceSnapshots = new List<ServiceSnapshot>();

            foreach (var rule in profile.ServiceRules.Where(
                         rule => rule.DesiredState == ServiceDesiredState.Stop))
            {
                try
                {
                    var snapshot = await _services
                        .CaptureAsync(rule.ServiceName, cancellationToken)
                        .ConfigureAwait(false);

                    if (snapshot.RunningDependentServices.Count > 0)
                    {
                        var message =
                            $"Service '{rule.ServiceName}' has running dependents: " +
                            string.Join(", ", snapshot.RunningDependentServices);

                        if (rule.Required)
                        {
                            throw new OptimizationActivationException(message);
                        }

                        warnings.Add(message);
                        await AuditAsync(
                            null,
                            "snapshot.service",
                            "skipped",
                            rule.ServiceName,
                            message,
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    serviceSnapshots.Add(snapshot);
                }
                catch (Exception exception) when (
                    !rule.Required && exception is not OperationCanceledException)
                {
                    var message =
                        $"Could not snapshot optional service '{rule.ServiceName}': " +
                        exception.Message;
                    warnings.Add(message);
                    await AuditAsync(
                        null,
                        "snapshot.service",
                        "failed",
                        rule.ServiceName,
                        exception.Message,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            PowerPlanSnapshot? powerSnapshot = null;
            if (profile.PowerPlanGuid is not null)
            {
                try
                {
                    powerSnapshot = await _power
                        .CaptureAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    warnings.Add($"Could not snapshot the active power plan: {exception.Message}");
                    await AuditAsync(
                        null,
                        "snapshot.power",
                        "failed",
                        null,
                        exception.Message,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            var now = _clock.UtcNow;
            var session = new RuntimeSession(
                Guid.NewGuid(),
                profile.Id,
                profile.GameName,
                profile.ExecutableName,
                processId,
                now);
            var journal = new RecoveryJournal(
                RecoveryJournal.CurrentFormatVersion,
                Guid.NewGuid(),
                session,
                RuntimePhase.SnapshotCaptured,
                now,
                new SystemSnapshot(powerSnapshot, serviceSnapshots),
                Array.Empty<MutationRecord>(),
                Array.Empty<JournalError>());

            await _journalStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);
            await AuditAsync(
                session.Id,
                "snapshot",
                "captured",
                null,
                $"Snapshot {journal.SnapshotId}",
                cancellationToken).ConfigureAwait(false);

            journal = journal.TransitionTo(RuntimePhase.Applying, _clock.UtcNow);
            await _journalStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);

            try
            {
                if (powerSnapshot is not null &&
                    Guid.TryParse(profile.PowerPlanGuid, out var desiredPowerPlan) &&
                    desiredPowerPlan != powerSnapshot.ActiveScheme)
                {
                    var result = await ApplyMutationAsync(
                        journal,
                        MutationKind.PowerPlan,
                        desiredPowerPlan.ToString("D"),
                        desiredPowerPlan.ToString("D"),
                        required: false,
                        token => _power.SetActiveAsync(desiredPowerPlan, token),
                        cancellationToken).ConfigureAwait(false);
                    journal = result.Journal;
                    if (result.Warning is not null)
                    {
                        warnings.Add(result.Warning);
                    }
                }

                foreach (var rule in profile.ServiceRules.Where(
                             rule => rule.DesiredState == ServiceDesiredState.Stop))
                {
                    var snapshot = serviceSnapshots.FirstOrDefault(item =>
                        string.Equals(
                            item.ServiceName,
                            rule.ServiceName,
                            StringComparison.OrdinalIgnoreCase));

                    if (snapshot is null ||
                        snapshot.OriginalStatus == ServiceObservedStatus.Stopped)
                    {
                        continue;
                    }

                    var result = await ApplyMutationAsync(
                        journal,
                        MutationKind.Service,
                        rule.ServiceName,
                        ServiceDesiredState.Stop.ToString(),
                        rule.Required,
                        token => _services.StopAsync(rule.ServiceName, token),
                        cancellationToken).ConfigureAwait(false);
                    journal = result.Journal;
                    if (result.Warning is not null)
                    {
                        warnings.Add(result.Warning);
                    }
                }

                journal = journal.TransitionTo(RuntimePhase.Active, _clock.UtcNow);
                await _journalStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);
                await AuditAsync(
                    session.Id,
                    "session",
                    "active",
                    profile.ExecutableName,
                    warnings.Count == 0 ? null : string.Join(" | ", warnings),
                    cancellationToken).ConfigureAwait(false);

                return new ActivationResult(session.Id, AlreadyActive: false, warnings);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await AuditAsync(
                    session.Id,
                    "session",
                    "activation_failed",
                    profile.ExecutableName,
                    exception.Message,
                    cancellationToken).ConfigureAwait(false);

                // ApplyMutationAsync may have durably recorded a failed intent before
                // throwing. Reload so immediate rollback uses the same source of truth
                // that startup recovery would use.
                var durableJournal = await _journalStore
                    .LoadAsync(cancellationToken)
                    .ConfigureAwait(false) ?? journal;
                var rollback = await RollbackCoreAsync(durableJournal, cancellationToken)
                    .ConfigureAwait(false);
                var suffix = rollback.Restored
                    ? "The captured state was restored."
                    : $"Rollback remains incomplete: {string.Join("; ", rollback.Errors)}";
                throw new OptimizationActivationException(
                    $"Profile activation failed. {suffix}",
                    exception);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SessionEndResult> EndSessionAsync(
        int processId,
        int? exitCode,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var journal = await _journalStore
                .LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (journal is null || journal.Session.ProcessId != processId)
            {
                return new SessionEndResult(
                    SessionMatched: false,
                    Restored: journal is null,
                    WasCleanExit: false,
                    Array.Empty<string>());
            }

            var cleanExit = exitCode == 0;
            journal = journal.EndSession(
                _clock.UtcNow,
                exitCode,
                cleanExit,
                string.IsNullOrWhiteSpace(reason) ? "process_exit" : reason);
            await _journalStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);

            var result = await RollbackCoreAsync(journal, cancellationToken)
                .ConfigureAwait(false);
            return new SessionEndResult(
                SessionMatched: true,
                result.Restored,
                cleanExit,
                result.Errors);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RecoveryResult> RecoverIfRequiredAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var journal = await _journalStore
                .LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (journal is null)
            {
                return RecoveryResult.NothingToDo;
            }

            await AuditAsync(
                journal.Session.Id,
                "recovery",
                "started",
                null,
                $"Recovered journal in phase {journal.Phase}.",
                cancellationToken).ConfigureAwait(false);

            return await RollbackCoreAsync(journal, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<RecoveryResult> ShutdownAsync(
        CancellationToken cancellationToken = default) =>
        RecoverIfRequiredAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<(RecoveryJournal Journal, string? Warning)> ApplyMutationAsync(
        RecoveryJournal journal,
        MutationKind kind,
        string target,
        string requestedValue,
        bool required,
        Func<CancellationToken, Task> apply,
        CancellationToken cancellationToken)
    {
        var mutation = new MutationRecord(
            Guid.NewGuid(),
            kind,
            target,
            requestedValue,
            MutationStatus.IntentRecorded,
            required,
            _clock.UtcNow);
        journal = journal.AddMutation(mutation, _clock.UtcNow);

        // Write-ahead intent: this flush must complete before the Windows mutation.
        await _journalStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);

        try
        {
            await apply(cancellationToken).ConfigureAwait(false);
            journal = journal.UpdateMutation(
                mutation.Id,
                MutationStatus.Applied,
                _clock.UtcNow);
            await _journalStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);
            await AuditAsync(
                journal.Session.Id,
                $"apply.{kind.ToString().ToLowerInvariant()}",
                "applied",
                target,
                requestedValue,
                cancellationToken).ConfigureAwait(false);
            return (journal, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            journal = journal
                .UpdateMutation(
                    mutation.Id,
                    MutationStatus.ApplyFailed,
                    _clock.UtcNow,
                    exception.Message)
                .AddError(
                    new JournalError(
                        _clock.UtcNow,
                        $"apply.{kind.ToString().ToLowerInvariant()}",
                        target,
                        exception.Message),
                    _clock.UtcNow);
            await _journalStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);
            await AuditAsync(
                journal.Session.Id,
                $"apply.{kind.ToString().ToLowerInvariant()}",
                "failed",
                target,
                exception.Message,
                cancellationToken).ConfigureAwait(false);

            if (required)
            {
                throw;
            }

            return (
                journal,
                $"Optional {kind.ToString().ToLowerInvariant()} mutation for '{target}' " +
                $"failed: {exception.Message}");
        }
    }

    private async Task<RecoveryResult> RollbackCoreAsync(
        RecoveryJournal journal,
        CancellationToken cancellationToken)
    {
        if (journal.Phase != RuntimePhase.RollbackPending)
        {
            journal = journal.TransitionTo(RuntimePhase.RollbackPending, _clock.UtcNow);
            await _journalStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);
        }

        var errors = new List<string>();
        for (var index = journal.Mutations.Count - 1; index >= 0; index--)
        {
            var mutation = journal.Mutations[index];
            if (mutation.Status == MutationStatus.Restored)
            {
                continue;
            }

            try
            {
                switch (mutation.Kind)
                {
                    case MutationKind.Service:
                        await _services
                            .RestoreAsync(
                                journal.Snapshot.GetService(mutation.Target),
                                cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case MutationKind.PowerPlan:
                        if (journal.Snapshot.PowerPlan is not null)
                        {
                            await _power
                                .RestoreAsync(journal.Snapshot.PowerPlan, cancellationToken)
                                .ConfigureAwait(false);
                        }

                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unknown mutation kind {mutation.Kind}.");
                }

                journal = journal.UpdateMutation(
                    mutation.Id,
                    MutationStatus.Restored,
                    _clock.UtcNow);
                await _journalStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);
                await AuditAsync(
                    journal.Session.Id,
                    $"restore.{mutation.Kind.ToString().ToLowerInvariant()}",
                    "restored",
                    mutation.Target,
                    null,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var message = $"Could not restore '{mutation.Target}': {exception.Message}";
                errors.Add(message);
                journal = journal
                    .UpdateMutation(
                        mutation.Id,
                        MutationStatus.RestoreFailed,
                        _clock.UtcNow,
                        exception.Message)
                    .AddError(
                        new JournalError(
                            _clock.UtcNow,
                            $"restore.{mutation.Kind.ToString().ToLowerInvariant()}",
                            mutation.Target,
                            exception.Message),
                        _clock.UtcNow);
                await _journalStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);
                await AuditAsync(
                    journal.Session.Id,
                    $"restore.{mutation.Kind.ToString().ToLowerInvariant()}",
                    "failed",
                    mutation.Target,
                    exception.Message,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (errors.Count > 0)
        {
            journal = journal.TransitionTo(RuntimePhase.RecoveryRequired, _clock.UtcNow);
            await _journalStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);
            return new RecoveryResult(
                JournalFound: true,
                Restored: false,
                journal.Session.Id,
                errors);
        }

        await AuditAsync(
            journal.Session.Id,
            "recovery",
            "complete",
            null,
            null,
            cancellationToken).ConfigureAwait(false);
        await _journalStore.DeleteAsync(cancellationToken).ConfigureAwait(false);
        return new RecoveryResult(
            JournalFound: true,
            Restored: true,
            journal.Session.Id,
            Array.Empty<string>());
    }

    private async ValueTask AuditAsync(
        Guid? sessionId,
        string eventType,
        string outcome,
        string? target,
        string? details,
        CancellationToken cancellationToken)
    {
        try
        {
            await _audit.WriteAsync(
                new AuditRecord(
                    _clock.UtcNow,
                    sessionId,
                    eventType,
                    outcome,
                    target,
                    details),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Reporting must never prevent state restoration.
        }
    }
}

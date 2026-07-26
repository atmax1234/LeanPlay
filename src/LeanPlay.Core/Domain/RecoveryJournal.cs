namespace LeanPlay.Core.Domain;

public enum RuntimePhase
{
    SnapshotCaptured = 1,
    Applying = 2,
    Active = 3,
    RollbackPending = 4,
    RecoveryRequired = 5
}

public enum MutationKind
{
    PowerPlan = 1,
    Service = 2
}

public enum MutationStatus
{
    IntentRecorded = 1,
    Applied = 2,
    ApplyFailed = 3,
    Restored = 4,
    RestoreFailed = 5
}

public sealed record RuntimeSession(
    Guid Id,
    long? ProfileId,
    string GameName,
    string ExecutableName,
    int ProcessId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt = null,
    int? ExitCode = null,
    bool? WasCleanExit = null,
    string? StopReason = null);

public sealed record MutationRecord(
    Guid Id,
    MutationKind Kind,
    string Target,
    string RequestedValue,
    MutationStatus Status,
    bool Required,
    DateTimeOffset UpdatedAt,
    string? Error = null);

public sealed record JournalError(
    DateTimeOffset Timestamp,
    string Operation,
    string? Target,
    string Message);

public sealed record RecoveryJournal(
    int FormatVersion,
    Guid SnapshotId,
    RuntimeSession Session,
    RuntimePhase Phase,
    DateTimeOffset UpdatedAt,
    SystemSnapshot Snapshot,
    IReadOnlyList<MutationRecord> Mutations,
    IReadOnlyList<JournalError> Errors)
{
    public const int CurrentFormatVersion = 1;

    public RecoveryJournal TransitionTo(RuntimePhase next, DateTimeOffset now)
    {
        if (!CanTransition(Phase, next))
        {
            throw new InvalidOperationException(
                $"Invalid runtime transition from {Phase} to {next}.");
        }

        return this with { Phase = next, UpdatedAt = now };
    }

    public RecoveryJournal AddMutation(MutationRecord mutation, DateTimeOffset now) =>
        this with
        {
            Mutations = Mutations.Append(mutation).ToArray(),
            UpdatedAt = now
        };

    public RecoveryJournal UpdateMutation(
        Guid mutationId,
        MutationStatus status,
        DateTimeOffset now,
        string? error = null)
    {
        if (!Mutations.Any(mutation => mutation.Id == mutationId))
        {
            throw new InvalidOperationException($"Unknown mutation {mutationId}.");
        }

        return this with
        {
            Mutations = Mutations
                .Select(mutation => mutation.Id == mutationId
                    ? mutation with { Status = status, UpdatedAt = now, Error = error }
                    : mutation)
                .ToArray(),
            UpdatedAt = now
        };
    }

    public RecoveryJournal AddError(JournalError error, DateTimeOffset now) =>
        this with
        {
            Errors = Errors.Append(error).ToArray(),
            UpdatedAt = now
        };

    public RecoveryJournal EndSession(
        DateTimeOffset endedAt,
        int? exitCode,
        bool wasCleanExit,
        string reason) =>
        this with
        {
            Session = Session with
            {
                EndedAt = endedAt,
                ExitCode = exitCode,
                WasCleanExit = wasCleanExit,
                StopReason = reason
            },
            UpdatedAt = endedAt
        };

    private static bool CanTransition(RuntimePhase current, RuntimePhase next)
    {
        if (current == next)
        {
            return true;
        }

        if (next == RuntimePhase.RollbackPending)
        {
            return current is RuntimePhase.SnapshotCaptured
                or RuntimePhase.Applying
                or RuntimePhase.Active
                or RuntimePhase.RecoveryRequired;
        }

        return (current, next) switch
        {
            (RuntimePhase.SnapshotCaptured, RuntimePhase.Applying) => true,
            (RuntimePhase.Applying, RuntimePhase.Active) => true,
            (RuntimePhase.RollbackPending, RuntimePhase.RecoveryRequired) => true,
            (RuntimePhase.RecoveryRequired, RuntimePhase.RecoveryRequired) => true,
            _ => false
        };
    }
}

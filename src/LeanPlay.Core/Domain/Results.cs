namespace LeanPlay.Core.Domain;

public sealed record ActivationResult(
    Guid SessionId,
    bool AlreadyActive,
    IReadOnlyList<string> Warnings);

public sealed record RecoveryResult(
    bool JournalFound,
    bool Restored,
    Guid? SessionId,
    IReadOnlyList<string> Errors)
{
    public static RecoveryResult NothingToDo { get; } =
        new(false, true, null, Array.Empty<string>());
}

public sealed record SessionEndResult(
    bool SessionMatched,
    bool Restored,
    bool WasCleanExit,
    IReadOnlyList<string> Errors);

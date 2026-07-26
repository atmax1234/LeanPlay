using LeanPlay.Core.Domain;

namespace LeanPlay.Core.Abstractions;

public interface IRecoveryJournalStore
{
    Task<RecoveryJournal?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(RecoveryJournal journal, CancellationToken cancellationToken);

    Task DeleteAsync(CancellationToken cancellationToken);
}

using LeanPlay.Core.Domain;
using LeanPlay.Core.Persistence;

namespace LeanPlay.Core.Tests;

public sealed class RecoveryJournalTests
{
    [Fact]
    public void InvalidForwardTransitionIsRejected()
    {
        var journal = CreateJournal(RuntimePhase.SnapshotCaptured);

        var exception = Assert.Throws<InvalidOperationException>(
            () => journal.TransitionTo(
                RuntimePhase.Active,
                DateTimeOffset.UtcNow));

        Assert.Contains("Invalid runtime transition", exception.Message);
    }

    [Fact]
    public async Task FileStoreFallsBackToPreviousAtomicBackup()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"leanplay-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "recovery-journal.json");

        try
        {
            var store = new FileRecoveryJournalStore(path);
            var first = CreateJournal(RuntimePhase.SnapshotCaptured);
            await store.SaveAsync(first, CancellationToken.None);
            var second = first.TransitionTo(
                RuntimePhase.Applying,
                DateTimeOffset.UtcNow);
            await store.SaveAsync(second, CancellationToken.None);

            await File.WriteAllTextAsync(path, "{ definitely not valid json");
            var loaded = await store.LoadAsync(CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal(RuntimePhase.SnapshotCaptured, loaded.Phase);

            await store.DeleteAsync(CancellationToken.None);
            Assert.False(File.Exists(path));
            Assert.False(File.Exists($"{path}.bak"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static RecoveryJournal CreateJournal(RuntimePhase phase)
    {
        var now = DateTimeOffset.UtcNow;
        return new RecoveryJournal(
            RecoveryJournal.CurrentFormatVersion,
            Guid.NewGuid(),
            new RuntimeSession(
                Guid.NewGuid(),
                1,
                "Counter-Strike 2",
                "cs2.exe",
                42,
                now),
            phase,
            now,
            new SystemSnapshot(
                new PowerPlanSnapshot(Guid.NewGuid()),
                Array.Empty<ServiceSnapshot>()),
            Array.Empty<MutationRecord>(),
            Array.Empty<JournalError>());
    }
}

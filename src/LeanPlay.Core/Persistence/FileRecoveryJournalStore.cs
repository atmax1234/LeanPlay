using System.Text.Json;
using System.Text.Json.Serialization;
using LeanPlay.Core.Abstractions;
using LeanPlay.Core.Domain;

namespace LeanPlay.Core.Persistence;

public sealed class FileRecoveryJournalStore : IRecoveryJournalStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _path;
    private readonly string _backupPath;

    public FileRecoveryJournalStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _backupPath = $"{_path}.bak";
    }

    public async Task<RecoveryJournal?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            return await LoadFileAsync(_path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception primaryException) when (
            primaryException is JsonException or IOException or InvalidDataException)
        {
            if (!File.Exists(_backupPath))
            {
                throw new InvalidDataException(
                    $"Recovery journal '{_path}' cannot be read and no backup exists.",
                    primaryException);
            }

            return await LoadFileAsync(_backupPath, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SaveAsync(
        RecoveryJournal journal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        Validate(journal);

        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Recovery journal has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    journal,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
            {
                File.Replace(temporaryPath, _path, _backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, _path);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public Task DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        if (File.Exists(_backupPath))
        {
            File.Delete(_backupPath);
        }

        return Task.CompletedTask;
    }

    private static async Task<RecoveryJournal> LoadFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var journal = await JsonSerializer.DeserializeAsync<RecoveryJournal>(
            stream,
            SerializerOptions,
            cancellationToken).ConfigureAwait(false);

        if (journal is null)
        {
            throw new InvalidDataException($"Recovery journal '{path}' is empty.");
        }

        Validate(journal);
        return journal;
    }

    private static void Validate(RecoveryJournal journal)
    {
        if (journal.FormatVersion != RecoveryJournal.CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"Unsupported recovery journal format {journal.FormatVersion}.");
        }

        if (journal.Session.Id == Guid.Empty || journal.SnapshotId == Guid.Empty)
        {
            throw new InvalidDataException("Recovery journal identifiers are invalid.");
        }
    }
}

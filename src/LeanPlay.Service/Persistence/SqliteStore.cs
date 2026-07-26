using LeanPlay.Core.Abstractions;
using LeanPlay.Core.Domain;
using LeanPlay.Service.Configuration;
using Microsoft.Data.Sqlite;

namespace LeanPlay.Service.Persistence;

public sealed class SqliteStore : IAuditSink, IAsyncDisposable
{
    private readonly RuntimePaths _paths;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private bool _initialized;

    public SqliteStore(RuntimePaths paths)
    {
        _paths = paths;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(_paths.DataDirectory);
            var schema = await File.ReadAllTextAsync(_paths.SchemaPath, cancellationToken)
                .ConfigureAwait(false);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = schema;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async ValueTask WriteAsync(
        AuditRecord record,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO optimization_actions
                (session_id, timestamp, action_kind, target, outcome, details)
            VALUES
                (
                    CASE
                        WHEN $session_id IS NOT NULL
                         AND EXISTS (
                            SELECT 1 FROM performance_sessions WHERE id = $session_id
                         )
                        THEN $session_id
                        ELSE NULL
                    END,
                    $timestamp,
                    $action_kind,
                    $target,
                    $outcome,
                    $details
                );
            """;
        command.Parameters.AddWithValue(
            "$session_id",
            record.SessionId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$timestamp", record.Timestamp.ToString("O"));
        command.Parameters.AddWithValue("$action_kind", record.EventType);
        command.Parameters.AddWithValue("$target", record.Target ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$outcome", record.Outcome);
        command.Parameters.AddWithValue("$details", record.Details ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordSessionStartedAsync(
        Guid sessionId,
        GameProfile profile,
        int processId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR IGNORE INTO performance_sessions
                (
                    id,
                    profile_id,
                    game_name,
                    executable_name,
                    game_process_id,
                    start_time,
                    activation_state
                )
            VALUES
                (
                    $id,
                    $profile_id,
                    $game_name,
                    $executable_name,
                    $process_id,
                    $start_time,
                    'ACTIVE'
                );
            """;
        command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$profile_id", profile.Id ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$game_name", profile.GameName);
        command.Parameters.AddWithValue("$executable_name", profile.ExecutableName);
        command.Parameters.AddWithValue("$process_id", processId);
        command.Parameters.AddWithValue("$start_time", startedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordSessionEndedAsync(
        Guid sessionId,
        DateTimeOffset endedAt,
        int? exitCode,
        bool cleanExit,
        bool recoveryComplete,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE performance_sessions
               SET end_time = $end_time,
                   exit_code = $exit_code,
                   was_clean_exit = $was_clean_exit,
                   activation_state = $activation_state,
                   recovery_was_required = $recovery_was_required
             WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$end_time", endedAt.ToString("O"));
        command.Parameters.AddWithValue("$exit_code", exitCode ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$was_clean_exit", cleanExit ? 1 : 0);
        command.Parameters.AddWithValue(
            "$activation_state",
            recoveryComplete ? "RESTORED" : "RECOVERY_REQUIRED");
        command.Parameters.AddWithValue("$recovery_was_required", recoveryComplete ? 0 : 1);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _initializationGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        };
        return new SqliteConnection(builder.ToString());
    }
}

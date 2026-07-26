using System.Diagnostics;
using System.Management;
using System.Threading.Channels;
using LeanPlay.Core.Domain;
using LeanPlay.Core.Engine;
using LeanPlay.Service.Configuration;
using LeanPlay.Service.Persistence;
using LeanPlay.Service.Runtime;
using Microsoft.Extensions.Options;

namespace LeanPlay.Service.Monitoring;

public sealed partial class GameDetectionWorker : BackgroundService
{
    private readonly OptimizationCoordinator _coordinator;
    private readonly ProfileCatalog _profiles;
    private readonly SqliteStore _database;
    private readonly ILogger<GameDetectionWorker> _logger;
    private readonly TimeSpan _reconciliationInterval;
    private readonly TimeSpan _recoveryRetryInterval;
    private readonly Dictionary<int, GameProfile> _observed = new();
    private int? _activeSessionProcessId;
    private Guid? _activeSessionId;
    private string? _activeExecutable;
    private int? _primaryExitCode;

    public GameDetectionWorker(
        OptimizationCoordinator coordinator,
        ProfileCatalog profiles,
        SqliteStore database,
        IOptions<LeanPlayOptions> options,
        ILogger<GameDetectionWorker> logger)
    {
        _coordinator = coordinator;
        _profiles = profiles;
        _database = database;
        _logger = logger;
        _reconciliationInterval = TimeSpan.FromSeconds(
            Math.Clamp(options.Value.ReconciliationIntervalSeconds, 2, 60));
        _recoveryRetryInterval = TimeSpan.FromSeconds(
            Math.Clamp(options.Value.RecoveryRetrySeconds, 2, 300));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverBeforeMonitoringAsync(stoppingToken).ConfigureAwait(false);
        try
        {
            await _database.InitializeAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogDatabaseFailure(
                _logger,
                exception,
                "initialize reporting storage");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunWatcherGenerationAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogWatcherFailure(_logger, exception);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken)
                    .ConfigureAwait(false);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var recovery = await _coordinator
                .ShutdownAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!recovery.Restored && _logger.IsEnabled(LogLevel.Critical))
            {
                var errors = string.Join("; ", recovery.Errors);
                LogShutdownRecoveryIncomplete(
                    _logger,
                    errors);
            }
        }
        catch (Exception exception)
        {
            LogShutdownRecoveryFailure(_logger, exception);
        }
    }

    private async Task RecoverBeforeMonitoringAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var recovery = await _coordinator
                    .RecoverIfRequiredAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (recovery.Restored)
                {
                    if (recovery.JournalFound)
                    {
                        LogStartupRecoveryComplete(_logger, recovery.SessionId);
                    }

                    return;
                }

                if (_logger.IsEnabled(LogLevel.Critical))
                {
                    var errors = string.Join("; ", recovery.Errors);
                    LogStartupRecoveryIncomplete(
                        _logger,
                        errors);
                }
            }
            catch (Exception exception)
            {
                LogStartupRecoveryFailure(_logger, exception);
            }

            await Task.Delay(_recoveryRetryInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunWatcherGenerationAsync(CancellationToken cancellationToken)
    {
        var events = Channel.CreateUnbounded<ProcessTrace>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

        using var startWatcher = new ManagementEventWatcher(
            new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
        using var stopWatcher = new ManagementEventWatcher(
            new WqlEventQuery("SELECT * FROM Win32_ProcessStopTrace"));

        EventArrivedEventHandler startHandler = (_, eventArgs) =>
            PublishTrace(events.Writer, eventArgs.NewEvent, started: true);
        EventArrivedEventHandler stopHandler = (_, eventArgs) =>
            PublishTrace(events.Writer, eventArgs.NewEvent, started: false);
        StoppedEventHandler stoppedHandler = (_, eventArgs) =>
            events.Writer.TryComplete(
                new ManagementException(
                    $"WMI watcher stopped with status {eventArgs.Status}."));

        startWatcher.EventArrived += startHandler;
        stopWatcher.EventArrived += stopHandler;
        startWatcher.Stopped += stoppedHandler;
        stopWatcher.Stopped += stoppedHandler;

        try
        {
            // Subscribe first, then enumerate. PID deduplication closes the race.
            startWatcher.Start();
            stopWatcher.Start();
            await ReconcileProcessesAsync(cancellationToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(_reconciliationInterval);
            var timerTask = timer.WaitForNextTickAsync(cancellationToken).AsTask();
            var eventTask = events.Reader.WaitToReadAsync(cancellationToken).AsTask();

            while (!cancellationToken.IsCancellationRequested)
            {
                var completed = await Task.WhenAny(timerTask, eventTask).ConfigureAwait(false);
                if (completed == eventTask)
                {
                    if (!await eventTask.ConfigureAwait(false))
                    {
                        break;
                    }

                    while (events.Reader.TryRead(out var trace))
                    {
                        await HandleTraceAsync(trace, cancellationToken).ConfigureAwait(false);
                    }

                    eventTask = events.Reader.WaitToReadAsync(cancellationToken).AsTask();
                }
                else
                {
                    if (!await timerTask.ConfigureAwait(false))
                    {
                        break;
                    }

                    await ReconcileProcessesAsync(cancellationToken).ConfigureAwait(false);
                    timerTask = timer.WaitForNextTickAsync(cancellationToken).AsTask();
                }
            }
        }
        finally
        {
            startWatcher.EventArrived -= startHandler;
            stopWatcher.EventArrived -= stopHandler;
            startWatcher.Stopped -= stoppedHandler;
            stopWatcher.Stopped -= stoppedHandler;
            TryStop(startWatcher);
            TryStop(stopWatcher);
            events.Writer.TryComplete();
        }
    }

    private async Task HandleTraceAsync(
        ProcessTrace trace,
        CancellationToken cancellationToken)
    {
        if (!_profiles.TryGet(trace.ExecutableName, out var profile))
        {
            return;
        }

        if (trace.Started)
        {
            if (!_observed.TryAdd(trace.ProcessId, profile))
            {
                return;
            }

            LogGameStart(
                _logger,
                profile.GameName,
                profile.ExecutableName,
                trace.ProcessId);
            await TryActivateAsync(profile, trace.ProcessId, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!_observed.Remove(trace.ProcessId, out var stoppedProfile))
        {
            return;
        }

        LogGameExit(
            _logger,
            stoppedProfile.GameName,
            trace.ProcessId,
            trace.ExitCode);

        if (_activeSessionProcessId == trace.ProcessId)
        {
            _primaryExitCode = trace.ExitCode;
        }

        if (_activeExecutable is null ||
            !string.Equals(
                stoppedProfile.NormalizedExecutableName,
                _activeExecutable,
                StringComparison.OrdinalIgnoreCase) ||
            _observed.Values.Any(profile =>
                string.Equals(
                    profile.NormalizedExecutableName,
                    _activeExecutable,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var restored = await EndActiveSessionAsync(
            _primaryExitCode ?? trace.ExitCode,
            trace.ObservedByReconciliation ? "process_reconciliation" : "wmi_process_stop",
            cancellationToken).ConfigureAwait(false);
        if (!restored)
        {
            await RecoverBeforeMonitoringAsync(cancellationToken).ConfigureAwait(false);
        }

        var waiting = _observed.FirstOrDefault();
        if (waiting.Value is not null)
        {
            await TryActivateAsync(waiting.Value, waiting.Key, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task TryActivateAsync(
        GameProfile profile,
        int processId,
        CancellationToken cancellationToken)
    {
        if (_activeSessionProcessId is not null)
        {
            return;
        }

        ActivationResult activation;
        try
        {
            activation = await _coordinator
                .BeginSessionAsync(profile, processId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogActivationFailure(
                _logger,
                exception,
                profile.GameName,
                processId);
            return;
        }

        var startedAt = DateTimeOffset.UtcNow;
        _activeSessionProcessId = processId;
        _activeSessionId = activation.SessionId;
        _activeExecutable = profile.NormalizedExecutableName;
        _primaryExitCode = null;

        try
        {
            await _database.RecordSessionStartedAsync(
                activation.SessionId,
                profile,
                processId,
                startedAt,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogDatabaseFailure(
                _logger,
                exception,
                "record session start");
        }

        if (activation.Warnings.Count > 0)
        {
            LogActivationWarnings(
                _logger,
                activation.SessionId,
                string.Join("; ", activation.Warnings));
        }
    }

    private async Task<bool> EndActiveSessionAsync(
        int? exitCode,
        string reason,
        CancellationToken cancellationToken)
    {
        if (_activeSessionProcessId is not int processId)
        {
            return true;
        }

        var sessionId = _activeSessionId;
        var cleanExit = exitCode == 0;
        var restored = false;
        IReadOnlyList<string> recoveryErrors = Array.Empty<string>();
        try
        {
            var ended = await _coordinator
                .EndSessionAsync(processId, exitCode, reason, cancellationToken)
                .ConfigureAwait(false);
            cleanExit = ended.WasCleanExit;
            restored = ended.Restored;
            recoveryErrors = ended.Errors;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRuntimeRecoveryFailure(_logger, exception, sessionId);
            try
            {
                var recovery = await _coordinator
                    .RecoverIfRequiredAsync(cancellationToken)
                    .ConfigureAwait(false);
                restored = recovery.Restored;
                recoveryErrors = recovery.Errors;
            }
            catch (Exception recoveryException) when (
                recoveryException is not OperationCanceledException)
            {
                LogRuntimeRecoveryFailure(_logger, recoveryException, sessionId);
                recoveryErrors = new[] { recoveryException.Message };
            }
        }

        try
        {
            if (sessionId is Guid id)
            {
                await _database.RecordSessionEndedAsync(
                    id,
                    DateTimeOffset.UtcNow,
                    exitCode,
                    cleanExit,
                    restored,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogDatabaseFailure(
                _logger,
                exception,
                "record session end");
        }

        _activeSessionProcessId = null;
        _activeSessionId = null;
        _activeExecutable = null;
        _primaryExitCode = null;

        if (!restored && _logger.IsEnabled(LogLevel.Critical))
        {
            var errors = string.Join("; ", recoveryErrors);
            LogSessionRecoveryIncomplete(_logger, sessionId, errors);
        }

        return restored;
    }

    private async Task ReconcileProcessesAsync(CancellationToken cancellationToken)
    {
        var live = new Dictionary<int, GameProfile>();
        foreach (var profile in _profiles.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processName = Path.GetFileNameWithoutExtension(profile.ExecutableName);
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    live[process.Id] = profile;
                }
            }
        }

        foreach (var process in live)
        {
            if (!_observed.ContainsKey(process.Key))
            {
                await HandleTraceAsync(
                    new ProcessTrace(
                        Started: true,
                        process.Value.ExecutableName,
                        process.Key,
                        ExitCode: null,
                        ObservedByReconciliation: true),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        var disappeared = _observed
            .Where(process => !live.ContainsKey(process.Key))
            .Select(process => (process.Key, process.Value.ExecutableName))
            .ToArray();

        foreach (var process in disappeared)
        {
            await HandleTraceAsync(
                new ProcessTrace(
                    Started: false,
                    process.ExecutableName,
                    process.Key,
                    ExitCode: null,
                    ObservedByReconciliation: true),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static void PublishTrace(
        ChannelWriter<ProcessTrace> writer,
        ManagementBaseObject managementEvent,
        bool started)
    {
        try
        {
            var executableName = Convert.ToString(
                managementEvent.Properties["ProcessName"]?.Value,
                System.Globalization.CultureInfo.InvariantCulture);
            var processIdValue = managementEvent.Properties["ProcessID"]?.Value;
            if (string.IsNullOrWhiteSpace(executableName) || processIdValue is null)
            {
                return;
            }

            int? exitCode = null;
            var exitStatusValue = managementEvent.Properties["ExitStatus"]?.Value;
            if (!started && exitStatusValue is not null)
            {
                exitCode = unchecked((int)Convert.ToUInt32(
                    exitStatusValue,
                    System.Globalization.CultureInfo.InvariantCulture));
            }

            writer.TryWrite(
                new ProcessTrace(
                    started,
                    executableName,
                    Convert.ToInt32(
                        processIdValue,
                        System.Globalization.CultureInfo.InvariantCulture),
                    exitCode,
                    ObservedByReconciliation: false));
        }
        catch (Exception exception) when (
            exception is FormatException or InvalidCastException or OverflowException)
        {
            // A malformed WMI event is ignored; reconciliation remains the safety net.
        }
    }

    private static void TryStop(ManagementEventWatcher watcher)
    {
        try
        {
            watcher.Stop();
        }
        catch (ManagementException)
        {
            // The watcher is already stopped or its provider disappeared.
        }
    }

    private sealed record ProcessTrace(
        bool Started,
        string ExecutableName,
        int ProcessId,
        int? ExitCode,
        bool ObservedByReconciliation);

    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Error,
        Message = "The WMI game watcher failed; retrying in one second.")]
    private static partial void LogWatcherFailure(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Critical,
        Message = "LeanPlay stopped with incomplete recovery: {Errors}")]
    private static partial void LogShutdownRecoveryIncomplete(
        ILogger logger,
        string errors);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Critical,
        Message = "LeanPlay could not complete shutdown recovery. The durable journal was retained.")]
    private static partial void LogShutdownRecoveryFailure(
        ILogger logger,
        Exception exception);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Warning,
        Message = "Recovered snapshot for session {SessionId} before monitoring.")]
    private static partial void LogStartupRecoveryComplete(
        ILogger logger,
        Guid? sessionId);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Critical,
        Message = "Recovery is incomplete; game optimization remains disabled. {Errors}")]
    private static partial void LogStartupRecoveryIncomplete(
        ILogger logger,
        string errors);

    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Critical,
        Message = "Recovery failed; game optimization remains disabled.")]
    private static partial void LogStartupRecoveryFailure(
        ILogger logger,
        Exception exception);

    [LoggerMessage(
        EventId = 1006,
        Level = LogLevel.Information,
        Message = "Detected {Game} start: {Executable} PID {ProcessId}.")]
    private static partial void LogGameStart(
        ILogger logger,
        string game,
        string executable,
        int processId);

    [LoggerMessage(
        EventId = 1007,
        Level = LogLevel.Information,
        Message = "Detected {Game} exit: PID {ProcessId}, exit code {ExitCode}.")]
    private static partial void LogGameExit(
        ILogger logger,
        string game,
        int processId,
        int? exitCode);

    [LoggerMessage(
        EventId = 1008,
        Level = LogLevel.Warning,
        Message = "Session {SessionId} activated with warnings: {Warnings}")]
    private static partial void LogActivationWarnings(
        ILogger logger,
        Guid sessionId,
        string warnings);

    [LoggerMessage(
        EventId = 1009,
        Level = LogLevel.Error,
        Message = "Could not activate profile {Profile} for PID {ProcessId}.")]
    private static partial void LogActivationFailure(
        ILogger logger,
        Exception exception,
        string profile,
        int processId);

    [LoggerMessage(
        EventId = 1010,
        Level = LogLevel.Critical,
        Message = "Session {SessionId} ended but recovery is incomplete: {Errors}")]
    private static partial void LogSessionRecoveryIncomplete(
        ILogger logger,
        Guid? sessionId,
        string errors);

    [LoggerMessage(
        EventId = 1011,
        Level = LogLevel.Error,
        Message = "Could not {Operation}; runtime safety processing continues.")]
    private static partial void LogDatabaseFailure(
        ILogger logger,
        Exception exception,
        string operation);

    [LoggerMessage(
        EventId = 1012,
        Level = LogLevel.Critical,
        Message = "Runtime recovery failed for session {SessionId}; the journal was retained.")]
    private static partial void LogRuntimeRecoveryFailure(
        ILogger logger,
        Exception exception,
        Guid? sessionId);
}

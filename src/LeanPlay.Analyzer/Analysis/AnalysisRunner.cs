using LeanPlay.Analyzer.Collectors;
using LeanPlay.Analyzer.Model;
using LeanPlay.Analyzer.Reporting;

namespace LeanPlay.Analyzer.Analysis;

public sealed class AnalysisRunner
{
    private readonly Action<double>? _progress;

    public AnalysisRunner(Action<double>? progress = null)
    {
        _progress = progress;
    }

    public async Task<AnalysisRunResult> RunAsync(
        AnalysisOptions options,
        CancellationToken cancellationToken)
    {
        var notices = new List<CollectorNotice>();
        var startedAt = DateTimeOffset.Now;
        var inventory = await new InventoryCollector(notices)
            .CollectAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var etw = new EtwCollector(options.IncludeEtw, notices);
        await etw.StartAsync(cancellationToken).ConfigureAwait(false);

        PerformanceCollectionResult performance;
        NetworkSummary network;
        try
        {
            var performanceTask = CollectPerformanceSafelyAsync(
                options,
                etw,
                notices,
                cancellationToken);
            var networkTask = CollectNetworkSafelyAsync(
                inventory,
                options,
                notices,
                cancellationToken);
            await Task.WhenAll(performanceTask, networkTask).ConfigureAwait(false);
            performance = await performanceTask.ConfigureAwait(false);
            network = await networkTask.ConfigureAwait(false);
        }
        finally
        {
            await etw.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var etwTotals = etw.GetProcessTotals();
        var processes = performance.Processes.Select(process =>
        {
            if (!etwTotals.TryGetValue(process.ProcessId, out var totals))
            {
                return process;
            }

            return process with
            {
                EtwDiskReadBytes = totals.DiskReadBytes,
                EtwDiskWriteBytes = totals.DiskWriteBytes,
                EtwNetworkSendBytes = totals.NetworkSendBytes,
                EtwNetworkReceiveBytes = totals.NetworkReceiveBytes
            };
        }).ToArray();
        var etwSummary = etw.BuildSummary();
        var findings = FindingAnalyzer.Analyze(
            inventory,
            performance.Samples,
            processes,
            network,
            etwSummary);

        var report = new AnalysisReport(
            "1.0",
            Guid.NewGuid(),
            startedAt,
            DateTimeOffset.Now,
            new AnalysisOptionsSnapshot(
                options.Duration.TotalSeconds,
                options.SampleInterval.TotalSeconds,
                options.IncludeEtw,
                options.WorkloadLabel),
            inventory,
            performance.Samples,
            processes,
            network,
            etwSummary,
            findings,
            notices
                .DistinctBy(notice => (notice.Collector, notice.Level, notice.Message))
                .ToArray());
        var paths = await ReportWriter
            .WriteAsync(report, options.OutputDirectory, cancellationToken)
            .ConfigureAwait(false);
        return new AnalysisRunResult(report, paths);
    }

    private async Task<PerformanceCollectionResult> CollectPerformanceSafelyAsync(
        AnalysisOptions options,
        EtwCollector etw,
        List<CollectorNotice> notices,
        CancellationToken cancellationToken)
    {
        try
        {
            return await new PerformanceCollector(notices)
                .CollectAsync(
                    options.Duration,
                    options.SampleInterval,
                    _progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            notices.Add(
                new CollectorNotice(
                    "Performance sampling",
                    "error",
                    exception.Message));
            return new PerformanceCollectionResult(
                Array.Empty<PerformanceSample>(),
                Array.Empty<ProcessSummary>());
        }
    }

    private static async Task<NetworkSummary> CollectNetworkSafelyAsync(
        MachineInventory inventory,
        AnalysisOptions options,
        List<CollectorNotice> notices,
        CancellationToken cancellationToken)
    {
        try
        {
            return await new NetworkCollector(notices)
                .CollectAsync(
                    inventory,
                    options.PublicPingTargets,
                    options.Duration,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            notices.Add(
                new CollectorNotice("Network sampling", "error", exception.Message));
            return new NetworkSummary(
                Array.Empty<PingTargetSummary>(),
                0,
                0,
                0,
                0,
                null);
        }
    }
}

public sealed record AnalysisRunResult(
    AnalysisReport Report,
    ReportPaths Paths);

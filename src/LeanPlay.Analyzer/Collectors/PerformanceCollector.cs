using System.Diagnostics;
using LeanPlay.Analyzer.Model;

namespace LeanPlay.Analyzer.Collectors;

public sealed class PerformanceCollector
{
    private readonly List<CollectorNotice> _notices;

    public PerformanceCollector(List<CollectorNotice> notices)
    {
        _notices = notices;
    }

    public async Task<PerformanceCollectionResult> CollectAsync(
        TimeSpan duration,
        TimeSpan interval,
        Action<double>? progress,
        CancellationToken cancellationToken)
    {
        using var pdh = CreateQuery();
        var processSampler = new ProcessSampler();
        var nativeSampler = new NativeSystemSampler();
        var samples = new List<PerformanceSample>();
        var stopwatch = Stopwatch.StartNew();

        _ = pdh.Collect();
        processSampler.Sample(interval);

        var previousSampleAt = stopwatch.Elapsed;
        while (stopwatch.Elapsed < duration)
        {
            var remaining = duration - stopwatch.Elapsed;
            var delay = remaining < interval ? remaining : interval;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var now = stopwatch.Elapsed;
            var sampleElapsed = now - previousSampleAt;
            previousSampleAt = now;

            _ = pdh.Collect();
            processSampler.Sample(sampleElapsed);
            var native = nativeSampler.Read(sampleElapsed);

            var committedBytes = pdh.Read("memory.committedBytes");
            var commitLimit = pdh.Read("memory.commitLimit");
            double? committedPercent =
                committedBytes is double committed &&
                commitLimit is double limit &&
                limit > 0
                    ? committed / limit * 100
                    : null;

            var networkBytes = pdh.ReadArray("network.bytes")
                .Where(value =>
                    !value.Instance.Contains("Loopback", StringComparison.OrdinalIgnoreCase) &&
                    !value.Instance.Contains("isatap", StringComparison.OrdinalIgnoreCase))
                .Sum(value => value.Value);
            var gpuBusy = pdh.ReadArray("gpu.busy")
                .Where(value => double.IsFinite(value.Value))
                .Select(value => value.Value)
                .DefaultIfEmpty()
                .Max();

            samples.Add(
                new PerformanceSample(
                    DateTimeOffset.UtcNow,
                    now.TotalSeconds,
                    CleanPercentage(pdh.Read("cpu.total")) ??
                    native.CpuTotalPercent,
                    CleanPercentage(pdh.Read("cpu.privileged")) ??
                    native.CpuPrivilegedPercent,
                    CleanPercentage(pdh.Read("cpu.dpc")),
                    CleanPercentage(pdh.Read("cpu.interrupt")),
                    NonNegative(pdh.Read("cpu.performance")),
                    NonNegative(pdh.Read("cpu.queue")),
                    NonNegative(pdh.Read("memory.availableMb")) ??
                    native.AvailableMemoryMb,
                    CleanPercentage(committedPercent) ??
                    native.CommittedMemoryPercent,
                    NonNegative(pdh.Read("memory.pagesInput")),
                    NonNegative(pdh.Read("disk.active")),
                    NonNegative(pdh.Read("disk.queue")),
                    NonNegative(pdh.Read("disk.bytes")),
                    SecondsToMilliseconds(pdh.Read("disk.readLatency")),
                    SecondsToMilliseconds(pdh.Read("disk.writeLatency")),
                    networkBytes > 0 ? networkBytes : native.NetworkBytesPerSecond,
                    gpuBusy > 0 ? Math.Clamp(gpuBusy, 0, 100) : 0));

            progress?.Invoke(Math.Clamp(now.TotalSeconds / duration.TotalSeconds, 0, 1));
        }

        var connections = TcpConnectionCollector.CountByProcess();
        var udpEndpoints = TcpConnectionCollector.CountUdpByProcess();
        return new PerformanceCollectionResult(
            samples,
            processSampler.Build(
                new Dictionary<int, EtwProcessTotals>(),
                connections,
                udpEndpoints));
    }

    private PdhQuery CreateQuery()
    {
        var query = new PdhQuery(_notices);
        query.Add("cpu.total", @"\Processor(_Total)\% Processor Time");
        query.Add("cpu.privileged", @"\Processor(_Total)\% Privileged Time");
        query.Add("cpu.dpc", @"\Processor(_Total)\% DPC Time");
        query.Add("cpu.interrupt", @"\Processor(_Total)\% Interrupt Time");
        query.Add(
            "cpu.performance",
            @"\Processor Information(_Total)\% Processor Performance");
        query.Add("cpu.queue", @"\System\Processor Queue Length");
        query.Add("memory.availableMb", @"\Memory\Available MBytes");
        query.Add("memory.committedBytes", @"\Memory\Committed Bytes");
        query.Add("memory.commitLimit", @"\Memory\Commit Limit");
        query.Add("memory.pagesInput", @"\Memory\Pages Input/sec");
        query.Add("disk.active", @"\PhysicalDisk(_Total)\% Disk Time");
        query.Add("disk.queue", @"\PhysicalDisk(_Total)\Avg. Disk Queue Length");
        query.Add("disk.bytes", @"\PhysicalDisk(_Total)\Disk Bytes/sec");
        query.Add("disk.readLatency", @"\PhysicalDisk(_Total)\Avg. Disk sec/Read");
        query.Add("disk.writeLatency", @"\PhysicalDisk(_Total)\Avg. Disk sec/Write");
        query.Add("network.bytes", @"\Network Interface(*)\Bytes Total/sec");
        query.Add("gpu.busy", @"\GPU Engine(*)\Utilization Percentage");
        return query;
    }

    private static double? CleanPercentage(double? value) =>
        value is double number && double.IsFinite(number)
            ? Math.Clamp(number, 0, 100)
            : null;

    private static double? NonNegative(double? value) =>
        value is double number && double.IsFinite(number)
            ? Math.Max(0, number)
            : null;

    private static double? SecondsToMilliseconds(double? value) =>
        value is double number && double.IsFinite(number)
            ? Math.Max(0, number * 1000)
            : null;
}

public sealed record PerformanceCollectionResult(
    IReadOnlyList<PerformanceSample> Samples,
    IReadOnlyList<ProcessSummary> Processes);

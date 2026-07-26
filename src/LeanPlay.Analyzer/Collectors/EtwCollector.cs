using System.Security.Principal;
using LeanPlay.Analyzer.Model;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace LeanPlay.Analyzer.Collectors;

public sealed class EtwCollector : IAsyncDisposable
{
    private readonly bool _requested;
    private readonly List<CollectorNotice> _notices;
    private readonly Dictionary<int, MutableProcessTotals> _processTotals = new();
    private readonly Dictionary<(ulong Routine, bool IsDpc), MutableRoutineTotals>
        _routineTotals = new();
    private readonly List<ImageRange> _images = new();
    private TraceEventSession? _session;
    private Task? _processingTask;
    private string? _unavailableReason;
    private long _diskReadBytes;
    private long _diskWriteBytes;
    private long _networkSendBytes;
    private long _networkReceiveBytes;
    private long _dpcCount;
    private long _isrCount;
    private double? _maximumDpcMicroseconds;
    private double? _maximumIsrMicroseconds;

    public EtwCollector(bool requested, List<CollectorNotice> notices)
    {
        _requested = requested;
        _notices = notices;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_requested)
        {
            _unavailableReason = "ETW collection was disabled by command-line option.";
            return Task.CompletedTask;
        }

        if (!IsAdministrator())
        {
            _unavailableReason =
                "Kernel ETW requires an elevated analyzer. The standard-user fallback " +
                "still collected PDH, process, network, and event-log evidence.";
            _notices.Add(
                new CollectorNotice("Kernel ETW", "information", _unavailableReason));
            return Task.CompletedTask;
        }

        try
        {
            _session = new TraceEventSession(
                $"LeanPlayAnalyzer-{Environment.ProcessId}-{Guid.NewGuid():N}")
            {
                StopOnDispose = true
            };

            var kernel = _session.Source.Kernel;
            kernel.DiskIORead += data => AddDisk(data.ProcessID, data.TransferSize, read: true);
            kernel.DiskIOWrite += data => AddDisk(data.ProcessID, data.TransferSize, read: false);
            kernel.TcpIpSend += data => AddNetwork(data.ProcessID, data.size, send: true);
            kernel.TcpIpRecv += data => AddNetwork(data.ProcessID, data.size, send: false);
            kernel.TcpIpSendIPV6 += data => AddNetwork(data.ProcessID, data.size, send: true);
            kernel.TcpIpRecvIPV6 += data => AddNetwork(data.ProcessID, data.size, send: false);
            kernel.PerfInfoDPC += AddDpc;
            kernel.PerfInfoThreadedDPC += AddDpc;
            kernel.PerfInfoTimerDPC += AddDpc;
            kernel.PerfInfoISR += AddIsr;
            kernel.ImageLoad += AddImage;
            kernel.ImageDCStart += AddImage;

            var keywords =
                KernelTraceEventParser.Keywords.Process |
                KernelTraceEventParser.Keywords.ImageLoad |
                KernelTraceEventParser.Keywords.DiskIO |
                KernelTraceEventParser.Keywords.DiskFileIO |
                KernelTraceEventParser.Keywords.NetworkTCPIP |
                KernelTraceEventParser.Keywords.Interrupt;
            _session.EnableKernelProvider(keywords);
            _processingTask = Task.Run(
                () => _session.Source.Process(),
                CancellationToken.None);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
                or InvalidOperationException
                or System.Runtime.InteropServices.COMException)
        {
            _unavailableReason = exception.Message;
            _notices.Add(
                new CollectorNotice(
                    "Kernel ETW",
                    "warning",
                    $"Kernel ETW could not start: {exception.Message}"));
            _session?.Dispose();
            _session = null;
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            _session.Stop();
            if (_processingTask is not null)
            {
                await _processingTask
                    .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (
            exception is TimeoutException
                or InvalidOperationException
                or OperationCanceledException)
        {
            _notices.Add(
                new CollectorNotice(
                    "Kernel ETW",
                    "warning",
                    $"Kernel ETW shutdown was incomplete: {exception.Message}"));
        }
        finally
        {
            _session.Dispose();
            _session = null;
        }
    }

    public IReadOnlyDictionary<int, EtwProcessTotals> GetProcessTotals() =>
        _processTotals.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToImmutable());

    public EtwSummary BuildSummary()
    {
        var ranges = _images
            .Where(image => image.Size > 0 && !string.IsNullOrWhiteSpace(image.FileName))
            .OrderBy(image => image.Base)
            .ToArray();
        var drivers = _routineTotals
            .Select(pair =>
            {
                var driver = ResolveImage(pair.Key.Routine, ranges);
                return new
                {
                    Driver = driver,
                    pair.Key.IsDpc,
                    Totals = pair.Value
                };
            })
            .GroupBy(item => item.Driver, StringComparer.OrdinalIgnoreCase)
            .Select(group => new DriverLatencySummary(
                group.Key,
                group.Where(item => item.IsDpc).Sum(item => item.Totals.Count),
                group.Where(item => !item.IsDpc).Sum(item => item.Totals.Count),
                group.Sum(item => item.Totals.TotalMicroseconds),
                group.Max(item => item.Totals.MaximumMicroseconds)))
            .OrderByDescending(driver => driver.TotalExecutionMicroseconds)
            .ThenByDescending(driver => driver.MaximumExecutionMicroseconds)
            .Take(20)
            .ToArray();

        return new EtwSummary(
            _requested,
            _requested && _unavailableReason is null && _processingTask is not null,
            _unavailableReason,
            _diskReadBytes,
            _diskWriteBytes,
            _networkSendBytes,
            _networkReceiveBytes,
            _dpcCount,
            _isrCount,
            _maximumDpcMicroseconds,
            _maximumIsrMicroseconds,
            drivers);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private void AddDisk(int processId, int size, bool read)
    {
        if (size <= 0)
        {
            return;
        }

        var totals = GetProcess(processId);
        if (read)
        {
            totals.DiskReadBytes += size;
            _diskReadBytes += size;
        }
        else
        {
            totals.DiskWriteBytes += size;
            _diskWriteBytes += size;
        }
    }

    private void AddNetwork(int processId, int size, bool send)
    {
        if (size <= 0)
        {
            return;
        }

        var totals = GetProcess(processId);
        if (send)
        {
            totals.NetworkSendBytes += size;
            _networkSendBytes += size;
        }
        else
        {
            totals.NetworkReceiveBytes += size;
            _networkReceiveBytes += size;
        }
    }

    private void AddDpc(DPCTraceData data)
    {
        var duration = Math.Max(0, data.ElapsedTimeMSec * 1000);
        _dpcCount++;
        _maximumDpcMicroseconds = Math.Max(_maximumDpcMicroseconds ?? 0, duration);
        AddRoutine(data.Routine, isDpc: true, duration);
    }

    private void AddIsr(ISRTraceData data)
    {
        var duration = Math.Max(0, data.ElapsedTimeMSec * 1000);
        _isrCount++;
        _maximumIsrMicroseconds = Math.Max(_maximumIsrMicroseconds ?? 0, duration);
        AddRoutine(data.Routine, isDpc: false, duration);
    }

    private void AddRoutine(ulong routine, bool isDpc, double durationMicroseconds)
    {
        var key = (routine, isDpc);
        if (!_routineTotals.TryGetValue(key, out var totals))
        {
            totals = new MutableRoutineTotals();
            _routineTotals[key] = totals;
        }

        totals.Count++;
        totals.TotalMicroseconds += durationMicroseconds;
        totals.MaximumMicroseconds = Math.Max(
            totals.MaximumMicroseconds,
            durationMicroseconds);
    }

    private void AddImage(ImageLoadTraceData data)
    {
        if (data.ImageBase == 0 || data.ImageSize <= 0)
        {
            return;
        }

        _images.Add(new ImageRange(data.ImageBase, (uint)data.ImageSize, data.FileName));
    }

    private MutableProcessTotals GetProcess(int processId)
    {
        if (!_processTotals.TryGetValue(processId, out var totals))
        {
            totals = new MutableProcessTotals();
            _processTotals[processId] = totals;
        }

        return totals;
    }

    private static string ResolveImage(ulong address, IReadOnlyList<ImageRange> ranges)
    {
        foreach (var image in ranges)
        {
            if (address >= image.Base && address - image.Base < image.Size)
            {
                return Path.GetFileName(image.FileName);
            }
        }

        return $"Unresolved routine 0x{address:X}";
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private sealed class MutableProcessTotals
    {
        public long DiskReadBytes { get; set; }

        public long DiskWriteBytes { get; set; }

        public long NetworkSendBytes { get; set; }

        public long NetworkReceiveBytes { get; set; }

        public EtwProcessTotals ToImmutable() =>
            new(
                DiskReadBytes,
                DiskWriteBytes,
                NetworkSendBytes,
                NetworkReceiveBytes);
    }

    private sealed class MutableRoutineTotals
    {
        public long Count { get; set; }

        public double TotalMicroseconds { get; set; }

        public double MaximumMicroseconds { get; set; }
    }

    private sealed record ImageRange(ulong Base, uint Size, string FileName);
}

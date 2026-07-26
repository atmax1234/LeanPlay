using System.Diagnostics;
using System.Runtime.InteropServices;
using LeanPlay.Analyzer.Model;

namespace LeanPlay.Analyzer.Collectors;

internal sealed partial class ProcessSampler
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private readonly int _logicalProcessorCount = Environment.ProcessorCount;
    private readonly Dictionary<ProcessKey, ProcessPoint> _previous = new();
    private readonly Dictionary<ProcessKey, ProcessAccumulator> _aggregates = new();

    public void Sample(TimeSpan elapsed)
    {
        var currentKeys = new HashSet<ProcessKey>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var key = new ProcessKey(process.Id, process.ProcessName);
                    currentKeys.Add(key);
                    var io = TryReadIo(process.Id);
                    var point = new ProcessPoint(
                        process.TotalProcessorTime,
                        io?.ReadTransferCount ?? 0,
                        io?.WriteTransferCount ?? 0);

                    if (!_aggregates.TryGetValue(key, out var aggregate))
                    {
                        aggregate = new ProcessAccumulator(
                            process.Id,
                            process.ProcessName,
                            TryGetPath(process));
                        _aggregates[key] = aggregate;
                    }

                    var cpuPercent = 0d;
                    var ioBytesPerSecond = 0d;
                    if (_previous.TryGetValue(key, out var previous) &&
                        elapsed.TotalSeconds > 0)
                    {
                        cpuPercent = Math.Max(
                            0,
                            (point.CpuTime - previous.CpuTime).TotalSeconds /
                            elapsed.TotalSeconds /
                            _logicalProcessorCount *
                            100);
                        var ioDelta = SaturatingDelta(
                                          point.ReadBytes,
                                          previous.ReadBytes) +
                                      SaturatingDelta(
                                          point.WriteBytes,
                                          previous.WriteBytes);
                        ioBytesPerSecond = ioDelta / elapsed.TotalSeconds;
                    }

                    aggregate.Add(
                        cpuPercent,
                        ioBytesPerSecond,
                        point.ReadBytes,
                        point.WriteBytes,
                        process.WorkingSet64,
                        process.PrivateMemorySize64,
                        process.Threads.Count);
                    _previous[key] = point;
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException
                        or System.ComponentModel.Win32Exception
                        or NotSupportedException)
                {
                    // Protected and terminating processes are expected.
                }
            }
        }

        foreach (var stale in _previous.Keys.Where(key => !currentKeys.Contains(key)).ToArray())
        {
            _previous.Remove(stale);
        }
    }

    public IReadOnlyList<ProcessSummary> Build(
        IReadOnlyDictionary<int, EtwProcessTotals> etwTotals,
        IReadOnlyDictionary<int, int> connectionCounts,
        IReadOnlyDictionary<int, int> udpEndpointCounts) =>
        _aggregates.Values
            .Select(aggregate =>
            {
                etwTotals.TryGetValue(aggregate.ProcessId, out var etw);
                return aggregate.Build(
                    etw ?? EtwProcessTotals.Empty,
                    connectionCounts.GetValueOrDefault(aggregate.ProcessId),
                    udpEndpointCounts.GetValueOrDefault(aggregate.ProcessId));
            })
            .OrderByDescending(process => process.AverageCpuPercent)
            .ThenByDescending(process => process.AverageIoBytesPerSecond)
            .ToArray();

    private static IoCounters? TryReadIo(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            return GetProcessIoCounters(handle, out var counters) ? counters : null;
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    private static string? TryGetPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException)
        {
            return null;
        }
    }

    private static ulong SaturatingDelta(ulong current, ulong previous) =>
        current >= previous ? current - previous : 0;

    private readonly record struct ProcessKey(int Id, string Name);

    private readonly record struct ProcessPoint(
        TimeSpan CpuTime,
        ulong ReadBytes,
        ulong WriteBytes);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct IoCounters
    {
        public readonly ulong ReadOperationCount;
        public readonly ulong WriteOperationCount;
        public readonly ulong OtherOperationCount;
        public readonly ulong ReadTransferCount;
        public readonly ulong WriteTransferCount;
        public readonly ulong OtherTransferCount;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessIoCounters(
        nint processHandle,
        out IoCounters ioCounters);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    private sealed class ProcessAccumulator
    {
        private double _cpuTotal;
        private double _ioRateTotal;
        private ulong _firstReadBytes;
        private ulong _firstWriteBytes;
        private ulong _lastReadBytes;
        private ulong _lastWriteBytes;

        public ProcessAccumulator(int processId, string name, string? executablePath)
        {
            ProcessId = processId;
            Name = name;
            ExecutablePath = executablePath;
        }

        public int ProcessId { get; }

        public string Name { get; }

        public string? ExecutablePath { get; }

        public int Samples { get; private set; }

        public double PeakCpu { get; private set; }

        public double PeakIoRate { get; private set; }

        public long PeakWorkingSet { get; private set; }

        public long PeakPrivateBytes { get; private set; }

        public int PeakThreads { get; private set; }

        public void Add(
            double cpuPercent,
            double ioRate,
            ulong readBytes,
            ulong writeBytes,
            long workingSet,
            long privateBytes,
            int threads)
        {
            if (Samples == 0)
            {
                _firstReadBytes = readBytes;
                _firstWriteBytes = writeBytes;
            }

            Samples++;
            _cpuTotal += cpuPercent;
            _ioRateTotal += ioRate;
            PeakCpu = Math.Max(PeakCpu, cpuPercent);
            PeakIoRate = Math.Max(PeakIoRate, ioRate);
            PeakWorkingSet = Math.Max(PeakWorkingSet, workingSet);
            PeakPrivateBytes = Math.Max(PeakPrivateBytes, privateBytes);
            PeakThreads = Math.Max(PeakThreads, threads);
            _lastReadBytes = readBytes;
            _lastWriteBytes = writeBytes;
        }

        public ProcessSummary Build(
            EtwProcessTotals etw,
            int connections,
            int udpEndpoints) =>
            new(
                ProcessId,
                Name,
                ExecutablePath,
                Samples,
                Samples == 0 ? 0 : _cpuTotal / Samples,
                PeakCpu,
                Samples == 0 ? 0 : _ioRateTotal / Samples,
                PeakIoRate,
                SaturatingDelta(_lastReadBytes, _firstReadBytes),
                SaturatingDelta(_lastWriteBytes, _firstWriteBytes),
                PeakWorkingSet,
                PeakPrivateBytes,
                PeakThreads,
                etw.DiskReadBytes,
                etw.DiskWriteBytes,
                etw.NetworkSendBytes,
                etw.NetworkReceiveBytes,
                connections,
                udpEndpoints);
    }
}

public sealed record EtwProcessTotals(
    long DiskReadBytes,
    long DiskWriteBytes,
    long NetworkSendBytes,
    long NetworkReceiveBytes)
{
    public static EtwProcessTotals Empty { get; } = new(0, 0, 0, 0);
}

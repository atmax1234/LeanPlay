using System.Globalization;
using LeanPlay.Analyzer.Model;

namespace LeanPlay.Analyzer.Analysis;

public sealed class FindingAnalyzer
{
    private static readonly HashSet<string> StorageEventProviders = new(
        new[]
        {
            "disk",
            "Microsoft-Windows-Disk",
            "storahci",
            "stornvme",
            "storport",
            "Ntfs",
            "volmgr"
        },
        StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<Finding> Analyze(
        MachineInventory inventory,
        IReadOnlyList<PerformanceSample> samples,
        IReadOnlyList<ProcessSummary> processes,
        NetworkSummary network,
        EtwSummary etw)
    {
        var findings = new List<Finding>();
        AnalyzeCoverage(samples, etw, findings);
        AnalyzeCpu(inventory, samples, processes, findings);
        AnalyzeMemory(inventory, samples, processes, findings);
        AnalyzeDisk(inventory, samples, processes, etw, findings);
        AnalyzeNetwork(network, processes, etw, findings);
        AnalyzeLatency(samples, etw, findings);
        AnalyzeGpu(inventory, samples, findings);
        AnalyzeReliability(inventory, processes, findings);
        AnalyzeConfiguration(inventory, findings);
        AnalyzeBackgroundSoftware(inventory, processes, findings);

        return findings
            .OrderByDescending(finding => finding.Severity)
            .ThenByDescending(finding => finding.Confidence)
            .ThenBy(finding => finding.Category, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AnalyzeCoverage(
        IReadOnlyList<PerformanceSample> samples,
        EtwSummary etw,
        List<Finding> findings)
    {
        if (samples.Count < 5)
        {
            findings.Add(
                Finding(
                    "coverage.short",
                    FindingSeverity.Warning,
                    "Measurement",
                    "The trace is too short for stable conclusions",
                    $"Only {samples.Count} timed samples were recorded.",
                    "Short traces can catch a spike but cannot establish sustained behavior.",
                    "Run a 60–120 second trace while reproducing the stutter or slowdown.",
                    0.99));
        }

        if (etw.Requested && !etw.Collected)
        {
            findings.Add(
                Finding(
                    "coverage.etw",
                    FindingSeverity.Information,
                    "Measurement",
                    "Kernel driver attribution was unavailable",
                    etw.UnavailableReason ?? "Kernel ETW did not produce a trace.",
                    "System-level DPC percentages were measured, but individual driver " +
                    "execution and exact per-process disk/network bytes are less certain.",
                    "Run the analyzer through Run-LeanPlayAnalysis.ps1 and approve the " +
                    "elevation prompt when driver-level evidence is needed.",
                    1));
        }
    }

    private static void AnalyzeCpu(
        MachineInventory inventory,
        IReadOnlyList<PerformanceSample> samples,
        IReadOnlyList<ProcessSummary> processes,
        List<Finding> findings)
    {
        var average = Statistics.Average(samples.Select(sample => sample.CpuTotalPercent));
        var p95 = Statistics.Percentile(
            samples.Select(sample => sample.CpuTotalPercent),
            95);
        var peak = Statistics.Maximum(samples.Select(sample => sample.CpuTotalPercent));
        var queueP95 = Statistics.Percentile(
            samples.Select(sample => sample.ProcessorQueueLength),
            95);
        var logicalProcessors = inventory.Cpus.Sum(cpu => cpu.LogicalProcessors);

        if (average is double avg &&
            p95 is double cpuP95 &&
            queueP95 is double queue &&
            ((avg >= 85 && queue >= 2) ||
             (cpuP95 >= 97 && queue >= Math.Max(2, logicalProcessors * 0.25))))
        {
            findings.Add(
                Finding(
                    "cpu.saturation",
                    avg >= 90 ? FindingSeverity.Critical : FindingSeverity.Warning,
                    "CPU",
                    "CPU scheduling saturation was measured",
                    $"Average CPU was {avg:F1}%, 95th percentile {cpuP95:F1}%, " +
                    $"and processor queue 95th percentile {queue:F1}.",
                    "Runnable work was waiting for CPU time. On a high-end CPU this " +
                    "usually indicates a demanding workload, runaway background work, " +
                    "or a process with excessive thread contention.",
                    $"Inspect the top CPU consumers in this report. Re-run during the " +
                    $"problem workload and close only the measured nonessential offender.",
                    0.96));
        }
        else if (p95 is >= 90 && average is < 70)
        {
            findings.Add(
                Finding(
                    "cpu.spikes",
                    FindingSeverity.Warning,
                    "CPU",
                    "Short CPU spikes may explain intermittent stutter",
                    $"CPU averaged {Format(average)}% but reached {Format(peak)}%; " +
                    $"the 95th percentile was {Format(p95)}%.",
                    "Brief all-core bursts can delay game, audio, and driver work even " +
                    "when average utilization looks healthy.",
                    "Match the timestamps to the process peaks below and repeat the trace " +
                    "while the visible stutter occurs.",
                    0.82));
        }
        else if (average is not null)
        {
            findings.Add(
                Finding(
                    "cpu.healthy",
                    FindingSeverity.Good,
                    "CPU",
                    "No sustained CPU saturation was observed",
                    $"Average CPU was {Format(average)}%; 95th percentile " +
                    $"{Format(p95)}%; peak {Format(peak)}%.",
                    "The processor had headroom during this trace.",
                    "If the issue occurs only in a game, run the analyzer while reproducing it.",
                    0.9));
        }

        var performanceP10 = Statistics.Percentile(
            samples.Select(sample => sample.CpuPerformancePercent),
            10);
        if (average is >= 35 && performanceP10 is > 0 and < 75)
        {
            findings.Add(
                Finding(
                    "cpu.performance",
                    FindingSeverity.Warning,
                    "CPU",
                    "CPU effective performance fell while the processor was busy",
                    $"CPU load averaged {average:F1}% while the 10th percentile of " +
                    $"Processor Performance was {performanceP10:F1}% of nominal.",
                    "This can be caused by temperature, firmware power limits, a restrictive " +
                    "power policy, or virtualization scheduling.",
                    "Check CPU temperature with the motherboard vendor or HWiNFO, update " +
                    "BIOS/chipset only from the OEM, and compare the same workload before " +
                    "changing power policy.",
                    0.72));
        }

        var top = processes
            .Where(process =>
                !process.Name.Contains("LeanPlay.Analyzer", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(process => process.AverageCpuPercent)
            .FirstOrDefault();
        if (top is not null && (top.AverageCpuPercent >= 8 || top.PeakCpuPercent >= 25))
        {
            findings.Add(
                Finding(
                    "cpu.process",
                    top.AverageCpuPercent >= 20
                        ? FindingSeverity.Warning
                        : FindingSeverity.Information,
                    "CPU",
                    $"{top.Name} was the largest measured CPU consumer",
                    $"PID {top.ProcessId} averaged {top.AverageCpuPercent:F1}% total CPU " +
                    $"and peaked at {top.PeakCpuPercent:F1}%.",
                    "This is evidence of CPU activity, not proof that the process caused " +
                    "a visible frame-time spike.",
                    "Confirm whether this process is expected for the workload. If not, " +
                    "disable its own background/startup option and repeat the trace.",
                    0.9));
        }
    }

    private static void AnalyzeMemory(
        MachineInventory inventory,
        IReadOnlyList<PerformanceSample> samples,
        IReadOnlyList<ProcessSummary> processes,
        List<Finding> findings)
    {
        var minimumAvailableMb = Statistics.Minimum(
            samples.Select(sample => sample.AvailableMemoryMb));
        var maximumCommit = Statistics.Maximum(
            samples.Select(sample => sample.CommittedMemoryPercent));
        var pagesInputP95 = Statistics.Percentile(
            samples.Select(sample => sample.PagesInputPerSecond),
            95);
        var totalMb = inventory.Computer.TotalPhysicalMemoryBytes / 1024d / 1024d;
        double? availablePercent = minimumAvailableMb is double available && totalMb > 0
            ? available / totalMb * 100
            : null;

        if (minimumAvailableMb is double free &&
            (free < 1024 || availablePercent is < 5 || maximumCommit is >= 95))
        {
            findings.Add(
                Finding(
                    "memory.pressure",
                    FindingSeverity.Critical,
                    "Memory",
                    "Severe memory or commit pressure was measured",
                    $"Minimum available memory was {free:F0} MB " +
                    $"({Format(availablePercent)}%); maximum commit was " +
                    $"{Format(maximumCommit)}%.",
                    "Paging and allocation stalls can produce large stutters even when CPU " +
                    "and GPU hardware are fast.",
                    "Sort the process table by private memory, close the measured offender, " +
                    "verify the page file is system-managed, and repeat the trace.",
                    0.97));
        }
        else if (minimumAvailableMb is double warningFree &&
                 (warningFree < 4096 || availablePercent is < 15 ||
                  pagesInputP95 is >= 100))
        {
            findings.Add(
                Finding(
                    "memory.moderate",
                    FindingSeverity.Warning,
                    "Memory",
                    "Memory headroom became limited",
                    $"Minimum available memory was {warningFree:F0} MB; commit peaked at " +
                    $"{Format(maximumCommit)}%; hard-page input 95th percentile was " +
                    $"{Format(pagesInputP95)} pages/s.",
                    "This amount of pressure can amplify asset-loading and application " +
                    "switching stalls.",
                    "Inspect the largest private-memory processes and browser/launcher " +
                    "background tabs before considering additional RAM.",
                    0.88));
        }
        else if (minimumAvailableMb is not null)
        {
            findings.Add(
                Finding(
                    "memory.healthy",
                    FindingSeverity.Good,
                    "Memory",
                    "No meaningful memory pressure was observed",
                    $"At least {minimumAvailableMb:F0} MB remained available and commit " +
                    $"peaked at {Format(maximumCommit)}%.",
                    "RAM capacity was not a bottleneck during this trace.",
                    "No memory optimization is justified from this sample.",
                    0.92));
        }

        var top = processes.OrderByDescending(process => process.PeakPrivateBytes).FirstOrDefault();
        if (top is not null && top.PeakPrivateBytes >= 4L * 1024 * 1024 * 1024)
        {
            findings.Add(
                Finding(
                    "memory.process",
                    FindingSeverity.Information,
                    "Memory",
                    $"{top.Name} held the largest private allocation",
                    $"PID {top.ProcessId} reached " +
                    $"{top.PeakPrivateBytes / 1024d / 1024d / 1024d:F1} GB private bytes.",
                    "A large allocation is normal for some games and content tools; it is " +
                    "relevant only when system pressure is also present.",
                    "Compare this process across a normal and a degraded session.",
                    0.9));
        }
    }

    private static void AnalyzeDisk(
        MachineInventory inventory,
        IReadOnlyList<PerformanceSample> samples,
        IReadOnlyList<ProcessSummary> processes,
        EtwSummary etw,
        List<Finding> findings)
    {
        var readP95 = Statistics.Percentile(
            samples.Select(sample => sample.DiskReadLatencyMs),
            95);
        var writeP95 = Statistics.Percentile(
            samples.Select(sample => sample.DiskWriteLatencyMs),
            95);
        var latencyP95 = Math.Max(readP95 ?? 0, writeP95 ?? 0);
        var activeP95 = Statistics.Percentile(
            samples.Select(sample => sample.DiskActivePercent),
            95);
        var queueP95 = Statistics.Percentile(
            samples.Select(sample => sample.DiskQueueLength),
            95);

        if (latencyP95 >= 30 || (activeP95 is >= 95 && queueP95 is >= 2))
        {
            findings.Add(
                Finding(
                    "disk.latency",
                    latencyP95 >= 75
                        ? FindingSeverity.Critical
                        : FindingSeverity.Warning,
                    "Storage",
                    "Storage latency or queueing can explain stalls",
                    $"Read/write latency 95th percentiles were {Format(readP95)} ms and " +
                    $"{Format(writeP95)} ms; active time p95 {Format(activeP95)}%; " +
                    $"queue p95 {Format(queueP95)}.",
                    "Long I/O completion times block asset streaming, paging, shader-cache " +
                    "work, and background scanners.",
                    "Use the top disk-I/O process below. Check drive health and free space; " +
                    "do not disable caching or services without a repeatable A/B result.",
                    0.93));
        }
        else if (latencyP95 >= 12 || (activeP95 is >= 80 && queueP95 is >= 1))
        {
            findings.Add(
                Finding(
                    "disk.moderate",
                    FindingSeverity.Warning,
                    "Storage",
                    "Intermittent storage pressure was observed",
                    $"Worst read/write p95 latency was {latencyP95:F1} ms with active time " +
                    $"p95 {Format(activeP95)}% and queue p95 {Format(queueP95)}.",
                    "This is high enough to be investigated when it overlaps the reported stutter.",
                    "Repeat during the issue and verify the same process or drive is involved.",
                    0.78));
        }
        else if (readP95 is not null || writeP95 is not null)
        {
            findings.Add(
                Finding(
                    "disk.healthy",
                    FindingSeverity.Good,
                    "Storage",
                    "No sustained storage queue or latency problem was observed",
                    $"Read/write latency p95 was {Format(readP95)} / " +
                    $"{Format(writeP95)} ms; queue p95 {Format(queueP95)}.",
                    "Storage responded normally during this trace.",
                    "No storage tuning is justified from this sample.",
                    0.88));
        }

        foreach (var volume in inventory.Disks.SelectMany(disk => disk.Volumes))
        {
            if (volume.SizeBytes == 0)
            {
                continue;
            }

            var freePercent = volume.FreeBytes * 100d / volume.SizeBytes;
            if (freePercent < 15)
            {
                findings.Add(
                    Finding(
                        $"disk.free.{volume.Name}",
                        freePercent < 7
                            ? FindingSeverity.Critical
                            : FindingSeverity.Warning,
                        "Storage",
                        $"{volume.Name} has little free space",
                        $"{freePercent:F1}% free " +
                        $"({volume.FreeBytes / 1024d / 1024d / 1024d:F1} GB).",
                        "Low free space can constrain updates, shader caches, temporary files, " +
                        "and SSD housekeeping.",
                        "Remove or move known files; keep the page file and Windows-managed " +
                        "system folders intact.",
                        0.99));
            }
        }

        var top = processes
            .OrderByDescending(process =>
                etw.Collected
                    ? (double)(process.EtwDiskReadBytes + process.EtwDiskWriteBytes)
                    : process.IoReadBytes + process.IoWriteBytes)
            .FirstOrDefault();
        var bytes = top is null
            ? 0
            : etw.Collected
                ? top.EtwDiskReadBytes + top.EtwDiskWriteBytes
                : ToLongSaturated(top.IoReadBytes + top.IoWriteBytes);
        if (top is not null && bytes >= 100L * 1024 * 1024)
        {
            findings.Add(
                Finding(
                    "disk.process",
                    FindingSeverity.Information,
                    "Storage",
                    $"{top.Name} generated the most measured I/O",
                    $"PID {top.ProcessId} transferred approximately " +
                    $"{bytes / 1024d / 1024d:F0} MB during the trace.",
                    etw.Collected
                        ? "Kernel disk events provide direct attribution."
                        : "The fallback process I/O counter can include non-disk device I/O.",
                    "Check whether this transfer overlapped the slowdown and whether it was " +
                    "an expected game/update/scanner operation.",
                    etw.Collected ? 0.96 : 0.65));
        }
    }

    private static void AnalyzeNetwork(
        NetworkSummary network,
        IReadOnlyList<ProcessSummary> processes,
        EtwSummary etw,
        List<Finding> findings)
    {
        foreach (var target in network.PingTargets)
        {
            if (target.Received == 0)
            {
                findings.Add(
                    Finding(
                        $"network.unreachable.{target.Target}",
                        FindingSeverity.Information,
                        "Network",
                        $"{target.Role} did not answer ICMP",
                        $"{target.Target}: {target.Error ?? "no successful replies"}.",
                        "Some routers and hosts intentionally block ping, so this is not proof " +
                        "of packet loss for game traffic.",
                        "Use another known-responsive target or the game server endpoint.",
                        0.45));
                continue;
            }

            if (target.LossPercent > 0 ||
                target.JitterMs is >= 8 ||
                (target.Role == "Default gateway" && target.MaximumMs is >= 15))
            {
                var severity =
                    target.LossPercent >= 3 ||
                    (target.Role == "Default gateway" && target.JitterMs is >= 15)
                        ? FindingSeverity.Critical
                        : FindingSeverity.Warning;
                findings.Add(
                    Finding(
                        $"network.quality.{target.Target}",
                        severity,
                        "Network",
                        $"{target.Role} showed latency instability",
                        $"{target.Target}: loss {target.LossPercent:F1}%, average " +
                        $"{Format(target.AverageMs)} ms, maximum {Format(target.MaximumMs)} ms, " +
                        $"jitter {Format(target.JitterMs)} ms.",
                        target.Role == "Default gateway"
                            ? "Instability before traffic leaves the LAN points to Wi-Fi, " +
                              "adapter, cabling, router load, or a local driver."
                            : "A stable gateway with an unstable public target points beyond " +
                              "the PC/LAN, but route and ICMP treatment still matter.",
                        target.Role == "Default gateway"
                            ? "Test wired Ethernet, check adapter errors/driver, and repeat with " +
                              "other LAN devices idle."
                            : "Compare multiple public targets and the actual game server; do " +
                              "not apply registry 'ping tweaks'.",
                        target.Role == "Default gateway" ? 0.94 : 0.72));
            }
            else
            {
                findings.Add(
                    Finding(
                        $"network.healthy.{target.Target}",
                        FindingSeverity.Good,
                        "Network",
                        $"{target.Role} was stable during the trace",
                        $"{target.Target}: 0% loss, average {Format(target.AverageMs)} ms, " +
                        $"jitter {Format(target.JitterMs)} ms.",
                        "No ICMP-visible instability was measured on this path.",
                        "For game-specific issues, repeat against the game server route.",
                        target.Role == "Default gateway" ? 0.95 : 0.75));
            }
        }

        if (network.TcpRetransmitPercent is >= 2)
        {
            findings.Add(
                Finding(
                    "network.retransmit",
                    network.TcpRetransmitPercent >= 5
                        ? FindingSeverity.Critical
                        : FindingSeverity.Warning,
                    "Network",
                    "Elevated TCP retransmissions were measured",
                    $"{network.TcpSegmentsRetransmitted} of " +
                    $"{network.TcpSegmentsSent} sent segments were retransmitted " +
                    $"({network.TcpRetransmitPercent:F2}%).",
                    "Retransmissions indicate loss or severe reordering for local TCP traffic. " +
                    "They do not directly measure a UDP game flow.",
                    "Check gateway results, Wi-Fi signal/interference, Ethernet errors, and " +
                    "background transfers before changing TCP settings.",
                    0.88));
        }

        if (etw.Collected)
        {
            var top = processes
                .OrderByDescending(process =>
                    process.EtwNetworkSendBytes + process.EtwNetworkReceiveBytes)
                .FirstOrDefault();
            var bytes = top is null
                ? 0
                : top.EtwNetworkSendBytes + top.EtwNetworkReceiveBytes;
            if (top is not null && bytes >= 10L * 1024 * 1024)
            {
                findings.Add(
                    Finding(
                        "network.process",
                        FindingSeverity.Information,
                        "Network",
                        $"{top.Name} generated the most network traffic",
                        $"PID {top.ProcessId} transferred approximately " +
                        $"{bytes / 1024d / 1024d:F1} MB in kernel TCP/IP events.",
                        "Traffic volume matters when it fills the access-link or router queue; " +
                        "volume alone does not prove latency impact.",
                        "Compare this transfer with ping/jitter timestamps and pause only the " +
                        "application's own download/sync feature for an A/B test.",
                        0.94));
            }
        }
    }

    private static void AnalyzeLatency(
        IReadOnlyList<PerformanceSample> samples,
        EtwSummary etw,
        List<Finding> findings)
    {
        var dpcP95 = Statistics.Percentile(
            samples.Select(sample => sample.CpuDpcPercent),
            95);
        var interruptP95 = Statistics.Percentile(
            samples.Select(sample => sample.CpuInterruptPercent),
            95);

        if (etw.Collected &&
            (etw.MaximumDpcMicroseconds is >= 250 ||
             etw.MaximumIsrMicroseconds is >= 250))
        {
            var maximum = Math.Max(
                etw.MaximumDpcMicroseconds ?? 0,
                etw.MaximumIsrMicroseconds ?? 0);
            var top = etw.DriverLatency.Count > 0 ? etw.DriverLatency[0] : null;
            findings.Add(
                Finding(
                    "latency.driver",
                    maximum >= 1000
                        ? FindingSeverity.Critical
                        : FindingSeverity.Warning,
                    "Drivers / DPC",
                    "A long-running driver interrupt routine was measured",
                    $"Maximum DPC was {Format(etw.MaximumDpcMicroseconds)} µs; maximum ISR " +
                    $"{Format(etw.MaximumIsrMicroseconds)} µs." +
                    (top is null
                        ? string.Empty
                        : $" Largest aggregate driver: {top.Driver} " +
                          $"({top.TotalExecutionMicroseconds:F0} µs total)."),
                    "Long DPC/ISR execution can block real-time audio and game threads even " +
                    "when average CPU usage is low.",
                    "Identify the named device, install a stable OEM driver/firmware, and " +
                    "repeat the same trace. Do not delete or disable kernel drivers blindly.",
                    top is null || top.Driver.StartsWith(
                        "Unresolved",
                        StringComparison.OrdinalIgnoreCase)
                        ? 0.72
                        : 0.96));
        }
        else if (dpcP95 is >= 2 || interruptP95 is >= 2)
        {
            findings.Add(
                Finding(
                    "latency.system",
                    FindingSeverity.Warning,
                    "Drivers / DPC",
                    "Elevated interrupt/DPC CPU time was observed",
                    $"DPC time p95 {Format(dpcP95)}%; interrupt time p95 " +
                    $"{Format(interruptP95)}%.",
                    "A driver or interrupt-heavy device was consuming measurable CPU at " +
                    "high priority.",
                    etw.Collected
                        ? "Use the driver table to select a specific device for an OEM " +
                          "driver/firmware A/B test."
                        : "Re-run elevated for individual DPC/ISR driver attribution.",
                    etw.Collected ? 0.88 : 0.68));
        }
        else if (dpcP95 is not null)
        {
            findings.Add(
                Finding(
                    "latency.healthy",
                    FindingSeverity.Good,
                    "Drivers / DPC",
                    "No broad interrupt/DPC CPU saturation was observed",
                    $"DPC time p95 {Format(dpcP95)}%; interrupt time p95 " +
                    $"{Format(interruptP95)}%.",
                    "High-priority driver work did not consume a large CPU share in this trace.",
                    etw.Collected
                        ? "Inspect maximum routine time as well; one long event can matter."
                        : "Run elevated if individual routine latency is the suspected issue.",
                    etw.Collected ? 0.9 : 0.62));
        }
    }

    private static void AnalyzeGpu(
        MachineInventory inventory,
        IReadOnlyList<PerformanceSample> samples,
        List<Finding> findings)
    {
        var average = Statistics.Average(samples.Select(sample => sample.GpuBusyPercent));
        var p95 = Statistics.Percentile(samples.Select(sample => sample.GpuBusyPercent), 95);
        var hottest = inventory.Gpus
            .Where(gpu => gpu.TemperatureCelsius is not null)
            .OrderByDescending(gpu => gpu.TemperatureCelsius)
            .FirstOrDefault();

        if (hottest?.TemperatureCelsius is >= 83)
        {
            findings.Add(
                Finding(
                    "gpu.temperature",
                    hottest.TemperatureCelsius >= 90
                        ? FindingSeverity.Critical
                        : FindingSeverity.Warning,
                    "GPU",
                    $"{hottest.Name} temperature is high",
                    $"The inventory reading was {hottest.TemperatureCelsius:F0} °C.",
                    "A hot GPU may reduce clocks or fan/noise headroom. One idle snapshot " +
                    "does not establish sustained thermal throttling.",
                    "Record temperature and clock during the actual game with the vendor " +
                    "overlay, verify airflow/fans, and avoid an undervolt until a baseline exists.",
                    0.78));
        }

        if (average is >= 20)
        {
            findings.Add(
                Finding(
                    "gpu.activity",
                    FindingSeverity.Information,
                    "GPU",
                    "Meaningful GPU engine activity was present",
                    $"GPU busy averaged {average:F1}% and reached p95 {Format(p95)}%.",
                    "This may be the intended workload, desktop composition, video, or an overlay.",
                    "Repeat with the problem workload label and compare clocks, temperature, " +
                    "and frame-time data; utilization alone is not a bottleneck diagnosis.",
                    0.7));
        }
    }

    private static void AnalyzeReliability(
        MachineInventory inventory,
        IReadOnlyList<ProcessSummary> processes,
        List<Finding> findings)
    {
        var whea = inventory.RecentSystemEvents.Where(
                item => item.Provider.Contains(
                    "WHEA",
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (whea.Length > 0)
        {
            findings.Add(
                Finding(
                    "reliability.whea",
                    FindingSeverity.Critical,
                    "Reliability",
                    "Hardware-error (WHEA) events exist in the last seven days",
                    $"{whea.Length} WHEA event(s); latest ID {whea[0].EventId} at " +
                    $"{whea[0].Timestamp:O}.",
                    "WHEA events can indicate CPU, memory, PCIe, firmware, voltage, or device " +
                    "instability and take priority over Windows 'optimization'.",
                    "Return BIOS tuning/overclock/undervolt to a known-stable baseline, update " +
                    "only OEM firmware/drivers, and investigate the event details.",
                    0.99));
        }

        var displayResets = inventory.RecentSystemEvents.Where(
                item =>
                    item.EventId == 4101 ||
                    item.Provider.Contains(
                        "nvlddmkm",
                        StringComparison.OrdinalIgnoreCase) ||
                    item.Provider.Equals("Display", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (displayResets.Length > 0)
        {
            findings.Add(
                Finding(
                    "reliability.display",
                    FindingSeverity.Warning,
                    "Reliability",
                    "Recent display-driver errors or resets were found",
                    $"{displayResets.Length} matching event(s); latest provider " +
                    $"{displayResets[0].Provider}, ID {displayResets[0].EventId}.",
                    "Driver recovery produces severe stutter, black screens, or application loss.",
                    "Use a stable NVIDIA/OEM driver, remove GPU overclock/undervolt for the " +
                    "test, and reproduce before considering a clean driver reinstall.",
                    0.94));
        }

        var storageEvents = inventory.RecentSystemEvents.Where(
                item =>
                    StorageEventProviders.Contains(item.Provider) ||
                    item.EventId is 7 or 51 or 129 or 153 or 157)
            .ToArray();
        if (storageEvents.Length > 0)
        {
            findings.Add(
                Finding(
                    "reliability.storage",
                    FindingSeverity.Warning,
                    "Reliability",
                    "Recent storage timeout/error events were found",
                    $"{storageEvents.Length} matching event(s); latest provider " +
                    $"{storageEvents[0].Provider}, ID {storageEvents[0].EventId}.",
                    "Storage resets and timeouts can freeze the whole system independent of " +
                    "average benchmark performance.",
                    "Back up important data, check the drive/vendor health tool and firmware, " +
                    "and inspect cabling/slot/controller before performance tuning.",
                    0.95));
        }

        var deviceProblems = inventory.RelevantDrivers
            .Where(driver => driver.ProblemCode is not 0 and not 22)
            .ToArray();
        if (deviceProblems.Length > 0)
        {
            findings.Add(
                Finding(
                    "reliability.devices",
                    FindingSeverity.Warning,
                    "Reliability",
                    "Windows reports devices with configuration errors",
                    string.Join(
                        "; ",
                        deviceProblems.Take(5).Select(
                            driver => $"{driver.DeviceName} (code {driver.ProblemCode})")),
                    "A device that is failing to initialize can cause retries, missing features, " +
                    "or driver activity.",
                    "Resolve the specific Device Manager error through the hardware/OEM " +
                    "documentation rather than installing a generic driver updater.",
                    0.99));
        }

        var udpExhaustion = inventory.RecentSystemEvents.Where(
                item =>
                    item.Provider.Equals("Tcpip", StringComparison.OrdinalIgnoreCase) &&
                    item.EventId == 4266)
            .ToArray();
        if (udpExhaustion.Length > 0)
        {
            var topUdp = processes
                .OrderByDescending(process => process.UdpEndpointCount)
                .Take(5)
                .Where(process => process.UdpEndpointCount > 0)
                .Select(process => $"{process.Name} ({process.UdpEndpointCount})")
                .ToArray();
            findings.Add(
                Finding(
                    "network.udp-port-exhaustion",
                    FindingSeverity.Critical,
                    "Network",
                    "Windows exhausted its global UDP ephemeral-port space",
                    $"{udpExhaustion.Length} TCP/IP event 4266 warning(s) in seven days; " +
                    $"latest at {udpExhaustion[0].Timestamp:O}." +
                    (topUdp.Length == 0
                        ? string.Empty
                        : $" Current UDP endpoint leaders: {string.Join(", ", topUdp)}."),
                    "New UDP sockets can fail while the port space is exhausted. That can " +
                    "directly disrupt games, voice chat, overlays, DNS, and launchers.",
                    "Restart first, then rerun and inspect UDP endpoint leaders. Update or " +
                    "close the leaking application; do not expand the dynamic port range " +
                    "until the owner is identified.",
                    0.99));
        }

        var wiredDisconnects = inventory.RecentSystemEvents.Where(
                item =>
                    item.EventId == 27 &&
                    (item.Provider.Contains(
                         "e2fn",
                         StringComparison.OrdinalIgnoreCase) ||
                     item.Message.Contains(
                         "Network link is disconnected",
                         StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (wiredDisconnects.Length >= 3)
        {
            findings.Add(
                Finding(
                    "network.link-disconnects",
                    FindingSeverity.Warning,
                    "Network",
                    "Repeated Ethernet link disconnects were logged",
                    $"{wiredDisconnects.Length} link-disconnect warning(s) in seven days; " +
                    $"latest at {wiredDisconnects[0].Timestamp:O}.",
                    "A physical or driver-level link drop interrupts all traffic and cannot be " +
                    "fixed by game QoS or TCP registry tuning. Some events can coincide with " +
                    "shutdown or sleep.",
                    "Correlate the timestamps with active use. If they occurred while awake, " +
                    "check cable/router port, install the motherboard-vendor Intel I226 driver, " +
                    "and A/B Energy Efficient Ethernet.",
                    0.84));
        }
    }

    private static void AnalyzeConfiguration(
        MachineInventory inventory,
        List<Finding> findings)
    {
        if (inventory.PowerAndGaming.PendingReboot)
        {
            findings.Add(
                Finding(
                    "configuration.reboot",
                    FindingSeverity.Warning,
                    "Configuration",
                    "Windows has a pending reboot",
                    "Servicing, update, or pending file-replacement state was detected.",
                    "Pending servicing can keep update components active and leave drivers or " +
                    "system files in an intermediate state.",
                    "Save work and perform one normal restart before collecting the decisive trace.",
                    0.9));
        }

        findings.Add(
            Finding(
                "configuration.power",
                FindingSeverity.Information,
                "Configuration",
                $"Active power plan: {inventory.PowerAndGaming.ActivePowerPlan}",
                $"GUID {inventory.PowerAndGaming.ActivePowerPlanGuid}; HAGS " +
                $"{inventory.PowerAndGaming.HardwareGpuScheduling}; Game Mode " +
                $"{inventory.PowerAndGaming.GameMode}.",
                "These settings are workload- and driver-dependent. Their labels alone do not " +
                "predict frame time.",
                "Keep them unchanged for the baseline, then compare one setting at a time with " +
                "the same workload.",
                1));

        if (inventory.Security.VirtualizationBasedSecurityRunning == true)
        {
            findings.Add(
                Finding(
                    "configuration.vbs",
                    FindingSeverity.Information,
                    "Configuration",
                    "Virtualization-based security is running",
                    string.Join(
                        ", ",
                        inventory.Security.RunningSecurityServices.DefaultIfEmpty(
                            "VBS active")),
                    "Security isolation can have a workload-dependent performance cost, but " +
                    "disabling it materially reduces protection.",
                    "Only evaluate it with a controlled before/after benchmark and restore " +
                    "security afterward; do not disable it based on this inventory alone.",
                    1));
        }
    }

    private static void AnalyzeBackgroundSoftware(
        MachineInventory inventory,
        IReadOnlyList<ProcessSummary> processes,
        List<Finding> findings)
    {
        var defender = processes.FirstOrDefault(process =>
            process.Name.Equals("MsMpEng", StringComparison.OrdinalIgnoreCase));
        if (defender is not null &&
            (defender.AverageCpuPercent >= 2 ||
             defender.EtwDiskReadBytes + defender.EtwDiskWriteBytes >=
             100L * 1024 * 1024))
        {
            findings.Add(
                Finding(
                    "background.defender",
                    FindingSeverity.Warning,
                    "Background activity",
                    "Microsoft Defender was materially active",
                    $"MsMpEng averaged {defender.AverageCpuPercent:F1}% CPU and generated " +
                    $"{(defender.EtwDiskReadBytes + defender.EtwDiskWriteBytes) / 1024d / 1024d:F0} " +
                    "MB of attributed disk I/O.",
                    "A scan overlapping asset loading or gameplay can create contention.",
                    "Let the scan finish and repeat. Prefer Microsoft-supported per-game-folder " +
                    "exclusions only after validating the tradeoff; never disable Defender globally.",
                    0.94));
        }

        if (inventory.Workload.ActiveOverlaysAndCaptureTools.Count > 0)
        {
            findings.Add(
                Finding(
                    "background.overlays",
                    FindingSeverity.Information,
                    "Background activity",
                    "Overlay or capture software was active",
                    string.Join(", ", inventory.Workload.ActiveOverlaysAndCaptureTools),
                    "These tools may be harmless, but capture, encoding, and multiple overlays " +
                    "can add GPU/CPU scheduling work.",
                    "Use the process evidence first. If symptoms persist, disable one overlay at " +
                    "a time and compare the same scenario.",
                    0.75));
        }

        if (inventory.Workload.ActiveCloudSyncAndLaunchers.Count > 0)
        {
            findings.Add(
                Finding(
                    "background.launchers",
                    FindingSeverity.Information,
                    "Background activity",
                    "Launchers or synchronization clients were active",
                    string.Join(", ", inventory.Workload.ActiveCloudSyncAndLaunchers),
                    "Their mere presence is not a performance problem; downloads, patching, and " +
                    "synchronization are.",
                    "Check their measured CPU/disk/network rows before pausing an application.",
                    0.7));
        }
    }

    private static Finding Finding(
        string id,
        FindingSeverity severity,
        string category,
        string title,
        string evidence,
        string interpretation,
        string recommendation,
        double confidence) =>
        new(
            id,
            severity,
            category,
            title,
            evidence,
            interpretation,
            recommendation,
            Math.Clamp(confidence, 0, 1));

    private static string Format(double? value) =>
        value?.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a";

    private static long ToLongSaturated(ulong value) =>
        value > long.MaxValue ? long.MaxValue : (long)value;
}

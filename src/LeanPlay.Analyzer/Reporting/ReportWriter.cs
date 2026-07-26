using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LeanPlay.Analyzer.Analysis;
using LeanPlay.Analyzer.Model;

namespace LeanPlay.Analyzer.Reporting;

public sealed class ReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task<ReportPaths> WriteAsync(
        AnalysisReport report,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var label = SanitizeFileName(report.Options.WorkloadLabel);
        var baseName =
            $"leanplay-{report.StartedAt:yyyyMMdd-HHmmss}" +
            (string.IsNullOrWhiteSpace(label) ? string.Empty : $"-{label}");
        var jsonPath = Path.Combine(outputDirectory, $"{baseName}.json");
        var htmlPath = Path.Combine(outputDirectory, $"{baseName}.html");

        await WriteAtomicallyAsync(
            jsonPath,
            JsonSerializer.Serialize(report, JsonOptions),
            cancellationToken).ConfigureAwait(false);
        await WriteAtomicallyAsync(
            htmlPath,
            BuildHtml(report, Path.GetFileName(jsonPath)),
            cancellationToken).ConfigureAwait(false);
        return new ReportPaths(htmlPath, jsonPath);
    }

    private static string BuildHtml(AnalysisReport report, string jsonFileName)
    {
        var html = new StringBuilder(128 * 1024);
        var critical = report.Findings.Count(
            finding => finding.Severity == FindingSeverity.Critical);
        var warnings = report.Findings.Count(
            finding => finding.Severity == FindingSeverity.Warning);
        var status = critical > 0
            ? "Critical evidence found"
            : warnings > 0 ? "Items to investigate" : "No broad bottleneck measured";
        var statusClass = critical > 0 ? "critical" : warnings > 0 ? "warning" : "good";

        html.Append(
            """
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>LeanPlay Windows Analysis</title>
              <style>
                :root {
                  color-scheme: dark;
                  --bg: #090d14;
                  --panel: #111824;
                  --panel2: #161f2e;
                  --line: #273449;
                  --text: #ecf2ff;
                  --muted: #91a0b7;
                  --cyan: #49d6ff;
                  --blue: #718cff;
                  --green: #48d597;
                  --amber: #ffbd59;
                  --red: #ff647c;
                }
                * { box-sizing: border-box; }
                body {
                  margin: 0;
                  background:
                    radial-gradient(circle at 15% -10%, #193451 0, transparent 34rem),
                    radial-gradient(circle at 90% 5%, #222855 0, transparent 28rem),
                    var(--bg);
                  color: var(--text);
                  font: 15px/1.55 Inter, ui-sans-serif, system-ui, "Segoe UI", sans-serif;
                }
                main { max-width: 1240px; margin: 0 auto; padding: 42px 24px 80px; }
                h1 { font-size: clamp(32px, 5vw, 56px); line-height: 1.02; margin: 8px 0 14px; letter-spacing: -0.04em; }
                h2 { margin: 42px 0 16px; font-size: 24px; letter-spacing: -0.02em; }
                h3 { margin: 0 0 8px; font-size: 17px; }
                p { margin: 7px 0; }
                .eyebrow { color: var(--cyan); text-transform: uppercase; letter-spacing: .18em; font-size: 12px; font-weight: 800; }
                .subtitle { max-width: 850px; color: var(--muted); font-size: 17px; }
                .badges { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 20px; }
                .badge, .severity {
                  display: inline-flex; align-items: center; gap: 6px;
                  border: 1px solid var(--line); border-radius: 999px;
                  padding: 6px 11px; background: #0d1420; font-size: 12px; font-weight: 700;
                }
                .status { margin-top: 28px; border-left: 4px solid; padding: 18px 20px; border-radius: 8px; background: var(--panel); }
                .status.critical { border-color: var(--red); }
                .status.warning { border-color: var(--amber); }
                .status.good { border-color: var(--green); }
                .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 12px; }
                .card, .finding, .chart, .table-wrap {
                  background: linear-gradient(145deg, rgba(22,31,46,.96), rgba(14,21,32,.96));
                  border: 1px solid var(--line);
                  border-radius: 14px;
                  box-shadow: 0 16px 50px rgba(0,0,0,.18);
                }
                .card { padding: 16px; min-height: 112px; }
                .card .value { font-size: 27px; font-weight: 800; letter-spacing: -.03em; margin-top: 4px; }
                .card .label { color: var(--muted); font-size: 12px; text-transform: uppercase; letter-spacing: .1em; }
                .findings { display: grid; gap: 12px; }
                .finding { padding: 18px 20px; border-left: 4px solid var(--line); }
                .finding.critical { border-left-color: var(--red); }
                .finding.warning { border-left-color: var(--amber); }
                .finding.information { border-left-color: var(--blue); }
                .finding.good { border-left-color: var(--green); }
                .finding .meta { color: var(--muted); font-size: 12px; margin-bottom: 5px; }
                .finding strong { color: #fff; }
                .severity.critical { color: var(--red); border-color: color-mix(in srgb, var(--red) 50%, transparent); }
                .severity.warning { color: var(--amber); border-color: color-mix(in srgb, var(--amber) 50%, transparent); }
                .severity.information { color: #99aaff; border-color: #4b5795; }
                .severity.good { color: var(--green); border-color: #277b5c; }
                .charts { display: grid; grid-template-columns: repeat(auto-fit, minmax(360px, 1fr)); gap: 12px; }
                .chart { padding: 16px; overflow: hidden; }
                .chart svg { width: 100%; height: 180px; display: block; }
                .chart .axis { color: var(--muted); font-size: 12px; display: flex; justify-content: space-between; }
                .table-wrap { overflow-x: auto; }
                table { width: 100%; border-collapse: collapse; min-width: 760px; }
                th, td { padding: 11px 13px; border-bottom: 1px solid var(--line); text-align: left; vertical-align: top; }
                th { color: var(--muted); font-size: 11px; text-transform: uppercase; letter-spacing: .08em; background: #111927; position: sticky; top: 0; }
                tr:last-child td { border-bottom: 0; }
                td.numeric, th.numeric { text-align: right; font-variant-numeric: tabular-nums; }
                code { color: #b9eaff; background: #0a111b; border: 1px solid #23334b; padding: 1px 5px; border-radius: 5px; }
                .muted { color: var(--muted); }
                .two-col { display: grid; grid-template-columns: repeat(auto-fit, minmax(330px, 1fr)); gap: 12px; }
                .section-card { padding: 18px; }
                footer { margin-top: 52px; padding-top: 20px; border-top: 1px solid var(--line); color: var(--muted); font-size: 13px; }
                @media (max-width: 600px) { main { padding: 28px 14px 60px; } .charts { grid-template-columns: 1fr; } }
              </style>
            </head>
            <body><main>
            """);

        html.Append("<div class=\"eyebrow\">LeanPlay evidence report</div><h1>")
            .Append(E(status))
            .Append("</h1><p class=\"subtitle\">A read-only Windows performance trace. ")
            .Append("Findings distinguish measured contention from configuration inventory; ")
            .Append("correlation is not presented as causation.</p><div class=\"badges\">")
            .Append(Badge($"Trace {report.Options.DurationSeconds:F0} s"))
            .Append(Badge(report.Inventory.IsAdministrator ? "Elevated" : "Standard user"))
            .Append(Badge(report.Etw.Collected ? "Kernel ETW captured" : "Kernel ETW unavailable"))
            .Append(Badge(report.Options.WorkloadLabel ?? "Unlabelled workload"))
            .Append("</div><div class=\"status ")
            .Append(statusClass)
            .Append("\"><strong>")
            .Append(critical)
            .Append(" critical, ")
            .Append(warnings)
            .Append(" warning finding(s)</strong><br><span class=\"muted\">")
            .Append(E($"{report.StartedAt:yyyy-MM-dd HH:mm:ss zzz} · Report {report.Id}"))
            .Append("</span></div>");

        AppendMetricCards(html, report);
        AppendFindings(html, report.Findings);
        AppendCharts(html, report.Samples);
        AppendSystemInventory(html, report.Inventory);
        AppendProcesses(html, report.Processes, report.Etw.Collected);
        AppendNetwork(html, report.Network);
        AppendEtw(html, report.Etw);
        AppendDriversAndEvents(html, report.Inventory);
        AppendNotices(html, report.Notices);

        html.Append("<footer><strong>Method:</strong> Language-neutral Windows PDH counters, ")
            .Append("process time/I/O counters, IP Helper statistics, ICMP probes, WMI inventory, ")
            .Append("System event log, and optional kernel ETW. A clean trace only describes ")
            .Append("the measured interval. Reproduce the actual poor-performance workload for ")
            .Append("the decisive report.<br>Machine-readable data: <code>")
            .Append(E(jsonFileName))
            .Append("</code>.</footer></main></body></html>");
        return html.ToString();
    }

    private static void AppendMetricCards(StringBuilder html, AnalysisReport report)
    {
        var cpuP95 = Statistics.Percentile(
            report.Samples.Select(sample => sample.CpuTotalPercent),
            95);
        var memoryMin = Statistics.Minimum(
            report.Samples.Select(sample => sample.AvailableMemoryMb));
        var diskReadP95 = Statistics.Percentile(
            report.Samples.Select(sample => sample.DiskReadLatencyMs),
            95);
        var diskWriteP95 = Statistics.Percentile(
            report.Samples.Select(sample => sample.DiskWriteLatencyMs),
            95);
        var diskP95 = diskReadP95 is null
            ? diskWriteP95
            : diskWriteP95 is null
                ? diskReadP95
                : Math.Max(diskReadP95.Value, diskWriteP95.Value);
        var gpuP95 = Statistics.Percentile(
            report.Samples.Select(sample => sample.GpuBusyPercent),
            95);
        var worstLoss = report.Network.PingTargets
            .Select(target => target.LossPercent)
            .DefaultIfEmpty()
            .Max();
        var maxRoutine = Math.Max(
            report.Etw.MaximumDpcMicroseconds ?? 0,
            report.Etw.MaximumIsrMicroseconds ?? 0);

        html.Append("<h2>Trace summary</h2><div class=\"grid\">")
            .Append(MetricCard("CPU p95", F(cpuP95, "%")))
            .Append(MetricCard("Available RAM min", F(memoryMin, " MB", 0)))
            .Append(MetricCard("Disk latency p95", F(diskP95, " ms")))
            .Append(MetricCard("GPU busy p95", F(gpuP95, "%")))
            .Append(MetricCard("Worst ping loss", $"{worstLoss:F1}%"))
            .Append(MetricCard(
                "Max DPC / ISR",
                report.Etw.Collected ? $"{maxRoutine:F0} µs" : "not attributed"))
            .Append("</div>");
    }

    private static void AppendFindings(
        StringBuilder html,
        IReadOnlyList<Finding> findings)
    {
        html.Append("<h2>Evidence-ranked findings</h2><div class=\"findings\">");
        foreach (var finding in findings)
        {
            var severity = finding.Severity.ToString().ToLowerInvariant();
            html.Append("<article class=\"finding ")
                .Append(severity)
                .Append("\"><div class=\"meta\"><span class=\"severity ")
                .Append(severity)
                .Append("\">")
                .Append(E(finding.Severity.ToString()))
                .Append("</span> &nbsp; ")
                .Append(E(finding.Category))
                .Append(" · confidence ")
                .Append((finding.Confidence * 100).ToString("F0", CultureInfo.InvariantCulture))
                .Append("%</div><h3>")
                .Append(E(finding.Title))
                .Append("</h3><p><strong>Evidence:</strong> ")
                .Append(E(finding.Evidence))
                .Append("</p><p><strong>Meaning:</strong> ")
                .Append(E(finding.Interpretation))
                .Append("</p><p><strong>Next action:</strong> ")
                .Append(E(finding.Recommendation))
                .Append("</p></article>");
        }

        html.Append("</div>");
    }

    private static void AppendCharts(
        StringBuilder html,
        IReadOnlyList<PerformanceSample> samples)
    {
        html.Append("<h2>Timeline</h2><div class=\"charts\">")
            .Append(Chart(
                "CPU total",
                samples,
                sample => sample.CpuTotalPercent,
                "#49d6ff",
                100,
                "%"))
            .Append(Chart(
                "GPU busiest engine",
                samples,
                sample => sample.GpuBusyPercent,
                "#718cff",
                100,
                "%"))
            .Append(Chart(
                "Available memory",
                samples,
                sample => sample.AvailableMemoryMb,
                "#48d597",
                null,
                " MB"))
            .Append(Chart(
                "Disk queue",
                samples,
                sample => sample.DiskQueueLength,
                "#ffbd59",
                null,
                string.Empty))
            .Append("</div>");
    }

    private static void AppendSystemInventory(
        StringBuilder html,
        MachineInventory inventory)
    {
        html.Append("<h2>Machine inventory</h2><div class=\"two-col\">")
            .Append("<div class=\"card section-card\"><h3>Platform</h3>")
            .Append(KeyValue("Windows", $"{inventory.OperatingSystem.Caption} {inventory.OperatingSystem.BuildNumber}"))
            .Append(KeyValue("Computer", $"{inventory.Computer.Manufacturer} {inventory.Computer.Model}"))
            .Append(KeyValue("Motherboard", inventory.Computer.Motherboard))
            .Append(KeyValue("BIOS", $"{inventory.Computer.BiosVersion} · {inventory.Computer.BiosDate:yyyy-MM-dd}"))
            .Append(KeyValue(
                "Uptime start",
                inventory.OperatingSystem.LastBootAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "unknown"))
            .Append("</div><div class=\"card section-card\"><h3>CPU and memory</h3>")
            .Append(KeyValue(
                "CPU",
                string.Join("; ", inventory.Cpus.Select(
                    cpu => $"{cpu.Name} · {cpu.PhysicalCores}C/{cpu.LogicalProcessors}T"))))
            .Append(KeyValue(
                "Installed RAM",
                FormatBytes(inventory.Computer.TotalPhysicalMemoryBytes)))
            .Append(KeyValue(
                "DIMMs",
                string.Join("; ", inventory.MemoryModules.Select(
                    module =>
                        $"{FormatBytes(module.CapacityBytes)} {module.ConfiguredClockMhz} MT/s " +
                        $"{module.Manufacturer} {module.PartNumber}".Trim()))))
            .Append("</div><div class=\"card section-card\"><h3>Graphics</h3>")
            .Append(string.Join(
                string.Empty,
                inventory.Gpus.Select(gpu =>
                    KeyValue(
                        gpu.Name,
                        $"driver {gpu.DriverVersion}; " +
                        $"{(gpu.AdapterMemoryBytes is ulong bytes ? FormatBytes(bytes) : "VRAM unknown")}; " +
                        $"{(gpu.TemperatureCelsius is double temperature ? $"{temperature:F0} °C" : "temperature unavailable")}"))))
            .Append("</div><div class=\"card section-card\"><h3>Runtime configuration</h3>")
            .Append(KeyValue("Power plan", inventory.PowerAndGaming.ActivePowerPlan))
            .Append(KeyValue("HAGS", inventory.PowerAndGaming.HardwareGpuScheduling))
            .Append(KeyValue("Game Mode", inventory.PowerAndGaming.GameMode))
            .Append(KeyValue(
                "VBS",
                inventory.Security.VirtualizationBasedSecurityRunning?.ToString() ?? "unknown"))
            .Append(KeyValue(
                "Defender",
                $"enabled {inventory.Security.DefenderEnabled}; real-time " +
                $"{inventory.Security.DefenderRealTimeProtectionEnabled}; signatures " +
                $"{inventory.Security.DefenderSignatureAgeDays?.ToString(CultureInfo.InvariantCulture) ?? "?"} day(s) old"))
            .Append("</div></div>");

        html.Append("<h2>Storage</h2><div class=\"table-wrap\"><table><thead><tr>")
            .Append("<th>Disk</th><th>Interface / media</th><th>Size</th><th>Status</th>")
            .Append("<th>Volumes</th></tr></thead><tbody>");
        foreach (var disk in inventory.Disks)
        {
            html.Append("<tr><td>")
                .Append(E(disk.Model))
                .Append("</td><td>")
                .Append(E($"{disk.InterfaceType} / {disk.MediaType}"))
                .Append("</td><td>")
                .Append(E(FormatBytes(disk.SizeBytes)))
                .Append("</td><td>")
                .Append(E(disk.Status))
                .Append("</td><td>")
                .Append(E(string.Join(
                    "; ",
                    disk.Volumes.Select(volume =>
                        $"{volume.Name} {FormatBytes(volume.FreeBytes)} free of " +
                        $"{FormatBytes(volume.SizeBytes)}"))))
                .Append("</td></tr>");
        }

        html.Append("</tbody></table></div>");
    }

    private static void AppendProcesses(
        StringBuilder html,
        IReadOnlyList<ProcessSummary> processes,
        bool etwCollected)
    {
        var topCpu = processes
            .OrderByDescending(process => process.AverageCpuPercent)
            .Take(20)
            .ToArray();
        html.Append("<h2>Top processes by CPU</h2>")
            .Append(ProcessTable(topCpu, etwCollected));

        var topIo = processes
            .OrderByDescending(process =>
                etwCollected
                    ? (double)(process.EtwDiskReadBytes + process.EtwDiskWriteBytes)
                    : process.IoReadBytes + process.IoWriteBytes)
            .Take(20)
            .ToArray();
        html.Append("<h2>Top processes by I/O</h2>")
            .Append(ProcessTable(topIo, etwCollected));
    }

    private static string ProcessTable(
        IReadOnlyList<ProcessSummary> processes,
        bool etwCollected)
    {
        var table = new StringBuilder();
        table.Append("<div class=\"table-wrap\"><table><thead><tr>")
            .Append("<th>Process</th><th class=\"numeric\">PID</th>")
            .Append("<th class=\"numeric\">CPU avg / peak</th>")
            .Append("<th class=\"numeric\">Private max</th>")
            .Append("<th class=\"numeric\">Disk / I/O</th>")
            .Append("<th class=\"numeric\">Network</th>")
            .Append("<th class=\"numeric\">TCP / UDP endpoints</th>")
            .Append("</tr></thead><tbody>");
        foreach (var process in processes)
        {
            var diskBytes = etwCollected
                ? process.EtwDiskReadBytes + process.EtwDiskWriteBytes
                : ToLongSaturated(process.IoReadBytes + process.IoWriteBytes);
            var networkBytes =
                process.EtwNetworkSendBytes + process.EtwNetworkReceiveBytes;
            table.Append("<tr><td><strong>")
                .Append(E(process.Name))
                .Append("</strong><br><span class=\"muted\">")
                .Append(E(process.ExecutablePath ?? string.Empty))
                .Append("</span></td><td class=\"numeric\">")
                .Append(process.ProcessId)
                .Append("</td><td class=\"numeric\">")
                .Append(process.AverageCpuPercent.ToString("F1", CultureInfo.InvariantCulture))
                .Append("% / ")
                .Append(process.PeakCpuPercent.ToString("F1", CultureInfo.InvariantCulture))
                .Append("%</td><td class=\"numeric\">")
                .Append(E(FormatBytes(process.PeakPrivateBytes)))
                .Append("</td><td class=\"numeric\">")
                .Append(E(FormatBytes(diskBytes)))
                .Append(etwCollected ? string.Empty : " total I/O")
                .Append("</td><td class=\"numeric\">")
                .Append(etwCollected ? E(FormatBytes(networkBytes)) : "not attributed")
                .Append("</td><td class=\"numeric\">")
                .Append(process.TcpConnectionCount)
                .Append(" / ")
                .Append(process.UdpEndpointCount)
                .Append("</td></tr>");
        }

        table.Append("</tbody></table></div>");
        return table.ToString();
    }

    private static void AppendNetwork(StringBuilder html, NetworkSummary network)
    {
        html.Append("<h2>Network path</h2><div class=\"table-wrap\"><table><thead><tr>")
            .Append("<th>Target</th><th>Role</th><th class=\"numeric\">Replies</th>")
            .Append("<th class=\"numeric\">Loss</th><th class=\"numeric\">Average</th>")
            .Append("<th class=\"numeric\">Maximum</th><th class=\"numeric\">Jitter</th>")
            .Append("</tr></thead><tbody>");
        foreach (var target in network.PingTargets)
        {
            html.Append("<tr><td>")
                .Append(E(target.Target))
                .Append("</td><td>")
                .Append(E(target.Role))
                .Append("</td><td class=\"numeric\">")
                .Append(target.Received)
                .Append(" / ")
                .Append(target.Sent)
                .Append("</td><td class=\"numeric\">")
                .Append(target.LossPercent.ToString("F1", CultureInfo.InvariantCulture))
                .Append("%</td><td class=\"numeric\">")
                .Append(E(F(target.AverageMs, " ms")))
                .Append("</td><td class=\"numeric\">")
                .Append(E(F(target.MaximumMs, " ms")))
                .Append("</td><td class=\"numeric\">")
                .Append(E(F(target.JitterMs, " ms")))
                .Append("</td></tr>");
        }

        html.Append("</tbody></table></div><p class=\"muted\">TCP sent ")
            .Append(network.TcpSegmentsSent)
            .Append(" segments; retransmitted ")
            .Append(network.TcpSegmentsRetransmitted)
            .Append(" (")
            .Append(F(network.TcpRetransmitPercent, "%", 2))
            .Append("). Adapter traffic: ")
            .Append(E(FormatBytes(network.BytesReceived)))
            .Append(" received, ")
            .Append(E(FormatBytes(network.BytesSent)))
            .Append(" sent.</p>");
    }

    private static void AppendEtw(StringBuilder html, EtwSummary etw)
    {
        html.Append("<h2>Kernel driver latency</h2>");
        if (!etw.Collected)
        {
            html.Append("<div class=\"card\"><strong>Not collected.</strong><br><span class=\"muted\">")
                .Append(E(etw.UnavailableReason ?? "No ETW data."))
                .Append("</span></div>");
            return;
        }

        html.Append("<div class=\"grid\">")
            .Append(MetricCard("DPC count", etw.DpcCount.ToString("N0", CultureInfo.InvariantCulture)))
            .Append(MetricCard("ISR count", etw.IsrCount.ToString("N0", CultureInfo.InvariantCulture)))
            .Append(MetricCard("Maximum DPC", F(etw.MaximumDpcMicroseconds, " µs", 0)))
            .Append(MetricCard("Maximum ISR", F(etw.MaximumIsrMicroseconds, " µs", 0)))
            .Append("</div><div class=\"table-wrap\" style=\"margin-top:12px\"><table><thead><tr>")
            .Append("<th>Driver / image</th><th class=\"numeric\">DPC</th>")
            .Append("<th class=\"numeric\">ISR</th><th class=\"numeric\">Total execution</th>")
            .Append("<th class=\"numeric\">Maximum routine</th></tr></thead><tbody>");
        foreach (var driver in etw.DriverLatency)
        {
            html.Append("<tr><td>")
                .Append(E(driver.Driver))
                .Append("</td><td class=\"numeric\">")
                .Append(driver.DpcCount)
                .Append("</td><td class=\"numeric\">")
                .Append(driver.IsrCount)
                .Append("</td><td class=\"numeric\">")
                .Append(driver.TotalExecutionMicroseconds.ToString("F0", CultureInfo.InvariantCulture))
                .Append(" µs</td><td class=\"numeric\">")
                .Append(driver.MaximumExecutionMicroseconds.ToString("F1", CultureInfo.InvariantCulture))
                .Append(" µs</td></tr>");
        }

        html.Append("</tbody></table></div>");
    }

    private static void AppendDriversAndEvents(
        StringBuilder html,
        MachineInventory inventory)
    {
        html.Append("<h2>Relevant drivers</h2><div class=\"table-wrap\"><table><thead><tr>")
            .Append("<th>Device</th><th>Class</th><th>Provider</th><th>Version</th>")
            .Append("<th>Date</th><th class=\"numeric\">Problem code</th>")
            .Append("</tr></thead><tbody>");
        foreach (var driver in inventory.RelevantDrivers.Take(80))
        {
            html.Append("<tr><td>")
                .Append(E(driver.DeviceName))
                .Append("</td><td>")
                .Append(E(driver.DeviceClass))
                .Append("</td><td>")
                .Append(E(driver.DriverProvider))
                .Append("</td><td>")
                .Append(E(driver.DriverVersion))
                .Append("</td><td>")
                .Append(E(driver.DriverDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty))
                .Append("</td><td class=\"numeric\">")
                .Append(driver.ProblemCode)
                .Append("</td></tr>");
        }

        html.Append("</tbody></table></div><h2>Recent System log errors and warnings</h2>")
            .Append("<div class=\"table-wrap\"><table><thead><tr><th>Time</th>")
            .Append("<th>Provider / ID</th><th>Level</th><th>Message</th>")
            .Append("</tr></thead><tbody>");
        foreach (var item in inventory.RecentSystemEvents)
        {
            html.Append("<tr><td>")
                .Append(E(item.Timestamp?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty))
                .Append("</td><td>")
                .Append(E($"{item.Provider} / {item.EventId}"))
                .Append("</td><td>")
                .Append(E(item.Level))
                .Append("</td><td>")
                .Append(E(item.Message))
                .Append("</td></tr>");
        }

        html.Append("</tbody></table></div>");
    }

    private static void AppendNotices(
        StringBuilder html,
        IReadOnlyList<CollectorNotice> notices)
    {
        if (notices.Count == 0)
        {
            return;
        }

        html.Append("<h2>Collector notices</h2><div class=\"card section-card\"><ul>");
        foreach (var notice in notices)
        {
            html.Append("<li><strong>")
                .Append(E(notice.Collector))
                .Append(":</strong> ")
                .Append(E(notice.Message))
                .Append("</li>");
        }

        html.Append("</ul></div>");
    }

    private static string Chart(
        string title,
        IReadOnlyList<PerformanceSample> samples,
        Func<PerformanceSample, double?> selector,
        string color,
        double? fixedMaximum,
        string unit)
    {
        var values = samples
            .Select(sample => selector(sample) is double value && double.IsFinite(value)
                ? value
                : 0)
            .ToArray();
        var maximum = fixedMaximum ??
                      Math.Max(1, values.DefaultIfEmpty().Max() * 1.1);
        const double width = 800;
        const double height = 150;
        var points = values.Length == 0
            ? string.Empty
            : string.Join(
                " ",
                values.Select((value, index) =>
                {
                    var x = values.Length == 1
                        ? 0
                        : index * width / (values.Length - 1);
                    var y = height - (Math.Clamp(value, 0, maximum) / maximum * height);
                    return string.Create(
                        CultureInfo.InvariantCulture,
                        $"{x:F1},{y:F1}");
                }));
        var last = values.LastOrDefault();

        return string.Create(
            CultureInfo.InvariantCulture,
            $"""
             <div class="chart"><h3>{E(title)}</h3>
             <svg viewBox="0 0 800 160" preserveAspectRatio="none" role="img" aria-label="{E(title)} timeline">
               <defs><linearGradient id="g{title.GetHashCode():X}" x1="0" y1="0" x2="0" y2="1">
                 <stop offset="0" stop-color="{color}" stop-opacity=".34"/>
                 <stop offset="1" stop-color="{color}" stop-opacity="0"/>
               </linearGradient></defs>
               <path d="M 0 150 L {points.Replace(" ", " L ", StringComparison.Ordinal)} L 800 150 Z" fill="url(#g{title.GetHashCode():X})"/>
               <polyline points="{points}" fill="none" stroke="{color}" stroke-width="3" vector-effect="non-scaling-stroke"/>
               <line x1="0" y1="150" x2="800" y2="150" stroke="#273449"/>
             </svg>
             <div class="axis"><span>0 s</span><strong>{last:F1}{E(unit)}</strong><span>{(samples.Count > 0 ? samples[^1].ElapsedSeconds : 0):F0} s</span></div>
             </div>
             """);
    }

    private static string Badge(string text) =>
        $"<span class=\"badge\">{E(text)}</span>";

    private static string MetricCard(string label, string value) =>
        $"<div class=\"card\"><div class=\"label\">{E(label)}</div>" +
        $"<div class=\"value\">{E(value)}</div></div>";

    private static string KeyValue(string key, string value) =>
        $"<p><strong>{E(key)}:</strong> {E(value)}</p>";

    private static string F(double? value, string suffix, int decimals = 1) =>
        value is double number && double.IsFinite(number)
            ? $"{number.ToString($"F{decimals}", CultureInfo.InvariantCulture)}{suffix}"
            : "n/a";

    private static string FormatBytes(long bytes) =>
        FormatBytes(bytes < 0 ? 0UL : (ulong)bytes);

    private static string FormatBytes(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value.ToString(unit == 0 ? "F0" : "F1", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private static long ToLongSaturated(ulong value) =>
        value > long.MaxValue ? long.MaxValue : (long)value;

    private static string E(string value) => HtmlEncoder.Default.Encode(value ?? string.Empty);

    private static string SanitizeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(
            value.Trim()
                .Select(character => invalid.Contains(character) ? '-' : character)
                .ToArray());
        return sanitized.Length <= 40 ? sanitized : sanitized[..40];
    }

    private static async Task WriteAtomicallyAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

public sealed record ReportPaths(string HtmlPath, string JsonPath);

using LeanPlay.Analyzer.Analysis;
using LeanPlay.Analyzer.Model;

namespace LeanPlay.Analyzer.Tests;

public sealed class FindingAnalyzerTests
{
    [Fact]
    public void CpuSaturationRequiresLoadAndQueueEvidence()
    {
        var samples = Enumerable.Range(1, 20)
            .Select(index => ReportFixture.Sample(index, cpu: 94, queue: 5))
            .ToArray();

        var findings = Analyze(samples);

        var finding = Assert.Single(
            findings,
            item => item.Id == "cpu.saturation");
        Assert.Equal(FindingSeverity.Critical, finding.Severity);
        Assert.Contains("processor queue", finding.Evidence);
    }

    [Fact]
    public void MemoryPressureIsCritical()
    {
        var samples = Enumerable.Range(1, 20)
            .Select(index => ReportFixture.Sample(
                index,
                memoryMb: 500,
                commit: 98,
                pagesInput: 200))
            .ToArray();

        var finding = Assert.Single(
            Analyze(samples),
            item => item.Id == "memory.pressure");

        Assert.Equal(FindingSeverity.Critical, finding.Severity);
    }

    [Fact]
    public void WheaEventTakesPriorityOverOptimization()
    {
        var inventory = ReportFixture.Inventory(
            new[]
            {
                new SystemEventInfo(
                    DateTimeOffset.UtcNow,
                    "Microsoft-Windows-WHEA-Logger",
                    18,
                    "Error",
                    "A fatal hardware error occurred.")
            });

        var finding = Assert.Single(
            Analyze(
                Enumerable.Range(1, 10)
                    .Select(index => ReportFixture.Sample(index))
                    .ToArray(),
                inventory),
            item => item.Id == "reliability.whea");

        Assert.Equal(FindingSeverity.Critical, finding.Severity);
        Assert.Contains("known-stable baseline", finding.Recommendation);
    }

    [Fact]
    public void GatewayLossIsCriticalAndLocalizedToLan()
    {
        var network = new NetworkSummary(
            new[]
            {
                new PingTargetSummary(
                    "192.168.1.1",
                    "Default gateway",
                    20,
                    17,
                    15,
                    1,
                    3,
                    80,
                    20,
                    null)
            },
            0,
            0,
            0,
            0,
            null);

        var finding = Assert.Single(
            FindingAnalyzer.Analyze(
                ReportFixture.Inventory(),
                Enumerable.Range(1, 10)
                    .Select(index => ReportFixture.Sample(index))
                    .ToArray(),
                Array.Empty<ProcessSummary>(),
                network,
                ReportFixture.EmptyEtw()),
            item => item.Id.StartsWith(
                "network.quality",
                StringComparison.Ordinal));

        Assert.Equal(FindingSeverity.Critical, finding.Severity);
        Assert.Contains("LAN", finding.Interpretation);
    }

    [Fact]
    public void UdpPortExhaustionEventIsCritical()
    {
        var inventory = ReportFixture.Inventory(
            new[]
            {
                new SystemEventInfo(
                    DateTimeOffset.UtcNow,
                    "Tcpip",
                    4266,
                    "Warning",
                    "The global UDP port space is exhausted.")
            });

        var finding = Assert.Single(
            Analyze(
                Enumerable.Range(1, 10)
                    .Select(index => ReportFixture.Sample(index))
                    .ToArray(),
                inventory),
            item => item.Id == "network.udp-port-exhaustion");

        Assert.Equal(FindingSeverity.Critical, finding.Severity);
        Assert.Contains("Restart first", finding.Recommendation);
    }

    [Fact]
    public void RepeatedIntelLinkDisconnectsAreReported()
    {
        var events = Enumerable.Range(1, 3)
            .Select(
                _ => new SystemEventInfo(
                    DateTimeOffset.UtcNow,
                    "e2fnexpress",
                    27,
                    "Warning",
                    "Network link is disconnected."))
            .ToArray();

        var finding = Assert.Single(
            Analyze(
                Enumerable.Range(1, 10)
                    .Select(index => ReportFixture.Sample(index))
                    .ToArray(),
                ReportFixture.Inventory(events)),
            item => item.Id == "network.link-disconnects");

        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Contains("cable/router port", finding.Recommendation);
    }

    private static IReadOnlyList<Finding> Analyze(
        IReadOnlyList<PerformanceSample> samples,
        MachineInventory? inventory = null) =>
        FindingAnalyzer.Analyze(
            inventory ?? ReportFixture.Inventory(),
            samples,
            Array.Empty<ProcessSummary>(),
            ReportFixture.HealthyNetwork(),
            ReportFixture.EmptyEtw());
}

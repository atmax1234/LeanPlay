using LeanPlay.Analyzer.Model;

namespace LeanPlay.Analyzer.Tests;

internal static class ReportFixture
{
    public static MachineInventory Inventory(
        IReadOnlyList<SystemEventInfo>? events = null) =>
        new(
            IsAdministrator: false,
            new OperatingSystemInfo(
                "Windows 11 Pro",
                "10.0.26200",
                "26200",
                DateTimeOffset.UtcNow.AddYears(-1),
                DateTimeOffset.UtcNow.AddHours(-1),
                "64-bit"),
            new ComputerInfo(
                "Example",
                "Gaming PC",
                "Example Board",
                "1.0",
                DateTimeOffset.UtcNow.AddYears(-1),
                32UL * 1024 * 1024 * 1024),
            new[]
            {
                new CpuInfo("Example CPU", 8, 16, 5000, "AM5")
            },
            new[]
            {
                new GpuInfo(
                    "Example GPU",
                    "1.0",
                    DateTimeOffset.UtcNow,
                    12UL * 1024 * 1024 * 1024,
                    "Example",
                    50,
                    12 * 1024,
                    30)
            },
            Array.Empty<MemoryModuleInfo>(),
            new[]
            {
                new DiskInfo(
                    "Example NVMe",
                    "NVMe",
                    "SSD",
                    1024UL * 1024 * 1024 * 1024,
                    "OK",
                    "1.0",
                    new[]
                    {
                        new VolumeInfo(
                            "C:",
                            "NTFS",
                            1024UL * 1024 * 1024 * 1024,
                            500UL * 1024 * 1024 * 1024)
                    })
            },
            Array.Empty<NetworkAdapterInfo>(),
            Array.Empty<DriverInfo>(),
            new PowerAndGamingInfo(
                "Balanced",
                Guid.Empty.ToString(),
                "Windows default",
                "Enabled",
                null,
                PendingReboot: false),
            new SecurityInfo(
                true,
                true,
                0,
                false,
                Array.Empty<string>()),
            new WorkloadInventory(
                100,
                80,
                60,
                Array.Empty<string>(),
                Array.Empty<string>()),
            events ?? Array.Empty<SystemEventInfo>());

    public static PerformanceSample Sample(
        int index,
        double cpu = 20,
        double queue = 0,
        double memoryMb = 16000,
        double commit = 40,
        double pagesInput = 0,
        double diskActive = 10,
        double diskQueue = 0,
        double diskLatencyMs = 1,
        double dpc = 0.1,
        double interrupt = 0.1) =>
        new(
            DateTimeOffset.UtcNow.AddSeconds(index),
            index,
            cpu,
            cpu / 3,
            dpc,
            interrupt,
            100,
            queue,
            memoryMb,
            commit,
            pagesInput,
            diskActive,
            diskQueue,
            1024,
            diskLatencyMs,
            diskLatencyMs,
            1024,
            10);

    public static NetworkSummary HealthyNetwork() =>
        new(
            new[]
            {
                new PingTargetSummary(
                    "192.168.1.1",
                    "Default gateway",
                    10,
                    10,
                    0,
                    1,
                    1,
                    2,
                    0.2,
                    null)
            },
            1000,
            1000,
            100,
            0,
            0);

    public static EtwSummary EmptyEtw() =>
        new(
            Requested: false,
            Collected: false,
            "disabled",
            0,
            0,
            0,
            0,
            0,
            0,
            null,
            null,
            Array.Empty<DriverLatencySummary>());
}

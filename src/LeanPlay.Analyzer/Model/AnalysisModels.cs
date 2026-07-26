namespace LeanPlay.Analyzer.Model;

public sealed record AnalysisOptions(
    TimeSpan Duration,
    TimeSpan SampleInterval,
    string OutputDirectory,
    IReadOnlyList<string> PublicPingTargets,
    bool IncludeEtw,
    bool OpenReport,
    string? WorkloadLabel);

public sealed record AnalysisReport(
    string SchemaVersion,
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    AnalysisOptionsSnapshot Options,
    MachineInventory Inventory,
    IReadOnlyList<PerformanceSample> Samples,
    IReadOnlyList<ProcessSummary> Processes,
    NetworkSummary Network,
    EtwSummary Etw,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<CollectorNotice> Notices);

public sealed record AnalysisOptionsSnapshot(
    double DurationSeconds,
    double SampleIntervalSeconds,
    bool EtwRequested,
    string? WorkloadLabel);

public sealed record MachineInventory(
    bool IsAdministrator,
    OperatingSystemInfo OperatingSystem,
    ComputerInfo Computer,
    IReadOnlyList<CpuInfo> Cpus,
    IReadOnlyList<GpuInfo> Gpus,
    IReadOnlyList<MemoryModuleInfo> MemoryModules,
    IReadOnlyList<DiskInfo> Disks,
    IReadOnlyList<NetworkAdapterInfo> NetworkAdapters,
    IReadOnlyList<DriverInfo> RelevantDrivers,
    PowerAndGamingInfo PowerAndGaming,
    SecurityInfo Security,
    WorkloadInventory Workload,
    IReadOnlyList<SystemEventInfo> RecentSystemEvents);

public sealed record OperatingSystemInfo(
    string Caption,
    string Version,
    string BuildNumber,
    DateTimeOffset? InstalledAt,
    DateTimeOffset? LastBootAt,
    string Architecture);

public sealed record ComputerInfo(
    string Manufacturer,
    string Model,
    string Motherboard,
    string BiosVersion,
    DateTimeOffset? BiosDate,
    ulong TotalPhysicalMemoryBytes);

public sealed record CpuInfo(
    string Name,
    int PhysicalCores,
    int LogicalProcessors,
    uint MaxClockMhz,
    string Socket);

public sealed record GpuInfo(
    string Name,
    string DriverVersion,
    DateTimeOffset? DriverDate,
    ulong? AdapterMemoryBytes,
    string VideoProcessor,
    double? TemperatureCelsius,
    double? DedicatedMemoryMb,
    double? PowerWatts);

public sealed record MemoryModuleInfo(
    string Manufacturer,
    string PartNumber,
    ulong CapacityBytes,
    uint ConfiguredClockMhz,
    uint SpeedMhz);

public sealed record DiskInfo(
    string Model,
    string InterfaceType,
    string MediaType,
    ulong SizeBytes,
    string Status,
    string? FirmwareRevision,
    IReadOnlyList<VolumeInfo> Volumes);

public sealed record VolumeInfo(
    string Name,
    string FileSystem,
    ulong SizeBytes,
    ulong FreeBytes);

public sealed record NetworkAdapterInfo(
    string Name,
    string Description,
    string MacAddress,
    long LinkSpeedBitsPerSecond,
    IReadOnlyList<string> Addresses,
    IReadOnlyList<string> Gateways,
    IReadOnlyList<string> DnsServers,
    string DriverVersion,
    DateTimeOffset? DriverDate);

public sealed record DriverInfo(
    string DeviceName,
    string DeviceClass,
    string Manufacturer,
    string DriverProvider,
    string DriverVersion,
    DateTimeOffset? DriverDate,
    uint ProblemCode);

public sealed record PowerAndGamingInfo(
    string ActivePowerPlan,
    string ActivePowerPlanGuid,
    string HardwareGpuScheduling,
    string GameMode,
    bool? VariableRefreshRateEnabled,
    bool PendingReboot);

public sealed record SecurityInfo(
    bool? DefenderEnabled,
    bool? DefenderRealTimeProtectionEnabled,
    int? DefenderSignatureAgeDays,
    bool? VirtualizationBasedSecurityRunning,
    IReadOnlyList<string> RunningSecurityServices);

public sealed record WorkloadInventory(
    int RunningProcessCount,
    int RunningServiceCount,
    int AutomaticServiceCount,
    IReadOnlyList<string> ActiveOverlaysAndCaptureTools,
    IReadOnlyList<string> ActiveCloudSyncAndLaunchers);

public sealed record SystemEventInfo(
    DateTimeOffset? Timestamp,
    string Provider,
    int EventId,
    string Level,
    string Message);

public sealed record PerformanceSample(
    DateTimeOffset Timestamp,
    double ElapsedSeconds,
    double? CpuTotalPercent,
    double? CpuPrivilegedPercent,
    double? CpuDpcPercent,
    double? CpuInterruptPercent,
    double? CpuPerformancePercent,
    double? ProcessorQueueLength,
    double? AvailableMemoryMb,
    double? CommittedMemoryPercent,
    double? PagesInputPerSecond,
    double? DiskActivePercent,
    double? DiskQueueLength,
    double? DiskBytesPerSecond,
    double? DiskReadLatencyMs,
    double? DiskWriteLatencyMs,
    double? NetworkBytesPerSecond,
    double? GpuBusyPercent);

public sealed record ProcessSummary(
    int ProcessId,
    string Name,
    string? ExecutablePath,
    int Samples,
    double AverageCpuPercent,
    double PeakCpuPercent,
    double AverageIoBytesPerSecond,
    double PeakIoBytesPerSecond,
    ulong IoReadBytes,
    ulong IoWriteBytes,
    long PeakWorkingSetBytes,
    long PeakPrivateBytes,
    int PeakThreadCount,
    long EtwDiskReadBytes,
    long EtwDiskWriteBytes,
    long EtwNetworkSendBytes,
    long EtwNetworkReceiveBytes,
    int TcpConnectionCount,
    int UdpEndpointCount);

public sealed record NetworkSummary(
    IReadOnlyList<PingTargetSummary> PingTargets,
    long BytesReceived,
    long BytesSent,
    long TcpSegmentsSent,
    long TcpSegmentsRetransmitted,
    double? TcpRetransmitPercent);

public sealed record PingTargetSummary(
    string Target,
    string Role,
    int Sent,
    int Received,
    double LossPercent,
    double? MinimumMs,
    double? AverageMs,
    double? MaximumMs,
    double? JitterMs,
    string? Error);

public sealed record EtwSummary(
    bool Requested,
    bool Collected,
    string? UnavailableReason,
    long DiskReadBytes,
    long DiskWriteBytes,
    long NetworkSendBytes,
    long NetworkReceiveBytes,
    long DpcCount,
    long IsrCount,
    double? MaximumDpcMicroseconds,
    double? MaximumIsrMicroseconds,
    IReadOnlyList<DriverLatencySummary> DriverLatency);

public sealed record DriverLatencySummary(
    string Driver,
    long DpcCount,
    long IsrCount,
    double TotalExecutionMicroseconds,
    double MaximumExecutionMicroseconds);

public enum FindingSeverity
{
    Good = 0,
    Information = 1,
    Warning = 2,
    Critical = 3
}

public sealed record Finding(
    string Id,
    FindingSeverity Severity,
    string Category,
    string Title,
    string Evidence,
    string Interpretation,
    string Recommendation,
    double Confidence);

public sealed record CollectorNotice(
    string Collector,
    string Level,
    string Message);

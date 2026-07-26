using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using LeanPlay.Analyzer.Model;
using Microsoft.Win32;

namespace LeanPlay.Analyzer.Collectors;

public sealed class InventoryCollector
{
    private static readonly HashSet<string> RelevantDriverClasses = new(
        new[] { "DISPLAY", "NET", "HDC", "SCSIADAPTER", "MEDIA", "USB" },
        StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<uint, string> SecurityServiceNames = new()
    {
        [1] = "Credential Guard",
        [2] = "Memory Integrity (HVCI)",
        [3] = "System Guard Secure Launch",
        [4] = "SMM Firmware Measurement"
    };
    private static readonly string[] OverlayProcessNames =
    {
        "Discord", "GameBar", "GameBarFTServer", "NVIDIA Share", "obs64",
        "Medal", "Overwolf", "RTSS", "MSIAfterburner"
    };
    private static readonly string[] BackgroundProcessNames =
    {
        "OneDrive", "Dropbox", "GoogleDriveFS", "steam", "steamwebhelper",
        "EpicGamesLauncher", "Battle.net", "RiotClientServices", "CCleaner64"
    };
    private static readonly char[] LineSeparators = { '\r', '\n' };

    private readonly List<CollectorNotice> _notices;

    public InventoryCollector(List<CollectorNotice> notices)
    {
        _notices = notices;
    }

    public Task<MachineInventory> CollectAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Collect(cancellationToken), cancellationToken);

    private MachineInventory Collect(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var nvidia = QueryNvidiaSmi();
        var operatingSystem = QueryOperatingSystem();
        var computer = QueryComputer();
        var cpus = QueryCpus();
        var drivers = QueryDrivers();
        var gpus = QueryGpus(nvidia, drivers);
        var memory = QueryMemoryModules();
        var disks = QueryDisks();
        var adapters = QueryNetworkAdapters(drivers);
        var power = QueryPowerAndGaming();
        var security = QuerySecurity();
        var workload = QueryWorkload();
        var recentEvents = QueryRecentEvents();

        return new MachineInventory(
            IsAdministrator(),
            operatingSystem,
            computer,
            cpus,
            gpus,
            memory,
            disks,
            adapters,
            drivers,
            power,
            security,
            workload,
            recentEvents);
    }

    private OperatingSystemInfo QueryOperatingSystem()
    {
        using var item = First("root\\cimv2", "SELECT * FROM Win32_OperatingSystem");
        if (item is null)
        {
            Notice("WMI", "warning", "Win32_OperatingSystem returned no data.");
            return new OperatingSystemInfo(
                Environment.OSVersion.VersionString,
                Environment.OSVersion.Version.ToString(),
                Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture),
                null,
                null,
                Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit");
        }

        return new OperatingSystemInfo(
            WmiValue.Text(item, "Caption"),
            WmiValue.Text(item, "Version"),
            WmiValue.Text(item, "BuildNumber"),
            WmiValue.DateTime(item, "InstallDate"),
            WmiValue.DateTime(item, "LastBootUpTime"),
            WmiValue.Text(item, "OSArchitecture"));
    }

    private ComputerInfo QueryComputer()
    {
        using var system = First("root\\cimv2", "SELECT * FROM Win32_ComputerSystem");
        using var board = First("root\\cimv2", "SELECT * FROM Win32_BaseBoard");
        using var bios = First("root\\cimv2", "SELECT * FROM Win32_BIOS");

        return new ComputerInfo(
            system is null ? string.Empty : WmiValue.Text(system, "Manufacturer"),
            system is null ? string.Empty : WmiValue.Text(system, "Model"),
            board is null
                ? string.Empty
                : $"{WmiValue.Text(board, "Manufacturer")} {WmiValue.Text(board, "Product")}".Trim(),
            bios is null ? string.Empty : WmiValue.Text(bios, "SMBIOSBIOSVersion"),
            bios is null ? null : WmiValue.DateTime(bios, "ReleaseDate"),
            system is null ? 0 : WmiValue.UInt64(system, "TotalPhysicalMemory"));
    }

    private CpuInfo[] QueryCpus() =>
        Query("root\\cimv2", "SELECT * FROM Win32_Processor")
            .Select(item => new CpuInfo(
                WmiValue.Text(item, "Name"),
                WmiValue.Int32(item, "NumberOfCores"),
                WmiValue.Int32(item, "NumberOfLogicalProcessors"),
                WmiValue.UInt32(item, "MaxClockSpeed"),
                WmiValue.Text(item, "SocketDesignation")))
            .ToArray();

    private List<GpuInfo> QueryGpus(
        IReadOnlyList<NvidiaSmiRow> nvidiaRows,
        IReadOnlyList<DriverInfo> drivers)
    {
        var result = new List<GpuInfo>();
        foreach (var item in Query("root\\cimv2", "SELECT * FROM Win32_VideoController"))
        {
            var name = WmiValue.Text(item, "Name");
            var nvidia = nvidiaRows.FirstOrDefault(row =>
                name.Contains(row.Name, StringComparison.OrdinalIgnoreCase) ||
                row.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            var driver = drivers.FirstOrDefault(candidate =>
                string.Equals(candidate.DeviceClass, "DISPLAY", StringComparison.OrdinalIgnoreCase) &&
                (candidate.DeviceName.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                 name.Contains(candidate.DeviceName, StringComparison.OrdinalIgnoreCase)));

            var adapterBytes = WmiValue.UInt64(item, "AdapterRAM");
            result.Add(
                new GpuInfo(
                    name,
                    string.IsNullOrWhiteSpace(nvidia?.DriverVersion)
                        ? WmiValue.Text(item, "DriverVersion")
                        : nvidia.DriverVersion,
                    driver?.DriverDate,
                    nvidia?.MemoryMb is double memoryMb
                        ? (ulong)(memoryMb * 1024 * 1024)
                        : adapterBytes == 0 ? null : adapterBytes,
                    WmiValue.Text(item, "VideoProcessor"),
                    nvidia?.TemperatureCelsius,
                    nvidia?.MemoryMb,
                    nvidia?.PowerWatts));
        }

        return result;
    }

    private MemoryModuleInfo[] QueryMemoryModules() =>
        Query("root\\cimv2", "SELECT * FROM Win32_PhysicalMemory")
            .Select(item => new MemoryModuleInfo(
                WmiValue.Text(item, "Manufacturer"),
                WmiValue.Text(item, "PartNumber"),
                WmiValue.UInt64(item, "Capacity"),
                WmiValue.UInt32(item, "ConfiguredClockSpeed"),
                WmiValue.UInt32(item, "Speed")))
            .ToArray();

    private List<DiskInfo> QueryDisks()
    {
        var result = new List<DiskInfo>();
        foreach (var item in QueryObjects("root\\cimv2", "SELECT * FROM Win32_DiskDrive"))
        {
            using (item)
            {
                var volumes = new List<VolumeInfo>();
                try
                {
                    foreach (ManagementObject partition in item.GetRelated(
                                 "Win32_DiskPartition"))
                    {
                        using (partition)
                        {
                            foreach (ManagementObject volume in partition.GetRelated(
                                         "Win32_LogicalDisk"))
                            {
                                using (volume)
                                {
                                    volumes.Add(
                                        new VolumeInfo(
                                            WmiValue.Text(volume, "DeviceID"),
                                            WmiValue.Text(volume, "FileSystem"),
                                            WmiValue.UInt64(volume, "Size"),
                                            WmiValue.UInt64(volume, "FreeSpace")));
                                }
                            }
                        }
                    }
                }
                catch (ManagementException exception)
                {
                    Notice("Storage inventory", "information", exception.Message);
                }

                result.Add(
                    new DiskInfo(
                        WmiValue.Text(item, "Model"),
                        WmiValue.Text(item, "InterfaceType"),
                        WmiValue.Text(item, "MediaType"),
                        WmiValue.UInt64(item, "Size"),
                        WmiValue.Text(item, "Status"),
                        WmiValue.Text(item, "FirmwareRevision"),
                        volumes));
            }
        }

        return result;
    }

    private List<NetworkAdapterInfo> QueryNetworkAdapters(
        IReadOnlyList<DriverInfo> drivers)
    {
        var result = new List<NetworkAdapterInfo>();
        foreach (var configuration in Query(
                     "root\\cimv2",
                     "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE"))
        {
            var index = WmiValue.UInt32(configuration, "Index");
            using var adapter = First(
                "root\\cimv2",
                $"SELECT * FROM Win32_NetworkAdapter WHERE Index = {index}");
            var description = WmiValue.Text(configuration, "Description");
            var driver = drivers.FirstOrDefault(candidate =>
                string.Equals(candidate.DeviceClass, "NET", StringComparison.OrdinalIgnoreCase) &&
                (description.Contains(candidate.DeviceName, StringComparison.OrdinalIgnoreCase) ||
                 candidate.DeviceName.Contains(description, StringComparison.OrdinalIgnoreCase)));

            result.Add(
                new NetworkAdapterInfo(
                    adapter is null ? description : WmiValue.Text(adapter, "Name"),
                    description,
                    WmiValue.Text(configuration, "MACAddress"),
                    adapter is null ? 0 : (long)WmiValue.UInt64(adapter, "Speed"),
                    WmiValue.TextArray(configuration, "IPAddress"),
                    WmiValue.TextArray(configuration, "DefaultIPGateway"),
                    WmiValue.TextArray(configuration, "DNSServerSearchOrder"),
                    driver?.DriverVersion ?? string.Empty,
                    driver?.DriverDate));
        }

        return result;
    }

    private DriverInfo[] QueryDrivers()
    {
        var problemCodes = Query(
                "root\\cimv2",
                "SELECT * FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0")
            .ToDictionary(
                item => WmiValue.Text(item, "DeviceID"),
                item => WmiValue.UInt32(item, "ConfigManagerErrorCode"),
                StringComparer.OrdinalIgnoreCase);

        return Query("root\\cimv2", "SELECT * FROM Win32_PnPSignedDriver")
            .Where(item =>
            {
                var deviceClass = WmiValue.Text(item, "DeviceClass");
                var deviceId = WmiValue.Text(item, "DeviceID");
                return RelevantDriverClasses.Contains(deviceClass) ||
                       problemCodes.ContainsKey(deviceId);
            })
            .Select(item =>
            {
                var deviceId = WmiValue.Text(item, "DeviceID");
                return new DriverInfo(
                    WmiValue.Text(item, "DeviceName"),
                    WmiValue.Text(item, "DeviceClass"),
                    WmiValue.Text(item, "Manufacturer"),
                    WmiValue.Text(item, "DriverProviderName"),
                    WmiValue.Text(item, "DriverVersion"),
                    WmiValue.DateTime(item, "DriverDate"),
                    problemCodes.GetValueOrDefault(deviceId));
            })
            .OrderByDescending(driver => driver.ProblemCode != 0)
            .ThenBy(driver => driver.DeviceClass, StringComparer.OrdinalIgnoreCase)
            .ThenBy(driver => driver.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private PowerAndGamingInfo QueryPowerAndGaming()
    {
        string planName = string.Empty;
        string planGuid = string.Empty;
        try
        {
            using var plan = First(
                "root\\cimv2\\power",
                "SELECT * FROM Win32_PowerPlan WHERE IsActive = TRUE");
            if (plan is not null)
            {
                planName = WmiValue.Text(plan, "ElementName");
                var instanceId = WmiValue.Text(plan, "InstanceID");
                var open = instanceId.IndexOf('{', StringComparison.Ordinal);
                var close = instanceId.IndexOf('}', StringComparison.Ordinal);
                if (open >= 0 && close > open)
                {
                    planGuid = instanceId[(open + 1)..close];
                }
            }
        }
        catch (ManagementException exception)
        {
            Notice("Power inventory", "information", exception.Message);
        }

        if (string.IsNullOrWhiteSpace(planName))
        {
            (planName, planGuid) = QueryActivePowerPlan();
        }

        using var graphics = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
        var hagsValue = graphics?.GetValue("HwSchMode");
        var hags = hagsValue switch
        {
            2 => "Enabled",
            1 => "Disabled",
            _ => "Windows default / not explicitly configured"
        };

        using var gameBar = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\GameBar");
        var gameModeValue = gameBar?.GetValue("AutoGameModeEnabled") ??
                            gameBar?.GetValue("AllowAutoGameMode");
        var gameMode = gameModeValue switch
        {
            1 => "Enabled",
            0 => "Disabled",
            _ => "Windows default / not explicitly configured"
        };

        using var gameConfig = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\DirectX\UserGpuPreferences");
        bool? variableRefreshRate = null;
        if (gameConfig?.GetValue("DirectXUserGlobalSettings") is string directXSettings)
        {
            variableRefreshRate = directXSettings.Contains(
                "VRROptimizeEnable=1",
                StringComparison.OrdinalIgnoreCase);
        }

        return new PowerAndGamingInfo(
            planName,
            planGuid,
            hags,
            gameMode,
            variableRefreshRate,
            IsPendingReboot());
    }

    private SecurityInfo QuerySecurity()
    {
        bool? defenderEnabled = null;
        bool? realTimeProtection = null;
        int? signatureAge = null;
        try
        {
            using var defender = First(
                "root\\Microsoft\\Windows\\Defender",
                "SELECT * FROM MSFT_MpComputerStatus");
            if (defender is not null)
            {
                defenderEnabled = WmiValue.Boolean(defender, "AntivirusEnabled");
                realTimeProtection = WmiValue.Boolean(
                    defender,
                    "RealTimeProtectionEnabled");
                signatureAge = WmiValue.Int32(defender, "AntivirusSignatureAge");
            }
        }
        catch (ManagementException exception)
        {
            Notice("Defender inventory", "information", exception.Message);
        }

        bool? vbsRunning = null;
        var runningSecurityServices = new List<string>();
        try
        {
            using var deviceGuard = First(
                "root\\Microsoft\\Windows\\DeviceGuard",
                "SELECT * FROM Win32_DeviceGuard");
            if (deviceGuard is not null)
            {
                vbsRunning = WmiValue.UInt32(
                    deviceGuard,
                    "VirtualizationBasedSecurityStatus") == 2;
                foreach (var code in WmiValue.UInt32Array(
                             deviceGuard,
                             "SecurityServicesRunning"))
                {
                    runningSecurityServices.Add(
                        SecurityServiceNames.GetValueOrDefault(
                            code,
                            $"Security service {code}"));
                }
            }
        }
        catch (ManagementException exception)
        {
            Notice("Device Guard inventory", "information", exception.Message);
        }

        return new SecurityInfo(
            defenderEnabled,
            realTimeProtection,
            signatureAge,
            vbsRunning,
            runningSecurityServices);
    }

    private WorkloadInventory QueryWorkload()
    {
        var processNames = Process.GetProcesses()
            .Select(process =>
            {
                using (process)
                {
                    return process.ProcessName;
                }
            })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var overlays = OverlayProcessNames
            .Where(processNames.Contains)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var background = BackgroundProcessNames
            .Where(processNames.Contains)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var services = Query("root\\cimv2", "SELECT State, StartMode FROM Win32_Service");
        return new WorkloadInventory(
            processNames.Count,
            services.Count(service =>
                string.Equals(
                    WmiValue.Text(service, "State"),
                    "Running",
                    StringComparison.OrdinalIgnoreCase)),
            services.Count(service =>
                string.Equals(
                    WmiValue.Text(service, "StartMode"),
                    "Auto",
                    StringComparison.OrdinalIgnoreCase)),
            overlays,
            background);
    }

    private List<SystemEventInfo> QueryRecentEvents()
    {
        var result = new List<SystemEventInfo>();
        const string query =
            "*[System[(Level=1 or Level=2 or Level=3) and " +
            "TimeCreated[timediff(@SystemTime) <= 604800000]]]";

        try
        {
            var eventQuery = new EventLogQuery(
                "System",
                PathType.LogName,
                query)
            {
                ReverseDirection = true,
                TolerateQueryErrors = true
            };
            using var reader = new EventLogReader(eventQuery);
            for (var index = 0; index < 150; index++)
            {
                using var record = reader.ReadEvent();
                if (record is null)
                {
                    break;
                }

                string message;
                try
                {
                    message = record.FormatDescription() ?? string.Empty;
                }
                catch (EventLogException)
                {
                    message = "Event text is unavailable.";
                }

                result.Add(
                    new SystemEventInfo(
                        record.TimeCreated is DateTime time
                            ? new DateTimeOffset(time)
                            : null,
                        record.ProviderName ?? string.Empty,
                        record.Id,
                        record.LevelDisplayName ?? $"Level {record.Level}",
                        Truncate(message.ReplaceLineEndings(" "), 600)));
            }
        }
        catch (Exception exception) when (
            exception is EventLogException or UnauthorizedAccessException)
        {
            Notice("System event log", "warning", exception.Message);
        }

        return result;
    }

    private NvidiaSmiRow[] QueryNvidiaSmi()
    {
        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "nvidia-smi.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA Corporation",
                "NVSMI",
                "nvidia-smi.exe"),
            "nvidia-smi.exe"
        };

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = candidate,
                        Arguments =
                            "--query-gpu=name,driver_version,memory.total," +
                            "temperature.gpu,power.draw --format=csv,noheader,nounits",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                if (!process.Start())
                {
                    continue;
                }

                var output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(5000))
                {
                    process.Kill(entireProcessTree: true);
                    continue;
                }

                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                {
                    continue;
                }

                return output.Split(
                        LineSeparators,
                        StringSplitOptions.RemoveEmptyEntries)
                    .Select(ParseNvidiaSmiRow)
                    .Where(row => row is not null)
                    .Select(row => row!)
                    .ToArray();
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception
                    or FormatException)
            {
                // Try the next conventional location.
            }
        }

        Notice(
            "GPU inventory",
            "information",
            "NVIDIA-SMI was unavailable; temperature, power, and exact VRAM may be absent.");
        return Array.Empty<NvidiaSmiRow>();
    }

    private static NvidiaSmiRow? ParseNvidiaSmiRow(string line)
    {
        var fields = line.Split(',').Select(field => field.Trim()).ToArray();
        if (fields.Length < 5)
        {
            return null;
        }

        return new NvidiaSmiRow(
            fields[0],
            fields[1],
            ParseDouble(fields[2]),
            ParseDouble(fields[3]),
            ParseDouble(fields[4]));
    }

    private static double? ParseDouble(string value) =>
        double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;

    private (string Name, string Guid) QueryActivePowerPlan()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.System),
                        "powercfg.exe"),
                    Arguments = "/getactivescheme",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            if (!process.Start())
            {
                return (string.Empty, string.Empty);
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return (string.Empty, string.Empty);
            }

            var match = System.Text.RegularExpressions.Regex.Match(
                output,
                @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-" +
                @"[0-9a-fA-F]{4}-[0-9a-fA-F]{12}).*?\((.+?)\)");
            return match.Success
                ? (match.Groups[2].Value.Trim(), match.Groups[1].Value)
                : (output.Trim(), string.Empty);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Notice("Power inventory", "information", exception.Message);
            return (string.Empty, string.Empty);
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool IsPendingReboot()
    {
        using var componentServicing = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        using var windowsUpdate = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        using var sessionManager = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\Session Manager");
        return componentServicing is not null ||
               windowsUpdate is not null ||
               sessionManager?.GetValue("PendingFileRenameOperations") is not null;
    }

    private List<ManagementBaseObject> Query(string scope, string query)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(scope),
                new ObjectQuery(query));
            using var results = searcher.Get();
            return results.Cast<ManagementBaseObject>().ToList();
        }
        catch (Exception exception) when (
            exception is ManagementException
                or COMException
                or UnauthorizedAccessException)
        {
            Notice("WMI", "warning", $"{query}: {exception.Message}");
            return new List<ManagementBaseObject>();
        }
    }

    private List<ManagementObject> QueryObjects(string scope, string query)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(scope),
                new ObjectQuery(query));
            using var results = searcher.Get();
            return results.Cast<ManagementObject>().ToList();
        }
        catch (Exception exception) when (
            exception is ManagementException
                or COMException
                or UnauthorizedAccessException)
        {
            Notice("WMI", "warning", $"{query}: {exception.Message}");
            return new List<ManagementObject>();
        }
    }

    private ManagementObject? First(string scope, string query)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(scope),
                new ObjectQuery(query));
            using var results = searcher.Get();
            return results.Cast<ManagementObject>().FirstOrDefault();
        }
        catch (Exception exception) when (
            exception is ManagementException
                or COMException
                or UnauthorizedAccessException)
        {
            Notice("WMI", "warning", $"{query}: {exception.Message}");
            return null;
        }
    }

    private void Notice(string collector, string level, string message)
    {
        lock (_notices)
        {
            _notices.Add(new CollectorNotice(collector, level, message));
        }
    }

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : $"{value[..maximum]}…";

    private sealed record NvidiaSmiRow(
        string Name,
        string DriverVersion,
        double? MemoryMb,
        double? TemperatureCelsius,
        double? PowerWatts);
}

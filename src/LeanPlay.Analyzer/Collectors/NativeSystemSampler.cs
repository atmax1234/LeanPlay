using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace LeanPlay.Analyzer.Collectors;

internal sealed partial class NativeSystemSampler
{
    private CpuTimes? _previousCpu;
    private long? _previousNetworkBytes;

    public NativeSystemSampler()
    {
        _previousCpu = ReadCpuTimes();
        _previousNetworkBytes = ReadNetworkBytes();
    }

    public NativeSystemSample Read(TimeSpan elapsed)
    {
        var cpu = ReadCpuTimes();
        double? totalCpu = null;
        double? privilegedCpu = null;
        if (cpu is not null && _previousCpu is not null)
        {
            var idleDelta = Delta(cpu.Idle, _previousCpu.Idle);
            var kernelDelta = Delta(cpu.Kernel, _previousCpu.Kernel);
            var userDelta = Delta(cpu.User, _previousCpu.User);
            var totalDelta = kernelDelta + userDelta;
            if (totalDelta > 0)
            {
                totalCpu = Math.Clamp(
                    (totalDelta - Math.Min(idleDelta, totalDelta)) * 100d / totalDelta,
                    0,
                    100);
                privilegedCpu = Math.Clamp(
                    (kernelDelta - Math.Min(idleDelta, kernelDelta)) * 100d / totalDelta,
                    0,
                    100);
            }
        }

        _previousCpu = cpu;

        double? availableMemoryMb = null;
        double? committedPercent = null;
        var performance = new PerformanceInformation
        {
            Size = checked((uint)Marshal.SizeOf<PerformanceInformation>())
        };
        if (GetPerformanceInfo(ref performance, performance.Size) &&
            performance.PageSize > 0)
        {
            availableMemoryMb =
                performance.PhysicalAvailable.ToUInt64() *
                performance.PageSize.ToUInt64() /
                1024d /
                1024d;
            var commitLimit = performance.CommitLimit.ToUInt64();
            if (commitLimit > 0)
            {
                committedPercent =
                    performance.CommitTotal.ToUInt64() * 100d / commitLimit;
            }
        }

        var networkBytes = ReadNetworkBytes();
        double? networkBytesPerSecond = null;
        if (networkBytes is long current &&
            _previousNetworkBytes is long previous &&
            current >= previous &&
            elapsed.TotalSeconds > 0)
        {
            networkBytesPerSecond = (current - previous) / elapsed.TotalSeconds;
        }

        _previousNetworkBytes = networkBytes;
        return new NativeSystemSample(
            totalCpu,
            privilegedCpu,
            availableMemoryMb,
            committedPercent,
            networkBytesPerSecond);
    }

    private static CpuTimes? ReadCpuTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return null;
        }

        return new CpuTimes(idle.ToUInt64(), kernel.ToUInt64(), user.ToUInt64());
    }

    private static long? ReadNetworkBytes()
    {
        try
        {
            long bytes = 0;
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                var statistics = adapter.GetIPv4Statistics();
                bytes += statistics.BytesReceived + statistics.BytesSent;
            }

            return bytes;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private static ulong Delta(ulong current, ulong previous) =>
        current >= previous ? current - previous : 0;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        private readonly uint _low;
        private readonly uint _high;

        public ulong ToUInt64() => ((ulong)_high << 32) | _low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal;
        public nuint CommitLimit;
        public nuint CommitPeak;
        public nuint PhysicalTotal;
        public nuint PhysicalAvailable;
        public nuint SystemCache;
        public nuint KernelTotal;
        public nuint KernelPaged;
        public nuint KernelNonpaged;
        public nuint PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    private sealed record CpuTimes(ulong Idle, ulong Kernel, ulong User);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(
        out FileTime idleTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPerformanceInfo(
        ref PerformanceInformation performanceInformation,
        uint size);
}

internal sealed record NativeSystemSample(
    double? CpuTotalPercent,
    double? CpuPrivilegedPercent,
    double? AvailableMemoryMb,
    double? CommittedMemoryPercent,
    double? NetworkBytesPerSecond);

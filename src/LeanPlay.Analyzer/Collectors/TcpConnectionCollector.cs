using System.Runtime.InteropServices;

namespace LeanPlay.Analyzer.Collectors;

internal static partial class TcpConnectionCollector
{
    private const uint ErrorInsufficientBuffer = 122;
    private const int AddressFamilyInternet = 2;
    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;

    public static IReadOnlyDictionary<int, int> CountByProcess()
    {
        uint size = 0;
        var result = GetExtendedTcpTable(
            0,
            ref size,
            sort: false,
            AddressFamilyInternet,
            TcpTableOwnerPidAll,
            0);
        if (result != ErrorInsufficientBuffer || size == 0)
        {
            return new Dictionary<int, int>();
        }

        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            result = GetExtendedTcpTable(
                buffer,
                ref size,
                sort: false,
                AddressFamilyInternet,
                TcpTableOwnerPidAll,
                0);
            if (result != 0)
            {
                return new Dictionary<int, int>();
            }

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var counts = new Dictionary<int, int>();
            var address = buffer + sizeof(int);
            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(
                    address + (index * rowSize));
                var processId = checked((int)row.OwningProcessId);
                counts[processId] = counts.GetValueOrDefault(processId) + 1;
            }

            return counts;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static IReadOnlyDictionary<int, int> CountUdpByProcess()
    {
        uint size = 0;
        var result = GetExtendedUdpTable(
            0,
            ref size,
            sort: false,
            AddressFamilyInternet,
            UdpTableOwnerPid,
            0);
        if (result != ErrorInsufficientBuffer || size == 0)
        {
            return new Dictionary<int, int>();
        }

        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            result = GetExtendedUdpTable(
                buffer,
                ref size,
                sort: false,
                AddressFamilyInternet,
                UdpTableOwnerPid,
                0);
            if (result != 0)
            {
                return new Dictionary<int, int>();
            }

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibUdpRowOwnerPid>();
            var counts = new Dictionary<int, int>();
            var address = buffer + sizeof(int);
            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<MibUdpRowOwnerPid>(
                    address + (index * rowSize));
                var processId = checked((int)row.OwningProcessId);
                counts[processId] = counts.GetValueOrDefault(processId) + 1;
            }

            return counts;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MibTcpRowOwnerPid
    {
        public readonly uint State;
        public readonly uint LocalAddress;
        public readonly uint LocalPort;
        public readonly uint RemoteAddress;
        public readonly uint RemotePort;
        public readonly uint OwningProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MibUdpRowOwnerPid
    {
        public readonly uint LocalAddress;
        public readonly uint LocalPort;
        public readonly uint OwningProcessId;
    }

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    private static partial uint GetExtendedTcpTable(
        nint tcpTable,
        ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool sort,
        int ipVersion,
        int tableClass,
        uint reserved);

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    private static partial uint GetExtendedUdpTable(
        nint udpTable,
        ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool sort,
        int ipVersion,
        int tableClass,
        uint reserved);
}

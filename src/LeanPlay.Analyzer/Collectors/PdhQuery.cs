using System.Runtime.InteropServices;
using LeanPlay.Analyzer.Model;

namespace LeanPlay.Analyzer.Collectors;

internal sealed partial class PdhQuery : IDisposable
{
    private const uint ErrorSuccess = 0;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhFormatDouble = 0x00000200;
    private readonly Dictionary<string, nint> _counters =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CollectorNotice> _notices;
    private nint _query;

    public PdhQuery(List<CollectorNotice> notices)
    {
        _notices = notices;
        var result = PdhOpenQuery(null, 0, out _query);
        if (result != ErrorSuccess)
        {
            throw new InvalidOperationException(
                $"PDH query creation failed with 0x{result:X8}.");
        }
    }

    public void Add(string key, string englishCounterPath)
    {
        var result = PdhAddEnglishCounter(
            _query,
            englishCounterPath,
            0,
            out var counter);
        if (result == ErrorSuccess)
        {
            _counters[key] = counter;
            return;
        }

        _notices.Add(
            new CollectorNotice(
                "Performance counters",
                "information",
                $"Counter '{englishCounterPath}' is unavailable (0x{result:X8})."));
    }

    public bool Collect()
    {
        var result = PdhCollectQueryData(_query);
        if (result == ErrorSuccess)
        {
            return true;
        }

        _notices.Add(
            new CollectorNotice(
                "Performance counters",
                "warning",
                $"PDH collection failed with 0x{result:X8}."));
        return false;
    }

    public double? Read(string key)
    {
        if (!_counters.TryGetValue(key, out var counter))
        {
            return null;
        }

        var result = PdhGetFormattedCounterValue(
            counter,
            PdhFormatDouble,
            out _,
            out var value);
        return result == ErrorSuccess && value.IsValid
            ? value.DoubleValue
            : null;
    }

    public IReadOnlyList<(string Instance, double Value)> ReadArray(string key)
    {
        if (!_counters.TryGetValue(key, out var counter))
        {
            return Array.Empty<(string, double)>();
        }

        uint bufferSize = 0;
        uint itemCount = 0;
        var result = PdhGetFormattedCounterArray(
            counter,
            PdhFormatDouble,
            ref bufferSize,
            ref itemCount,
            0);
        if (result != PdhMoreData || bufferSize == 0 || itemCount == 0)
        {
            return Array.Empty<(string, double)>();
        }

        var buffer = Marshal.AllocHGlobal(checked((int)bufferSize));
        try
        {
            result = PdhGetFormattedCounterArray(
                counter,
                PdhFormatDouble,
                ref bufferSize,
                ref itemCount,
                buffer);
            if (result != ErrorSuccess)
            {
                return Array.Empty<(string, double)>();
            }

            var itemSize = Marshal.SizeOf<PdhFormattedCounterValueItem>();
            var values = new List<(string, double)>(checked((int)itemCount));
            for (var index = 0; index < itemCount; index++)
            {
                var address = buffer + checked((int)index * itemSize);
                var item = Marshal.PtrToStructure<PdhFormattedCounterValueItem>(address);
                if (item.Value.IsValid)
                {
                    values.Add(
                        (Marshal.PtrToStringUni(item.Name) ?? string.Empty,
                         item.Value.DoubleValue));
                }
            }

            return values;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (_query != 0)
        {
            _ = PdhCloseQuery(_query);
            _query = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PdhFormattedCounterValue
    {
        public readonly uint Status;
        private readonly uint _padding;
        public readonly double DoubleValue;

        public bool IsValid => Status is 0 or 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PdhFormattedCounterValueItem
    {
        public readonly nint Name;
        public readonly PdhFormattedCounterValue Value;
    }

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQuery(
        string? dataSource,
        nuint userData,
        out nint query);

    [LibraryImport(
        "pdh.dll",
        EntryPoint = "PdhAddEnglishCounterW",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounter(
        nint query,
        string fullCounterPath,
        nuint userData,
        out nint counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterValue")]
    private static partial uint PdhGetFormattedCounterValue(
        nint counter,
        uint format,
        out uint counterType,
        out PdhFormattedCounterValue value);

    [LibraryImport(
        "pdh.dll",
        EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static partial uint PdhGetFormattedCounterArray(
        nint counter,
        uint format,
        ref uint bufferSize,
        ref uint itemCount,
        nint itemBuffer);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(nint query);
}

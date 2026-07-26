using System.Globalization;
using System.Management;

namespace LeanPlay.Analyzer.Collectors;

internal static class WmiValue
{
    public static string Text(ManagementBaseObject item, string property) =>
        Convert.ToString(
            item.Properties[property]?.Value,
            CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;

    public static uint UInt32(ManagementBaseObject item, string property)
    {
        var value = item.Properties[property]?.Value;
        return value is null
            ? 0
            : Convert.ToUInt32(value, CultureInfo.InvariantCulture);
    }

    public static int Int32(ManagementBaseObject item, string property)
    {
        var value = item.Properties[property]?.Value;
        return value is null
            ? 0
            : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    public static ulong UInt64(ManagementBaseObject item, string property)
    {
        var value = item.Properties[property]?.Value;
        return value is null
            ? 0
            : Convert.ToUInt64(value, CultureInfo.InvariantCulture);
    }

    public static bool? Boolean(ManagementBaseObject item, string property)
    {
        var value = item.Properties[property]?.Value;
        return value is null
            ? null
            : Convert.ToBoolean(value, CultureInfo.InvariantCulture);
    }

    public static string[] TextArray(ManagementBaseObject item, string property) =>
        item.Properties[property]?.Value is Array values
            ? values.Cast<object>()
                .Select(value => Convert.ToString(value, CultureInfo.InvariantCulture))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToArray()
            : Array.Empty<string>();

    public static uint[] UInt32Array(ManagementBaseObject item, string property) =>
        item.Properties[property]?.Value is Array values
            ? values.Cast<object>()
                .Select(value => Convert.ToUInt32(value, CultureInfo.InvariantCulture))
                .ToArray()
            : Array.Empty<uint>();

    public static DateTimeOffset? DateTime(ManagementBaseObject item, string property)
    {
        var value = Text(item, property);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var converted = ManagementDateTimeConverter.ToDateTime(value);
            return new DateTimeOffset(converted);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}

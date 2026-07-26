namespace LeanPlay.Analyzer.Analysis;

public static class Statistics
{
    public static double? Average(IEnumerable<double?> values)
    {
        var materialized = values
            .Where(value => value is double number && double.IsFinite(number))
            .Select(value => value!.Value)
            .ToArray();
        return materialized.Length == 0 ? null : materialized.Average();
    }

    public static double? Minimum(IEnumerable<double?> values)
    {
        var materialized = values
            .Where(value => value is double number && double.IsFinite(number))
            .Select(value => value!.Value)
            .ToArray();
        return materialized.Length == 0 ? null : materialized.Min();
    }

    public static double? Maximum(IEnumerable<double?> values)
    {
        var materialized = values
            .Where(value => value is double number && double.IsFinite(number))
            .Select(value => value!.Value)
            .ToArray();
        return materialized.Length == 0 ? null : materialized.Max();
    }

    public static double? Percentile(IEnumerable<double?> values, double percentile)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(percentile, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percentile, 100);

        var ordered = values
            .Where(value => value is double number && double.IsFinite(number))
            .Select(value => value!.Value)
            .Order()
            .ToArray();
        if (ordered.Length == 0)
        {
            return null;
        }

        if (ordered.Length == 1)
        {
            return ordered[0];
        }

        var position = percentile / 100d * (ordered.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return ordered[lower];
        }

        var fraction = position - lower;
        return ordered[lower] + ((ordered[upper] - ordered[lower]) * fraction);
    }
}

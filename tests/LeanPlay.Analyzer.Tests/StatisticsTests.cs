using LeanPlay.Analyzer.Analysis;

namespace LeanPlay.Analyzer.Tests;

public sealed class StatisticsTests
{
    [Fact]
    public void PercentileInterpolatesAndIgnoresMissingValues()
    {
        double?[] values = { 1, null, 2, 3, 4, 5 };

        Assert.Equal(3, Statistics.Percentile(values, 50));
        Assert.Equal(4.8, Statistics.Percentile(values, 95));
    }

    [Fact]
    public void EmptySeriesReturnsNull()
    {
        Assert.Null(Statistics.Average(Array.Empty<double?>()));
        Assert.Null(Statistics.Percentile(Array.Empty<double?>(), 95));
    }
}

using LeanPlay.Analyzer.CommandLine;

namespace LeanPlay.Analyzer.Tests;

public sealed class CommandLineOptionsTests
{
    private static readonly string[] FullArguments =
    {
        "--duration", "90",
        "--interval", "2",
        "--label", "CS2 test",
        "--no-etw",
        "--no-open"
    };

    private static readonly string[] ShortArguments = { "--duration", "3" };

    [Fact]
    public void ParsesDurationLabelAndSafetyFlags()
    {
        var parsed = CommandLineOptions.Parse(FullArguments);

        Assert.Null(parsed.Error);
        Assert.NotNull(parsed.Options);
        Assert.Equal(90, parsed.Options.Duration.TotalSeconds);
        Assert.Equal(2, parsed.Options.SampleInterval.TotalSeconds);
        Assert.Equal("CS2 test", parsed.Options.WorkloadLabel);
        Assert.False(parsed.Options.IncludeEtw);
        Assert.False(parsed.Options.OpenReport);
    }

    [Fact]
    public void RejectsTraceThatIsTooShort()
    {
        var parsed = CommandLineOptions.Parse(ShortArguments);

        Assert.Null(parsed.Options);
        Assert.Contains("between 10 and 1800", parsed.Error);
    }
}

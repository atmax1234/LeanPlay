using System.Globalization;
using LeanPlay.Analyzer.Model;

namespace LeanPlay.Analyzer.CommandLine;

public sealed record CommandLineParseResult(
    AnalysisOptions? Options,
    bool ShowHelp,
    string? Error);

public static class CommandLineOptions
{
    public static CommandLineParseResult Parse(string[] args)
    {
        var durationSeconds = 60d;
        var intervalSeconds = 1d;
        var outputDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "LeanPlay Reports");
        var targets = new List<string> { "1.1.1.1" };
        var includeEtw = true;
        var openReport = true;
        string? workloadLabel = null;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument.ToLowerInvariant())
            {
                case "--help":
                case "-h":
                case "/?":
                    return new CommandLineParseResult(null, ShowHelp: true, null);
                case "--quick":
                    durationSeconds = 15;
                    break;
                case "--duration":
                    if (!TryReadDouble(args, ref index, out durationSeconds))
                    {
                        return Error("--duration requires a number of seconds.");
                    }

                    break;
                case "--interval":
                    if (!TryReadDouble(args, ref index, out intervalSeconds))
                    {
                        return Error("--interval requires a number of seconds.");
                    }

                    break;
                case "--output":
                    if (!TryReadValue(args, ref index, out outputDirectory))
                    {
                        return Error("--output requires a directory path.");
                    }

                    break;
                case "--target":
                    if (!TryReadValue(args, ref index, out var target))
                    {
                        return Error("--target requires a host name or IP address.");
                    }

                    targets.AddRange(
                        target.Split(
                            ',',
                            StringSplitOptions.RemoveEmptyEntries |
                            StringSplitOptions.TrimEntries));
                    break;
                case "--label":
                    if (!TryReadValue(args, ref index, out workloadLabel))
                    {
                        return Error("--label requires a short workload name.");
                    }

                    break;
                case "--no-etw":
                    includeEtw = false;
                    break;
                case "--no-open":
                    openReport = false;
                    break;
                default:
                    return Error($"Unknown option '{argument}'.");
            }
        }

        if (durationSeconds is < 10 or > 1800)
        {
            return Error("--duration must be between 10 and 1800 seconds.");
        }

        if (intervalSeconds is < 0.5 or > 5)
        {
            return Error("--interval must be between 0.5 and 5 seconds.");
        }

        if (intervalSeconds > durationSeconds / 3)
        {
            return Error("--interval must produce at least three timed samples.");
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return Error("The output directory cannot be empty.");
        }

        return new CommandLineParseResult(
            new AnalysisOptions(
                TimeSpan.FromSeconds(durationSeconds),
                TimeSpan.FromSeconds(intervalSeconds),
                Path.GetFullPath(
                    Environment.ExpandEnvironmentVariables(outputDirectory)),
                targets.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                includeEtw,
                openReport,
                string.IsNullOrWhiteSpace(workloadLabel)
                    ? null
                    : workloadLabel.Trim()),
            ShowHelp: false,
            null);
    }

    public static string HelpText =>
        """
        LeanPlay Windows Analyzer

        Usage:
          LeanPlay.Analyzer [options]

        Options:
          --duration <seconds>   Trace duration, 10-1800 (default: 60)
          --interval <seconds>   Sampling interval, 0.5-5 (default: 1)
          --quick                Use a 15-second smoke trace
          --target <host[,host]> Add a public ping reference (default includes 1.1.1.1)
          --label <name>         Label the reproduced workload, e.g. "CS2 deathmatch"
          --output <directory>   Report directory (default: Documents\LeanPlay Reports)
          --no-etw               Skip elevated kernel disk/network/DPC attribution
          --no-open              Do not open the HTML report when complete
          --help                 Show this help

        For full driver attribution, run scripts\Run-LeanPlayAnalysis.ps1 and approve
        its elevation prompt. The analyzer is read-only and never applies optimizations.
        """;

    private static bool TryReadDouble(
        string[] args,
        ref int index,
        out double value)
    {
        value = 0;
        return index + 1 < args.Length &&
               double.TryParse(
                   args[++index],
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out value);
    }

    private static bool TryReadValue(
        string[] args,
        ref int index,
        out string value)
    {
        value = string.Empty;
        if (index + 1 >= args.Length)
        {
            return false;
        }

        value = args[++index];
        return !string.IsNullOrWhiteSpace(value);
    }

    private static CommandLineParseResult Error(string message) =>
        new(null, ShowHelp: false, message);
}

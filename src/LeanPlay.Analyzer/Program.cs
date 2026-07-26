using System.Diagnostics;
using LeanPlay.Analyzer.Analysis;
using LeanPlay.Analyzer.CommandLine;
using LeanPlay.Analyzer.Model;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("LeanPlay Analyzer runs only on Windows.");
    return 3;
}

var parsed = CommandLineOptions.Parse(args);
if (parsed.ShowHelp)
{
    Console.WriteLine(CommandLineOptions.HelpText);
    return 0;
}

if (parsed.Error is not null || parsed.Options is null)
{
    Console.Error.WriteLine($"Error: {parsed.Error}");
    Console.Error.WriteLine();
    Console.Error.WriteLine(CommandLineOptions.HelpText);
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var options = parsed.Options;
Console.WriteLine("LeanPlay Windows Analyzer");
Console.WriteLine("========================");
Console.WriteLine(
    $"Collecting a {options.Duration.TotalSeconds:F0}-second read-only trace.");
Console.WriteLine(
    options.IncludeEtw
        ? "Kernel ETW will be used when the process is elevated."
        : "Kernel ETW is disabled; standard counters remain enabled.");
Console.WriteLine("Reproduce the slowdown now. Press Ctrl+C to cancel.");
Console.WriteLine();

var lastProgress = -1;
var runner = new AnalysisRunner(progress =>
{
    var percent = (int)Math.Floor(progress * 100);
    var bucket = percent / 5;
    if (bucket == lastProgress)
    {
        return;
    }

    lastProgress = bucket;
    Console.Write($"\rSampling: {Math.Min(100, percent),3}%");
});

try
{
    var result = await runner.RunAsync(options, cancellation.Token).ConfigureAwait(false);
    Console.WriteLine("\rSampling: 100%");
    Console.WriteLine();
    PrintSummary(result.Report);
    Console.WriteLine();
    Console.WriteLine($"HTML report: {result.Paths.HtmlPath}");
    Console.WriteLine($"JSON data:   {result.Paths.JsonPath}");

    if (options.OpenReport)
    {
        try
        {
            _ = Process.Start(
                new ProcessStartInfo(result.Paths.HtmlPath)
                {
                    UseShellExecute = true
                });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            Console.WriteLine($"Could not open the browser automatically: {exception.Message}");
        }
    }

    return 0;
}
catch (OperationCanceledException)
{
    Console.WriteLine();
    Console.WriteLine("Analysis cancelled; no Windows settings were changed.");
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"Analysis failed: {exception.Message}");
    Console.Error.WriteLine(exception);
    return 3;
}

static void PrintSummary(AnalysisReport report)
{
    var important = report.Findings
        .Where(finding => finding.Severity is
            FindingSeverity.Critical or FindingSeverity.Warning)
        .Take(8)
        .ToArray();

    Console.WriteLine(
        $"Recorded {report.Samples.Count} samples and {report.Processes.Count} processes.");
    Console.WriteLine(
        report.Etw.Collected
            ? "Kernel ETW attribution: collected"
            : $"Kernel ETW attribution: unavailable ({report.Etw.UnavailableReason})");

    if (important.Length == 0)
    {
        Console.WriteLine("No critical or warning finding was measured in this interval.");
        return;
    }

    Console.WriteLine("Highest-priority findings:");
    foreach (var finding in important)
    {
        Console.WriteLine($"  [{finding.Severity}] {finding.Title}");
    }
}

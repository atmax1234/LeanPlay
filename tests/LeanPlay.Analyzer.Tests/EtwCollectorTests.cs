using LeanPlay.Analyzer.Collectors;
using LeanPlay.Analyzer.Model;

namespace LeanPlay.Analyzer.Tests;

public sealed class EtwCollectorTests
{
    [Fact]
    public void KernelProviderIsEnabledBeforeSourceAccess()
    {
        var calls = new List<string>();

        EtwCollector.EnableKernelBeforeAccessingSource(
            () => calls.Add("enable"),
            () => calls.Add("source"));

        Assert.Equal(["enable", "source"], calls);
    }

    [Fact]
    public async Task StartupFailureDegradesWithoutAbortingAnalysis()
    {
        const string failure = "synthetic kernel session failure";
        var notices = new List<CollectorNotice>();
        await using var collector = new EtwCollector(
            requested: true,
            notices,
            administratorCheck: () => true,
            sessionStarterOverride: () => throw new NotSupportedException(failure));

        await collector.StartAsync(CancellationToken.None);

        var summary = collector.BuildSummary();
        Assert.False(summary.Collected);
        Assert.Equal(failure, summary.UnavailableReason);
        Assert.Contains(
            notices,
            notice =>
                notice.Collector == "Kernel ETW" &&
                notice.Level == "warning" &&
                notice.Message.Contains(failure, StringComparison.Ordinal));
    }
}

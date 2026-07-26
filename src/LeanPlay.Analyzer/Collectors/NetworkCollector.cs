using System.Diagnostics;
using System.Net.NetworkInformation;
using LeanPlay.Analyzer.Model;

namespace LeanPlay.Analyzer.Collectors;

public sealed class NetworkCollector
{
    private readonly List<CollectorNotice> _notices;

    public NetworkCollector(List<CollectorNotice> notices)
    {
        _notices = notices;
    }

    public async Task<NetworkSummary> CollectAsync(
        MachineInventory inventory,
        IReadOnlyList<string> publicTargets,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var start = ReadNetworkCounters();
        var targets = inventory.NetworkAdapters
            .SelectMany(adapter => adapter.Gateways)
            .Where(gateway => !string.IsNullOrWhiteSpace(gateway))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(gateway => (Target: gateway, Role: "Default gateway"))
            .Concat(
                publicTargets
                    .Where(target => !string.IsNullOrWhiteSpace(target))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(target => (Target: target, Role: "Public reference")))
            .ToArray();

        var pingTasks = targets
            .Select(target => PingForDurationAsync(
                target.Target,
                target.Role,
                duration,
                cancellationToken))
            .ToArray();
        var pings = await Task.WhenAll(pingTasks).ConfigureAwait(false);
        var end = ReadNetworkCounters();

        var sent = Delta(end.TcpSegmentsSent, start.TcpSegmentsSent);
        var resent = Delta(end.TcpSegmentsResent, start.TcpSegmentsResent);
        return new NetworkSummary(
            pings,
            Delta(end.BytesReceived, start.BytesReceived),
            Delta(end.BytesSent, start.BytesSent),
            sent,
            resent,
            sent > 0 ? resent * 100d / sent : null);
    }

    private static async Task<PingTargetSummary> PingForDurationAsync(
        string target,
        string role,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var latencies = new List<double>();
        var sent = 0;
        string? lastError = null;
        var started = Stopwatch.GetTimestamp();
        using var ping = new Ping();

        while (Stopwatch.GetElapsedTime(started) < duration)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sent++;
            try
            {
                var reply = await ping.SendPingAsync(target, 1200)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (reply.Status == IPStatus.Success)
                {
                    latencies.Add(reply.RoundtripTime);
                }
                else
                {
                    lastError = reply.Status.ToString();
                }
            }
            catch (Exception exception) when (
                exception is PingException
                    or InvalidOperationException
                    or System.Net.Sockets.SocketException)
            {
                lastError = exception.Message;
            }

            var remaining = duration - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(
                    remaining < TimeSpan.FromSeconds(1)
                        ? remaining
                        : TimeSpan.FromSeconds(1),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        double? jitter = latencies.Count > 1
            ? latencies.Zip(latencies.Skip(1), (left, right) => Math.Abs(right - left))
                .Average()
            : null;
        return new PingTargetSummary(
            target,
            role,
            sent,
            latencies.Count,
            sent == 0 ? 0 : (sent - latencies.Count) * 100d / sent,
            latencies.Count == 0 ? null : latencies.Min(),
            latencies.Count == 0 ? null : latencies.Average(),
            latencies.Count == 0 ? null : latencies.Max(),
            jitter,
            latencies.Count == 0 ? lastError : null);
    }

    private NetworkCounters ReadNetworkCounters()
    {
        try
        {
            long received = 0;
            long sent = 0;
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                var statistics = adapter.GetIPv4Statistics();
                received += statistics.BytesReceived;
                sent += statistics.BytesSent;
            }

            var tcp = IPGlobalProperties.GetIPGlobalProperties().GetTcpIPv4Statistics();
            return new NetworkCounters(
                received,
                sent,
                tcp.SegmentsSent,
                tcp.SegmentsResent);
        }
        catch (NetworkInformationException exception)
        {
            _notices.Add(
                new CollectorNotice("Network counters", "warning", exception.Message));
            return new NetworkCounters(0, 0, 0, 0);
        }
    }

    private static long Delta(long current, long previous) =>
        current >= previous ? current - previous : 0;

    private sealed record NetworkCounters(
        long BytesReceived,
        long BytesSent,
        long TcpSegmentsSent,
        long TcpSegmentsResent);
}

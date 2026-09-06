using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NetworkMonitoring.Core;

namespace NetworkMonitoring.Dashboard;

/// <summary>实时看板 API（与参考 server.ts 字段结构 1:1，前端 dashboard.html 零改动复用）</summary>
public static class DashboardServer
{
    private const int WindowMs = 30 * 60 * 1000;
    private const double LossAlertThreshold = 0.2;

    public static void MapDashboard(this WebApplication app, Collector collector)
    {
        app.MapGet("/api/meta", () =>
        {
            var tcpColors = Config.TcpTargets.ToDictionary(t => t.Key, t => Config.Colors[t.Key]);
            var ttfbColors = Config.TtfbTargets.ToDictionary(t => t.Key, t => Config.Colors[t.Key]);
            return new
            {
                targets = Config.Targets.Select(t => new { t.Key, t.Label }).ToArray(),
                tcpTargets = Config.TcpTargets.Select(t => new { t.Key, t.Label }).ToArray(),
                ttfbTargets = Config.TtfbTargets.Select(t => new { t.Key, t.Label }).ToArray(),
                colors = Config.Colors,
                tcpColors,
                ttfbColors,
                now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
        });

        app.MapGet("/api/latest", (long? from) =>
        {
            var f = from ?? 0;
            var points = new List<object?[]>();
            foreach (var s in collector.SamplesInRange(f))
            {
                var row = new List<object?> { s.Ts };
                foreach (var t in Config.Targets) row.Add(Val(s.Icmp, t.Key));
                foreach (var t in Config.TcpTargets) row.Add(Val(s.Tcp, t.Key));
                foreach (var t in Config.TtfbTargets) row.Add(Val(s.Ttfb, t.Key));
                points.Add(row.ToArray());
            }
            return new
            {
                now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                points,
            };
        });

        app.MapGet("/api/summary", () =>
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var dayStart = new DateTimeOffset(DateTime.Today).ToUnixTimeMilliseconds();
            var daySamples = collector.SamplesInRange(dayStart);
            var win = collector.SamplesInRange(now - WindowMs);

            var targets = Config.Targets.Select(t =>
            {
                var w = Stat(win, s => s.Icmp, t.Key);
                var d = Stat(daySamples, s => s.Icmp, t.Key);
                (double? Avg, double? Max, double LossPct)? tcpS = t.Tcp ? Stat(win, s => s.Tcp, t.Key) : null;
                (double? Avg, double? Max, double LossPct)? ttfbS = t.Ttfb ? TtfbStat(win, t.Key) : null;
                double? tcpAvg = tcpS is null ? null : tcpS.Value.Avg;
                double? tcpLoss = tcpS is null ? null : tcpS.Value.LossPct;
                double? ttfbAvg = ttfbS is null ? null : ttfbS.Value.Avg;
                double? ttfbLoss = ttfbS is null ? null : ttfbS.Value.LossPct;
                return new
                {
                    t.Key,
                    t.Label,
                    avg = w.Avg,
                    max = w.Max,
                    lossPct = w.LossPct,
                    dayAvg = d.Avg,
                    dayLossPct = d.LossPct,
                    tcpAvg,
                    tcpLossPct = tcpLoss,
                    ttfbAvg,
                    ttfbLossPct = ttfbLoss,
                };
            }).ToArray();

            var currentLossKeys = targets.Where(t => t.lossPct >= LossAlertThreshold).Select(t => t.Key).ToArray();
            return new { now, sampleCountToday = daySamples.Count, targets, currentLossKeys };
        });

        app.MapGet("/", () => Results.File(Path.Combine(Config.AssetsDir, "dashboard.html"), "text/html; charset=utf-8"));
        app.MapGet("/chart.min.js", () =>
            Results.File(Path.Combine(Config.AssetsDir, "chart.min.js"), "application/javascript; charset=utf-8"));
        app.MapGet("/favicon.ico", () => Results.StatusCode(204));
    }

    private static double? Val(Dictionary<string, double> d, string key) =>
        d.TryGetValue(key, out var v) ? v : null;

    private static (double? Avg, double? Max, double LossPct) Stat(
        List<Sample> samples, Func<Sample, Dictionary<string, double>> mapOf, string key)
    {
        var vals = new List<double>();
        var loss = 0;
        foreach (var s in samples)
        {
            if (mapOf(s).TryGetValue(key, out var v)) vals.Add(v);
            else loss++;
        }
        return (
            vals.Count > 0 ? vals.Average() : null,
            vals.Count > 0 ? vals.Max() : null,
            samples.Count > 0 ? (double)loss / samples.Count : 0.0);
    }

    /// <summary>TTFB 低频探测：丢包率按"探测轮"统计（非探测秒不计入分母）</summary>
    private static (double? Avg, double? Max, double LossPct) TtfbStat(List<Sample> samples, string key)
    {
        var probes = 0;
        var loss = 0;
        var vals = new List<double>();
        foreach (var s in samples)
        {
            var isProbe = s.Ttfb.ContainsKey(key) || s.Failures.Contains($"{key}_ttfb");
            if (!isProbe) continue;
            probes++;
            if (s.Ttfb.TryGetValue(key, out var v)) vals.Add(v);
            else loss++;
        }
        return (
            vals.Count > 0 ? vals.Average() : null,
            vals.Count > 0 ? vals.Max() : null,
            probes > 0 ? (double)loss / probes : 0.0);
    }
}
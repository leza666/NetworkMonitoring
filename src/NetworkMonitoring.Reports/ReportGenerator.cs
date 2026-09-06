using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetworkMonitoring.Core;

namespace NetworkMonitoring.Reports;

/// <summary>静态 HTML 报表：当日报表（1 分钟聚合 + 统计表）与多日对比 overview</summary>
public static class ReportGenerator
{
    private const string KindLabelIcmp = "";
    private const string KindLabelTcp = "TCP";
    private const string KindLabelTtfb = "TTFB";

    private record Metric(string Mkey, string Key, string Kind, string Label);
    private record Row(long Ts, Dictionary<string, double?> V, List<string> Failures);
    private record Parsed(List<Metric> Metrics, List<Row> Rows);

    private static readonly Regex MetricRe = new(@"^(.+?)(?:_(tcp|ttfb))?_ms$", RegexOptions.Compiled);
    private static readonly Regex DateFileRe = new(@"^(\d{4})-(\d{2})-(\d{2})\.(csv|html)$", RegexOptions.Compiled);

    public static bool GenerateDaily(string? dateArg)
    {
        var day = dateArg ?? Util.DateKey(DateTime.Now);
        if (!Regex.IsMatch(day, @"^\d{4}-\d{2}-\d{2}$"))
        {
            Log.Error($"日期格式应为 YYYY-MM-DD: {day}");
            return false;
        }
        var csvPath = Path.Combine(Config.DataDir, $"{day}.csv");
        if (!File.Exists(csvPath))
        {
            Log.Error($"数据文件不存在: {csvPath}");
            return false;
        }
        var parsed = ParseCsv(File.ReadAllText(csvPath, Encoding.UTF8));
        if (parsed.Rows.Count == 0)
        {
            Log.Error($"{day} 无数据");
            return false;
        }
        if (parsed.Metrics.Count == 0)
        {
            Log.Error($"{day} CSV 无识别列（列: {File.ReadLines(csvPath).FirstOrDefault()?.Truncate(120)}…）");
            return false;
        }

        var sortedMin = parsed.Rows.Select(r => Util.MinuteKeyOf(DateTimeOffset.FromUnixTimeMilliseconds(r.Ts).LocalDateTime))
            .Distinct().OrderBy(x => x).ToList();
        var byMetric = new Dictionary<string, Dictionary<string, MinuteAgg>>();
        var statsByMetric = new Dictionary<string, MetricStat>();
        foreach (var m in parsed.Metrics)
        {
            byMetric[m.Mkey] = MinuteAggs(parsed.Rows, m);
            statsByMetric[m.Mkey] = OverallStat(parsed.Rows, m);
        }

        var panels = new StringBuilder();
        var chartData = new StringBuilder();
        foreach (var kind in new[] { "icmp", "tcp", "ttfb" })
        {
            var ms = parsed.Metrics.Where(m => m.Kind == kind).ToList();
            if (ms.Count == 0) continue;
            var kindLabel = KindLabelOf(kind);
            var unitNote = kind == "icmp" ? "ICMP ping" : kind == "tcp" ? "TCP 443 握手" : "HTTPS 首字节";
            var title = kind == "icmp" ? "ICMP 延迟" : kind == "tcp" ? "TCP 连接握手" : "HTTPS 首字节 TTFB";
            panels.Append($"<h2>{title} 统计（{unitNote}，按分钟聚合）</h2><div class=\"panel\">{StatTable(ms.Select(m => statsByMetric[m.Mkey]).ToList())}</div>");
            panels.Append($"<h2>{title} 平均延迟（ms/分钟）</h2><div class=\"panel\"><div class=\"chart-wrap\"><canvas id=\"c_{kind}_avg\"></canvas></div></div>");
            chartData.Append($"new Chart(document.getElementById(\"c_{kind}_avg\"),{{type:\"line\",data:{Json(Series(ms, sortedMin, byMetric, "avg"))},options:opts}});\n");
            if (kind == "icmp")
            {
                panels.Append("<h2>ICMP 丢包率（%/分钟）</h2><div class=\"panel\"><div class=\"chart-wrap\"><canvas id=\"c_icmp_loss\"></canvas></div></div>");
                chartData.Append($"new Chart(document.getElementById(\"c_icmp_loss\"),{{type:\"line\",data:{Json(Series(ms, sortedMin, byMetric, "lossPct"))},options:{{...opts,scales:{{...opts.scales,x:{{...opts.scales.x}},y:{{...opts.scales.y,suggestedMax:100}}}}}}}});\n");
            }
        }

        var body =
            $"<h1>网络监控报表 · {day}</h1>\n" +
            $"<div class=\"sub\">{parsed.Rows.Count:N0} 个采样点（1Hz；TCP 同频；TTFB 每 5 秒） · 丢包率=失败探测/探测次数（TTFB 按探测轮统计，非探测秒不计入）</div>\n" +
            $"{panels}\n" +
            "<script>\n" + JsOpts + "\n" + chartData + "\n</script>";

        Directory.CreateDirectory(Config.ReportsDir);
        var outPath = Path.Combine(Config.ReportsDir, $"{day}.html");
        File.WriteAllText(outPath, Shell(body), Encoding.UTF8);
        Log.Info($"报表已生成: {outPath}");
        return true;
    }

    public static bool GenerateOverview()
    {
        if (!Directory.Exists(Config.DataDir))
        {
            Log.Error("data/ 目录不存在");
            return false;
        }
        var files = Directory.EnumerateFiles(Config.DataDir)
            .Select(Path.GetFileName)
            .Where(f => f is not null && DateFileRe.IsMatch(f))
            .OrderBy(f => f)
            .ToList();
        if (files.Count == 0)
        {
            Log.Error("data/ 目录下没有任何 CSV 数据文件");
            return false;
        }

        var days = new List<string>();
        var perDay = new Dictionary<string, Dictionary<string, (int Count, double Avg, double Max, double LossPct)>>();
        foreach (var f in files)
        {
            var day = f![..10];
            var parsed = ParseCsv(File.ReadAllText(Path.Combine(Config.DataDir, f), Encoding.UTF8));
            if (parsed.Rows.Count == 0) continue;
            var obj = new Dictionary<string, (int, double, double, double)>();
            foreach (var m in parsed.Metrics)
            {
                if (m.Kind != "icmp") continue;
                var s = OverallStat(parsed.Rows, m);
                obj[m.Key] = (s.Count, s.Avg, s.Max, s.LossPct);
            }
            days.Add(day);
            perDay[day] = obj;
        }
        if (days.Count == 0)
        {
            Log.Error("没有可用的数据文件");
            return false;
        }

        var th = string.Concat(Config.Targets.Select(t =>
            $"<th>{Util.EscapeHtml(t.Label)}<br><small>avg/丢包率</small></th>"));
        var tr = string.Join("\n", days.Select(d =>
        {
            var o = perDay[d];
            return $"<tr><td style=\"text-align:left\">{d}</td>{string.Concat(Config.Targets.Select(t => o.TryGetValue(t.Key, out var v)
                ? $"<td>{Util.Fmt(v.Avg)}ms / {Util.Fmt(v.LossPct)}%</td>" : "<td>—</td>"))}</tr>";
        }));

        var body =
            "<h1>网络监控多日对比</h1>\n" +
            $"<div class=\"sub\">{days.Count} 天 · 每日按全天 ICMP 样本计算平均/最大/丢包率</div>\n" +
            $"<div class=\"panel\"><table><thead><tr><th style=\"text-align:left\">日期</th>{th}</tr></thead><tbody>{tr}</tbody></table></div>\n" +
            "<div class=\"panel\"><h2>每日平均延迟（ms）</h2><div class=\"chart-wrap\"><canvas id=\"cAvg\"></canvas></div></div>\n" +
            "<div class=\"panel\"><h2>每日最大延迟（ms）</h2><div class=\"chart-wrap\"><canvas id=\"cMax\"></canvas></div></div>\n" +
            "<div class=\"panel\"><h2>每日丢包率（%）</h2><div class=\"chart-wrap\"><canvas id=\"cLoss\"></canvas></div></div>\n" +
            "<script>\n" +
            $"const dataAvg={Json(OverviewSeries(days, perDay, "avg"))},dataMax={Json(OverviewSeries(days, perDay, "max"))},dataLoss={Json(OverviewSeries(days, perDay, "lossPct"))};\n" +
            JsOpts + "\n" + JsOverviewCharts + "\n</script>";

        Directory.CreateDirectory(Config.ReportsDir);
        var outPath = Path.Combine(Config.ReportsDir, "overview.html");
        File.WriteAllText(outPath, Shell(body), Encoding.UTF8);
        Log.Info($"多日对比报表已生成: {outPath}");
        return true;
    }

    // ---------- 解析与统计 ----------

    private static Parsed ParseCsv(string content)
    {
        var lines = content.Split('\n');
        if (lines.Length == 0) return new Parsed([], []);
        var header = lines[0].TrimEnd('\r').Split(',');
        var colOf = new Dictionary<string, int>();
        for (var i = 0; i < header.Length; i++) colOf[header[i].Trim()] = i;
        if (!colOf.TryGetValue("timestamp", out var colTs)) return new Parsed([], []);

        var metrics = new List<Metric>();
        foreach (var h in header)
        {
            var m = MetricRe.Match(h.Trim());
            if (!m.Success) continue;
            var key = m.Groups[1].Value;
            var kind = m.Groups[2].Value switch
            {
                "tcp" => "tcp",
                "ttfb" => "ttfb",
                _ => "icmp",
            };
            var baseTarget = Config.Targets.FirstOrDefault(t => t.Key == key);
            var baseLabel = baseTarget?.Label ?? key;
            var label = kind == "icmp" ? baseLabel : $"{baseLabel} {KindLabelOf(kind)}";
            metrics.Add(new Metric(h.Trim(), key, kind, label));
        }

        var rows = new List<Row>();
        var colFailures = colOf.GetValueOrDefault("failures", -1);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0) continue;
            var f = line.Split(',');
            if (colTs >= f.Length || f[colTs].Length == 0) continue;
            if (!DateTimeOffset.TryParse(f[colTs], out var ts)) continue;
            var v = new Dictionary<string, double?>();
            foreach (var metric in metrics)
            {
                var idx = colOf.GetValueOrDefault(metric.Mkey, -1);
                var raw = idx >= 0 && idx < f.Length ? f[idx] : "";
                v[metric.Mkey] = raw.Length == 0 ? null : double.TryParse(raw, out var n) ? n : null;
            }
            var failures = colFailures >= 0 && colFailures < f.Length && f[colFailures].Length > 0
                ? f[colFailures].Split('|').Where(s => s.Length > 0).ToList()
                : new List<string>();
            rows.Add(new Row(ts.ToUnixTimeMilliseconds(), v, failures));
        }
        return new Parsed(metrics, rows);
    }

    private static double JitterOf(List<double> vals)
    {
        if (vals.Count < 2) return 0;
        var sum = 0.0;
        for (var i = 1; i < vals.Count; i++) sum += Math.Abs(vals[i] - vals[i - 1]);
        return sum / (vals.Count - 1);
    }

    /// <summary>TTFB 低频探测：该行是否为该指标的探测轮（非探测秒不计入丢包率分母）</summary>
    private static bool IsProbeRow(Row r, Metric metric) =>
        metric.Kind != "ttfb" || r.V[metric.Mkey].HasValue || r.Failures.Contains($"{metric.Key}_ttfb");

    private static MetricStat OverallStat(List<Row> rows, Metric metric)
    {
        var ok = new List<double>();
        var probes = 0;
        var loss = 0;
        foreach (var r in rows)
        {
            if (!IsProbeRow(r, metric)) continue;
            probes++;
            var v = r.V[metric.Mkey];
            if (v.HasValue) ok.Add(v.Value);
            else loss++;
        }
        return new MetricStat
        {
            Mkey = metric.Mkey,
            Label = metric.Label,
            Count = rows.Count,
            ProbeCount = probes,
            OkCount = ok.Count,
            Avg = ok.Count > 0 ? ok.Average() : 0,
            Min = ok.Count > 0 ? ok.Min() : 0,
            Max = ok.Count > 0 ? ok.Max() : 0,
            LossPct = probes > 0 ? (double)loss / probes * 100 : 0,
            JitterMs = JitterOf(ok),
        };
    }

    private static Dictionary<string, MinuteAgg> MinuteAggs(List<Row> rows, Metric metric)
    {
        var acc = new Dictionary<string, (List<double> OkVals, int Probes)>();
        foreach (var r in rows)
        {
            if (!IsProbeRow(r, metric)) continue;
            var mk = Util.MinuteKeyOf(DateTimeOffset.FromUnixTimeMilliseconds(r.Ts).LocalDateTime);
            if (!acc.TryGetValue(mk, out var a)) a = (new List<double>(), 0);
            a.Probes++;
            var v = r.V[metric.Mkey];
            if (v.HasValue) a.OkVals.Add(v.Value);
            acc[mk] = a;
        }
        var outMap = new Dictionary<string, MinuteAgg>();
        foreach (var (mk, a) in acc)
        {
            outMap[mk] = new MinuteAgg
            {
                Count = a.Probes,
                Probes = a.Probes,
                Avg = a.OkVals.Count > 0 ? a.OkVals.Average() : 0,
                Min = a.OkVals.Count > 0 ? a.OkVals.Min() : 0,
                Max = a.OkVals.Count > 0 ? a.OkVals.Max() : 0,
                LossPct = a.Probes > 0 ? (double)(a.Probes - a.OkVals.Count) / a.Probes * 100 : 0,
                JitterMs = JitterOf(a.OkVals),
            };
        }
        return outMap;
    }

    // ---------- HTML 生成 ----------

    private static string KindLabelOf(string kind) => kind switch
    {
        "tcp" => KindLabelTcp,
        "ttfb" => KindLabelTtfb,
        _ => KindLabelIcmp,
    };

    private static string ColorOf(string key) => Config.Colors.GetValueOrDefault(key, "#888");

    private static string BaseKeyOf(string mkey) => Regex.Replace(mkey, @"_(tcp|ttfb)?_ms$", "");

    private static string StatTable(List<MetricStat> stats)
    {
        var rows = string.Join("\n", stats.Select(s =>
            $"""
            <tr>
              <td style="text-align:left"><span style="display:inline-block;width:10px;height:10px;border-radius:50%;background:{ColorOf(BaseKeyOf(s.Mkey))};margin-right:6px"></span>{Util.EscapeHtml(s.Label)}</td>
              <td>{s.Count}</td>
              <td>{s.ProbeCount}</td>
              <td>{s.OkCount}</td>
              <td>{Util.Fmt(s.Avg)} ms</td>
              <td>{Util.Fmt(s.Min)} ms</td>
              <td>{Util.Fmt(s.Max)} ms</td>
              <td>{Util.Fmt(s.JitterMs)} ms</td>
              <td>{Util.Fmt(s.LossPct)}%</td>
            </tr>
            """));
        return $"""
            <table>
              <thead><tr><th>指标</th><th>样本数</th><th>探测次数</th><th>有效</th><th>平均</th><th>最小</th><th>最大</th><th>抖动</th><th>丢包率</th></tr></thead>
              <tbody>{rows}</tbody>
            </table>
            """;
    }

    private static object Series(
        List<Metric> metrics, List<string> sortedMin,
        Dictionary<string, Dictionary<string, MinuteAgg>> byMetric, string field)
    {
        var datasets = metrics.Select(m =>
        {
            var agg = byMetric[m.Mkey];
            return new
            {
                label = m.Label,
                data = sortedMin.Select(mk => agg.TryGetValue(mk, out var a)
                    ? Math.Round(field == "lossPct" ? a.LossPct : field == "avg" ? a.Avg : a.Max, 2)
                    : (double?)null).ToArray(),
                borderColor = ColorOf(m.Key),
                backgroundColor = ColorOf(m.Key) + "22",
                borderWidth = 1.5,
                pointRadius = 0,
                tension = 0.15,
                spanGaps = true,
            };
        }).ToArray();
        return new { labels = sortedMin.Select(Util.HmOfMinuteKey).ToArray(), datasets };
    }

    private static object OverviewSeries(
        List<string> days, Dictionary<string, Dictionary<string, (int Count, double Avg, double Max, double LossPct)>> perDay,
        string field)
    {
        var datasets = Config.Targets.Select(t =>
        {
            double? Pick(string d)
            {
                if (!perDay.TryGetValue(d, out var o) || !o.TryGetValue(t.Key, out var v)) return null;
                var val = field switch { "avg" => v.Avg, "max" => v.Max, _ => v.LossPct };
                return Math.Round(double.IsFinite(val) ? val : 0, 2);
            }
            return new
            {
                label = t.Label,
                data = days.Select(Pick).ToArray(),
                borderColor = ColorOf(t.Key),
                backgroundColor = ColorOf(t.Key) + "22",
                borderWidth = 1.5,
                pointRadius = 2,
                tension = 0.15,
                spanGaps = true,
            };
        }).ToArray();
        return new { labels = days.ToArray(), datasets };
    }

    /// <summary>JSON 序列化，转义 &lt; 防止 HTML 注入</summary>
    private static string Json(object x) =>
        JsonSerializer.Serialize(x, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            .Replace("<", "\\u003c");

    private static string Shell(string inner) => ShellTemplate.Replace("<!--INNER-->", inner);

    private const string ShellTemplate =
"""
<!DOCTYPE html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>网络监控报表</title>
<script src="../assets/chart.min.js"></script>
<style>
:root{color-scheme:dark}
*{box-sizing:border-box;margin:0;padding:0}
body{font-family:"Segoe UI","Microsoft YaHei",sans-serif;background:#0f1419;color:#e6e6e6;padding:20px}
h1{font-size:20px;margin-bottom:4px}
h2{font-size:15px;color:#c9d1d9;margin:20px 0 8px}
.sub{color:#8b949e;font-size:12px;margin-bottom:14px}
.panel{background:#161b22;border:1px solid #30363d;border-radius:10px;padding:14px;margin-bottom:16px}
.chart-wrap{position:relative;height:300px}
table{width:100%;border-collapse:collapse;font-size:13px}
th,td{padding:6px 10px;border-bottom:1px solid #21262d;text-align:right}
th{color:#8b949e;font-weight:600}
td:first-child{text-align:left}
tbody tr:hover{background:#1c2128}
</style></head><body>
<!--INNER-->
</body></html>
""";

    private const string JsOpts =
"""
const opts={animation:false,interaction:{mode:"index",intersect:false},
  plugins:{legend:{labels:{color:"#c9d1d9",boxWidth:10,font:{size:11}}},tooltip:{mode:"index",intersect:false}},
  scales:{x:{ticks:{color:"#8b949e",maxRotation:0,maxTicksLimit:12},grid:{color:"#21262d"}},y:{ticks:{color:"#8b949e"},grid:{color:"#21262d"}}}};
""";

    private const string JsOverviewCharts =
"""
new Chart(document.getElementById("cAvg"),{type:"line",data:dataAvg,options:opts});
new Chart(document.getElementById("cMax"),{type:"line",data:dataMax,options:opts});
new Chart(document.getElementById("cLoss"),{type:"line",data:dataLoss,options:{...opts,
 scales:{...opts.scales,x:{...opts.scales.x},y:{...opts.scales.y,suggestedMax:100}}}});
""";
}

file static class StringExt
{
    public static string Truncate(this string s, int max) => s.Length <= max ? s : s[..max];
}
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Hosting;

namespace NetworkMonitoring;

/// <summary>1Hz 并发采集核心（ICMP/TCP 1Hz、TTFB 5s）+ 内存环形缓冲 + CSV 写入 + 清理</summary>
public class Collector : IHostedService
{
    private static readonly string Header = BuildHeader();

    private readonly List<Sample> _samples = new();
    private readonly object _sync = new();
    private StreamWriter? _writer;
    private string _day = "";
    private long _roundCount;
    private readonly Dictionary<string, string> _gatewayCache = new();
    private long _lastGatewayResolve;
    private long _lastResolveFail;
    private string? _lastGateway;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public List<Sample> SamplesInRange(long fromTs, long toTs = long.MaxValue)
    {
        lock (_sync)
        {
            var outList = new List<Sample>();
            foreach (var s in _samples)
            {
                if (s.Ts < fromTs) continue;
                if (s.Ts > toTs) break;
                outList.Add(s);
            }
            return outList;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = Task.Run(() => RunLoop(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_loopTask is null) return;
        try
        {
            await _loopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            Log.Error($"[采集] 停止异常: {e.Message}");
        }
    }

    private async Task RunLoop(CancellationToken ct)
    {
        Util.CleanOldFiles();
        while (!ct.IsCancellationRequested)
        {
            var startedAt = DateTime.Now;
            var sw = Stopwatch.StartNew();
            try
            {
                EnsureFileFor(startedAt);
                await RunRoundAsync(startedAt, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                Log.Error($"[采集] 轮次异常: {e.Message}");
            }
            sw.Stop();
            var wait = Config.SampleIntervalMs - (int)sw.ElapsedMilliseconds;
            if (wait > 0)
            {
                try
                {
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        lock (_sync)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private async Task RunRoundAsync(DateTime now, CancellationToken ct)
    {
        var icmp = new Dictionary<string, double>();
        var tcp = new Dictionary<string, double>();
        var ttfb = new Dictionary<string, double>();
        var failures = new List<string>();
        var doTtfb = _roundCount % Config.TtfbIntervalRounds == 0;

        var jobs = new List<Task>();
        foreach (var t in Config.Targets) jobs.Add(ProbeIcmpAsync(t, icmp, failures));
        foreach (var t in Config.TcpTargets) jobs.Add(ProbeTcpAsync(t, tcp, failures));
        if (doTtfb)
        {
            foreach (var t in Config.TtfbTargets) jobs.Add(ProbeTtfbAsync(t, ttfb, failures));
        }
        await Task.WhenAll(jobs).WaitAsync(ct).ConfigureAwait(false);

        var sample = new Sample
        {
            Timestamp = Util.FormatLocalIso(now),
            Ts = new DateTimeOffset(now).ToUnixTimeMilliseconds(),
        };
        foreach (var kv in icmp) sample.Icmp[kv.Key] = kv.Value;
        foreach (var kv in tcp) sample.Tcp[kv.Key] = kv.Value;
        foreach (var kv in ttfb) sample.Ttfb[kv.Key] = kv.Value;
        sample.Failures.AddRange(failures);

        PushSample(sample);
        WriteRow(sample);
        _roundCount++;

        var parts = Config.Targets.Select(t =>
            icmp.TryGetValue(t.Key, out var v) ? $"{t.Key}={v:F1}" : $"{t.Key}=✗");
        var tcpParts = Config.TcpTargets.Select(t =>
            tcp.TryGetValue(t.Key, out var v) ? $"tcp:{t.Key}={v:F0}" : $"tcp:{t.Key}=✗");
        var ttfbParts = doTtfb
            ? Config.TtfbTargets.Select(t =>
                ttfb.TryGetValue(t.Key, out var v) ? $"ttfb:{t.Key}={v:F0}" : $"ttfb:{t.Key}=✗")
            : Array.Empty<string>();
        var failTag = failures.Count > 0 ? $"  [失败: {string.Join(",", failures)}]" : "";
        Log.Info($"{Util.HmsOf(now)}  #{_roundCount}  {string.Join("  ", parts)}  {string.Join("  ", tcpParts)}  {string.Join("  ", ttfbParts)}{failTag}");
    }

    private async Task ProbeIcmpAsync(TargetSpec t, Dictionary<string, double> icmp, List<string> failures)
    {
        var addr = t.Kind == TargetKind.Gateway ? await ResolveGatewayIpAsync() : t.Host;
        if (addr is null)
        {
            failures.Add(t.Key);
            return;
        }
        var r = await PingProbe.PingAsync(addr).ConfigureAwait(false);
        if (r.Ok && r.Ms.HasValue) icmp[t.Key] = r.Ms.Value;
        else failures.Add(t.Key);
    }

    /// <summary>探测并缓存默认网关 IP（60s 重查）；失败时静默抑制日志 30s，临时沿用旧 IP</summary>
    private async Task<string?> ResolveGatewayIpAsync()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _gatewayCache.TryGetValue("gateway", out var cached);
        if (cached is not null && nowMs - _lastGatewayResolve < Config.GatewayResolveMs) return cached;
        try
        {
            var ip = await Task.Run(GatewayDetector.GetDefaultGateway).ConfigureAwait(false);
            if (ip is null) throw new InvalidOperationException("未找到默认网关");
            _gatewayCache["gateway"] = ip;
            _lastGatewayResolve = nowMs;
            _lastResolveFail = 0;
            if (_lastGateway is null) Log.Info($"[网关] 默认网关: {ip}");
            else if (_lastGateway != ip) Log.Warn($"[网关] 默认网关变化: {_lastGateway} -> {ip}");
            _lastGateway = ip;
            return ip;
        }
        catch (Exception e)
        {
            if (nowMs - _lastResolveFail >= Config.ResolveRetryMs)
            {
                _lastResolveFail = nowMs;
                Log.Warn($"[网关] 探测失败: {e.Message}");
            }
            return cached;
        }
    }

    private static async Task ProbeTcpAsync(TargetSpec t, Dictionary<string, double> tcp, List<string> failures)
    {
        if (t.Host is null)
        {
            failures.Add($"{t.Key}_tcp");
            return;
        }
        var r = await TcpProbe.TcpConnectMsAsync(t.Host, Config.TcpPort, Config.TcpTimeoutMs).ConfigureAwait(false);
        if (r.Ok && r.Ms.HasValue) tcp[t.Key] = r.Ms.Value;
        else failures.Add($"{t.Key}_tcp");
    }

    private static async Task ProbeTtfbAsync(TargetSpec t, Dictionary<string, double> ttfb, List<string> failures)
    {
        if (t.Host is null)
        {
            failures.Add($"{t.Key}_ttfb");
            return;
        }
        var r = await TtfbProbe.TtfbMsAsync(t.Host, Config.TcpPort, Config.TtfbTimeoutMs).ConfigureAwait(false);
        if (r.Ok && r.Ms.HasValue) ttfb[t.Key] = r.Ms.Value;
        else failures.Add($"{t.Key}_ttfb");
    }

    private void PushSample(Sample s)
    {
        lock (_sync)
        {
            _samples.Add(s);
            var cutoff = s.Ts - Config.BufferWindowMs;
            while (_samples.Count > 0 && _samples[0].Ts < cutoff) _samples.RemoveAt(0);
        }
    }

    private void EnsureFileFor(DateTime now)
    {
        var d = Util.DateKey(now);
        if (_day == d) return;
        var dayChanged = _day != "";
        lock (_sync)
        {
            _writer?.Dispose();
            _writer = null;
            _day = d;
        }
        Directory.CreateDirectory(Config.DataDir);
        var p = Path.Combine(Config.DataDir, $"{d}.csv");
        var isNew = !File.Exists(p);
        var writer = new StreamWriter(p, append: true, new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
        lock (_sync)
        {
            _writer = writer;
        }
        if (isNew)
        {
            writer.WriteLine(Header);
            Log.Info($"[采集] 新建当日数据文件: {p}");
        }
        if (dayChanged)
        {
            Log.Info($"[采集] 已切换数据文件: {p}");
            Util.CleanOldFiles();
        }
    }

    private void WriteRow(Sample s)
    {
        var cols = new List<string> { s.Timestamp };
        foreach (var t in Config.Targets)
            cols.Add(s.Icmp.TryGetValue(t.Key, out var v) ? v.ToString("0.##", CultureInfo.InvariantCulture) : "");
        foreach (var t in Config.TcpTargets)
            cols.Add(s.Tcp.TryGetValue(t.Key, out var v) ? v.ToString("0.##", CultureInfo.InvariantCulture) : "");
        foreach (var t in Config.TtfbTargets)
            cols.Add(s.Ttfb.TryGetValue(t.Key, out var v) ? v.ToString("0.##", CultureInfo.InvariantCulture) : "");
        cols.Add(string.Join("|", s.Failures));
        var line = string.Join(",", cols);
        try
        {
            lock (_sync)
            {
                _writer?.WriteLine(line);
            }
        }
        catch (Exception e)
        {
            Log.Error($"[采集] CSV 写入失败: {e.Message}");
        }
    }

    private static string BuildHeader()
    {
        var cols = new List<string> { "timestamp" };
        cols.AddRange(Config.Targets.Select(t => $"{t.Key}_ms"));
        cols.AddRange(Config.TcpTargets.Select(t => $"{t.Key}_tcp_ms"));
        cols.AddRange(Config.TtfbTargets.Select(t => $"{t.Key}_ttfb_ms"));
        cols.Add("failures");
        return string.Join(",", cols);
    }
}
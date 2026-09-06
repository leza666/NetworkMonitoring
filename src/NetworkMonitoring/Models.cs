namespace NetworkMonitoring;

public enum TargetKind
{
    Gateway,
    Ip,
    Dns,
}

public class TargetSpec
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required TargetKind Kind { get; init; }
    public string? Host { get; init; }
    public bool Tcp { get; init; }
    public bool Ttfb { get; init; }
}

public record ProbeResult(bool Ok, double? Ms);

/// <summary>一次采样轮（通常 1 秒一轮，TTFB 5 秒一轮）的所有结果</summary>
public class Sample
{
    public required string Timestamp { get; init; }
    public required long Ts { get; init; }
    public Dictionary<string, double> Icmp { get; } = new();
    public Dictionary<string, double> Tcp { get; } = new();
    public Dictionary<string, double> Ttfb { get; } = new();
    public List<string> Failures { get; } = new();
}

public class MinuteAgg
{
    public int Count { get; set; }
    public int Probes { get; set; }
    public double Avg { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public double LossPct { get; set; }
    public double JitterMs { get; set; }
}

public class MetricStat
{
    public required string Mkey { get; init; }
    public required string Label { get; init; }
    public int Count { get; set; }
    public int ProbeCount { get; set; }
    public int OkCount { get; set; }
    public double Avg { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public double LossPct { get; set; }
    public double JitterMs { get; set; }
}
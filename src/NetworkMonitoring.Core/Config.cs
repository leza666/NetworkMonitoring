namespace NetworkMonitoring.Core;

public static class Config
{
    public static readonly TargetSpec[] Targets =
    [
        new() { Key = "gateway", Label = "网关(路由器)", Kind = TargetKind.Gateway },
        new() { Key = "modem", Label = "光猫 192.168.1.1", Kind = TargetKind.Ip, Host = "192.168.1.1" },
        new() { Key = "baidu", Label = "百度", Kind = TargetKind.Dns, Host = "www.baidu.com", Tcp = true, Ttfb = true },
        new() { Key = "bilibili", Label = "哔哩哔哩", Kind = TargetKind.Dns, Host = "www.bilibili.com", Tcp = true, Ttfb = true },
        new() { Key = "alibaba", Label = "阿里 DNS 223.5.5.5", Kind = TargetKind.Ip, Host = "223.5.5.5" },
    ];

    public static readonly TargetSpec[] TcpTargets = Targets.Where(t => t.Tcp).ToArray();
    public static readonly TargetSpec[] TtfbTargets = Targets.Where(t => t.Ttfb).ToArray();

    public static readonly Dictionary<string, string> Colors = new()
    {
        ["gateway"] = "#4facfe",
        ["modem"] = "#ffd43b",
        ["baidu"] = "#ff6b6b",
        ["bilibili"] = "#ffa94d",
        ["alibaba"] = "#2bd9af",
    };

    public const int TtfbIntervalRounds = 5;
    public const int TcpPort = 443;
    public const int SampleIntervalMs = 1000;
    public const int RetentionDays = 90;
    public const int DashboardPort = 8000;
    public const long BufferWindowMs = 24 * 60 * 60 * 1000;
    public const int PingTimeoutMs = 4000;
    public const int TcpTimeoutMs = 3000;
    public const int TtfbTimeoutMs = 6000;
    public const int GatewayResolveMs = 60_000;
    public const int ResolveRetryMs = 30_000;
    public const string ServiceName = "NetworkMonitor";

    public static readonly string BaseDir = AppContext.BaseDirectory;
    public static readonly string DataDir = Path.Combine(BaseDir, "data");
    public static readonly string ReportsDir = Path.Combine(BaseDir, "reports");
    public static readonly string AssetsDir = Path.Combine(BaseDir, "assets");
    public static readonly string LogsDir = Path.Combine(BaseDir, "logs");
}
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetworkMonitoring;

/// <summary>ICMP ping 探针（原生 Ping API，域名由系统解析，无编码问题）</summary>
public static class PingProbe
{
    public static async Task<ProbeResult> PingAsync(string host, int timeoutMs = Config.PingTimeoutMs)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, timeoutMs).ConfigureAwait(false);
            return reply.Status == IPStatus.Success
                ? new ProbeResult(true, reply.RoundtripTime)
                : new ProbeResult(false, null);
        }
        catch
        {
            return new ProbeResult(false, null);
        }
    }
}

/// <summary>TCP connect(443) 握手耗时 ≈ 网络 RTT + 服务器握手处理</summary>
public static class TcpProbe
{
    public static async Task<ProbeResult> TcpConnectMsAsync(string host, int port, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(timeoutMs);
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return new ProbeResult(true, sw.Elapsed.TotalMilliseconds);
        }
        catch
        {
            return new ProbeResult(false, null);
        }
    }
}

/// <summary>HTTPS GET 首字节(TTFB)耗时 = 连接 + TLS + 服务端首包返回</summary>
public static class TtfbProbe
{
    private const string Ua =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromSeconds(60),
        };
        var c = new HttpClient(handler);
        c.DefaultRequestHeaders.UserAgent.ParseAdd(Ua);
        return c;
    }

    public static async Task<ProbeResult> TtfbMsAsync(string host, int port, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{host}:{port}/");
            req.Headers.TryAddWithoutValidation("Accept", "*/*");
            using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            return new ProbeResult(true, sw.Elapsed.TotalMilliseconds);
        }
        catch
        {
            return new ProbeResult(false, null);
        }
    }
}
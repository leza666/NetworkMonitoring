using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetworkMonitoring;

var cliArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();

// SCM 启动（服务模式）自动分流；命令行进入交互模式
if (!Environment.UserInteractive)
{
    await RunApp(serviceMode: true);
    return 0;
}

switch (cliArgs.FirstOrDefault())
{
    case "collect":
        await RunApp(serviceMode: false);
        return 0;
    case "report":
    {
        if (cliArgs.Contains("--all") || cliArgs.Contains("-a"))
            return ReportGenerator.GenerateOverview() ? 0 : 1;
        var dateArg = cliArgs.Skip(1).FirstOrDefault(a => !a.StartsWith("-"));
        return ReportGenerator.GenerateDaily(dateArg) ? 0 : 1;
    }
    case "install":
        return ServiceInstaller.Install() ? 0 : 1;
    case "uninstall":
    case "-u":
        return ServiceInstaller.Uninstall() ? 0 : 1;
    default:
        Console.WriteLine(
            """
            === 家庭网络监控 ===
            用法:
              NetworkMonitoring collect            前台采集 + 实时看板（Ctrl+C 停止）
              NetworkMonitoring report [日期|--all] 生成静态报表（默认当天；--all 多日对比）
              NetworkMonitoring install            注册并启动 Windows 服务（管理员）
              NetworkMonitoring uninstall          停止并删除服务
            看板: http://localhost:8000
            """);
        return 1;
}

static async Task RunApp(bool serviceMode)
{
    var builder = WebApplication.CreateBuilder();
    if (serviceMode)
    {
        builder.Host.UseWindowsService(o => o.ServiceName = Config.ServiceName);
    }
    else
    {
        Console.WriteLine("=== 家庭网络监控采集器 ===");
        Console.WriteLine("监测目标: " + string.Join("、", Config.Targets
            .Select(t => $"{t.Label}{(t.Host is not null ? $" ({t.Host})" : "")}")));
        Console.WriteLine($"采样频率: 1Hz  |  看板: http://localhost:{Config.DashboardPort}  |  Ctrl+C 停止");
        Console.WriteLine("----------------------------------------");
    }
    ((IWebHostBuilder)builder.WebHost).UseUrls($"http://127.0.0.1:{Config.DashboardPort}");
    builder.Services.AddSingleton<Collector>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<Collector>());
    var app = builder.Build();
    app.MapDashboard(app.Services.GetRequiredService<Collector>());
    await app.RunAsync();
}
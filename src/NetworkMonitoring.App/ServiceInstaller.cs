using System.Diagnostics;
using NetworkMonitoring.Core;

namespace NetworkMonitoring.App;

/// <summary>Windows 服务注册/卸载（sc.exe，需要管理员权限；默认 LocalSystem）</summary>
public static class ServiceInstaller
{
    public static bool Install()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            Log.Error("无法确定可执行文件路径");
            return false;
        }

        // dotnet run 场景下 ProcessPath 是 dotnet.exe，需要带上 dll 参数
        var isDotnetHost = Path.GetFileNameWithoutExtension(exe)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        var binPathValue = isDotnetHost
            ? $"\"{exe}\" \"{typeof(ServiceInstaller).Assembly.Location}\""
            : $"\"{exe}\"";

        Log.Info($"注册服务 {Config.ServiceName} …");
        var ok = RunSc("create", Config.ServiceName, "binPath=", binPathValue, "start=", "auto");
        if (!ok)
        {
            Log.Error($"服务注册失败（通常需管理员权限）：请用管理员 PowerShell 重试");
            return false;
        }
        RunSc("start", Config.ServiceName);
        Log.Info($"完成：服务 {Config.ServiceName} 已注册并启动（默认 LocalSystem）。");
        Log.Info($"看板地址: http://localhost:{Config.DashboardPort}");
        return true;
    }

    public static bool Uninstall()
    {
        RunSc("stop", Config.ServiceName);
        var ok = RunSc("delete", Config.ServiceName);
        if (!ok) Log.Warn("服务删除失败（可能不存在，或需要管理员权限）");
        Log.Info("采集器若正在运行需手动结束。");
        return ok;
    }

    private static bool RunSc(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return false;
            var outText = p.StandardOutput.ReadToEnd();
            var errText = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (!string.IsNullOrWhiteSpace(outText)) Log.Info(outText.Trim());
            if (!string.IsNullOrWhiteSpace(errText)) Log.Warn(errText.Trim());
            return p.ExitCode == 0;
        }
        catch (Exception e)
        {
            Log.Error($"sc.exe 执行失败: {e.Message}");
            return false;
        }
    }
}
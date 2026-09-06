using System.Text;

namespace NetworkMonitoring;

/// <summary>控制台（前台模式）+ 文件（按天轮转，服务模式必需）日志</summary>
public static class Log
{
    private static readonly object Sync = new();
    private static StreamWriter? _file;
    private static string _day = "";
    private static readonly bool ConsoleEnabled = Environment.UserInteractive;

    private static void EnsureFile()
    {
        var day = Util.DateKey(DateTime.Now);
        if (_file != null && _day == day) return;
        _file?.Dispose();
        _day = day;
        Directory.CreateDirectory(Config.LogsDir);
        var path = Path.Combine(Config.LogsDir, $"app-{day}.log");
        _file = new StreamWriter(path, append: true, new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
    }

    public static void WriteLine(string level, string message)
    {
        var line = $"{Util.FormatLocalIso(DateTime.Now)} [{level}] {message}";
        lock (Sync)
        {
            if (ConsoleEnabled) Console.WriteLine(line);
            try
            {
                EnsureFile();
                _file!.WriteLine(line);
            }
            catch
            {
                // 日志失败不影响采集
            }
        }
    }

    public static void Info(string message) => WriteLine("INFO", message);
    public static void Warn(string message) => WriteLine("WARN", message);
    public static void Error(string message) => WriteLine("ERROR", message);
}
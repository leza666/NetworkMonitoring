using System.Globalization;
using System.Text;

namespace NetworkMonitoring;

public static class Util
{
    private static string Pad(int n, int width = 2) => n.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');

    /// <summary>本地日期键，如 2026-09-02</summary>
    public static string DateKey(DateTime d) => $"{d.Year:0000}-{Pad(d.Month)}-{Pad(d.Day)}";

    private static string TzOffset(DateTime d)
    {
        var off = TimeZoneInfo.Local.GetUtcOffset(d);
        var sign = off < TimeSpan.Zero ? "-" : "+";
        return $"{sign}{Pad(Math.Abs(off.Hours))}:{Pad(Math.Abs(off.Minutes))}";
    }

    /// <summary>本地时间 ISO 字符串，带时区偏移，如 2026-09-02T12:00:00.123+08:00</summary>
    public static string FormatLocalIso(DateTime d) =>
        $"{d.Year:0000}-{Pad(d.Month)}-{Pad(d.Day)}T{Pad(d.Hour)}:{Pad(d.Minute)}:{Pad(d.Second)}.{d.Millisecond:000}{TzOffset(d)}";

    /// <summary>HH:MM:SS</summary>
    public static string HmsOf(DateTime d) => $"{Pad(d.Hour)}:{Pad(d.Minute)}:{Pad(d.Second)}";

    /// <summary>HH:MM（从分钟键 "yyyy-MM-ddTHH:mm" 取）</summary>
    public static string HmOfMinuteKey(string minuteKey) => minuteKey[11..];

    /// <summary>分钟键 "yyyy-MM-ddTHH:mm"</summary>
    public static string MinuteKeyOf(DateTime d) =>
        $"{d.Year:0000}-{Pad(d.Month)}-{Pad(d.Day)}T{Pad(d.Hour)}:{Pad(d.Minute)}";

    public static string EscapeHtml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public static string Fmt(double v, int digits = 1) =>
        double.IsFinite(v) ? v.ToString($"F{digits}", CultureInfo.InvariantCulture) : "0.0";

    /// <summary>删除 data/ 与 reports/ 下超过留存天数的按日期命名的文件</summary>
    public static void CleanOldFiles()
    {
        var cutoff = DateTime.Now.AddDays(-Config.RetentionDays);
        foreach (var dir in new[] { Config.DataDir, Config.ReportsDir })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(f);
                var m = System.Text.RegularExpressions.Regex.Match(name, @"^(\d{4})-(\d{2})-(\d{2})\.(csv|html)$");
                if (!m.Success) continue;
                try
                {
                    var fileDate = new DateTime(
                        int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
                    if (fileDate < cutoff)
                    {
                        File.Delete(f);
                        Log.Info($"[clean] 已删除过期文件: {f}");
                    }
                }
                catch (Exception e)
                {
                    Log.Warn($"[clean] 删除失败 {f}: {e.Message}");
                }
            }
        }
    }

    /// <summary>解析 CSV 行：按逗号切分，支持引号字段（本系统字段无逗号，简单切分即可）</summary>
    public static string[] SplitCsvLine(string line) => line.Split(',');

    /// <summary>标准 JSON 数字格式化（与参考 Node 一致：整数无小数点）</summary>
    public static string JsonNum(double? v) => v is null ? "null" : v.Value.ToString("0.##", CultureInfo.InvariantCulture);
}
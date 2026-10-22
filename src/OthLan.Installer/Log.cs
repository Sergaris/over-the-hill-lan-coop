using System;
using System.IO;

namespace OthLan.Installer;

static class Log
{
    static readonly string FilePath = MakePath();
    public static string PathOnDisk => FilePath;
    public static string Dir => Path.GetDirectoryName(FilePath);
    /// <summary>GUI subscribes here (level, line).</summary>
    public static event Action<string, string> Line;

    static string MakePath()
    {
        var name = $"OTH-LAN-Setup-{DateTime.Now:yyyyMMdd-HHmmss}.log";
        foreach (var dir in new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OTH-LAN", "logs"),
            AppContext.BaseDirectory, Path.GetTempPath() })
        {
            try { Directory.CreateDirectory(dir); var p = Path.Combine(dir, name); File.AppendAllText(p, ""); return p; } catch { }
        }
        return Path.Combine(Path.GetTempPath(), name);
    }

    static void W(string lvl, string msg, ConsoleColor c)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {lvl} {msg}";
        ConsoleColor? old = null;
        try { old = Console.ForegroundColor; Console.ForegroundColor = c; } catch { }
        try { Console.WriteLine(line); } catch { }
        try { if (old != null) Console.ForegroundColor = old.Value; } catch { }
        try { File.AppendAllText(FilePath, line + Environment.NewLine); } catch { }
        try { Line?.Invoke(lvl.Trim(), line); } catch { }
    }
    public static void Info(string m) => W("INFO ", m, ConsoleColor.Gray);
    public static void Ok(string m) => W("OK   ", m, ConsoleColor.Green);
    public static void Warn(string m) => W("WARN ", m, ConsoleColor.Yellow);
    public static void Err(string m) => W("ERROR", m, ConsoleColor.Red);
}

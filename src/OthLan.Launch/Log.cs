using System;
using System.IO;

namespace OthLan.Launch;

static class Log
{
    static readonly string FilePath = MakePath();
    public static string PathOnDisk => FilePath;

    static string MakePath()
    {
        var name = $"OTH-LAN-Launch-{DateTime.Now:yyyyMMdd-HHmmss}.log";
        foreach (var dir in new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OTH-LAN", "logs"),
            AppContext.BaseDirectory, Path.GetTempPath() })
        {
            try { Directory.CreateDirectory(dir); var p = Path.Combine(dir, name); File.AppendAllText(p, ""); return p; } catch { }
        }
        return Path.Combine(Path.GetTempPath(), name);
    }

    static void W(string lvl, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {lvl} {msg}";
        try { Console.WriteLine(line); } catch { }
        try { File.AppendAllText(FilePath, line + Environment.NewLine); } catch { }
    }
    public static void Info(string m) => W("INFO ", m);
    public static void Ok(string m) => W("OK   ", m);
    public static void Warn(string m) => W("WARN ", m);
    public static void Err(string m) => W("ERROR", m);
}

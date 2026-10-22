using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace OthLan.Installer;

static class Steam
{
    public static string SteamRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var (hive, key, val) in new[]
            {
                (Microsoft.Win32.Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
            })
            {
                try
                {
                    if (hive.OpenSubKey(key)?.GetValue(val) is string p && Directory.Exists(p))
                        return Path.GetFullPath(p.Replace('/', '\\'));
                }
                catch { }
            }
        }
        var def = @"C:\Program Files (x86)\Steam";
        return Directory.Exists(def) ? def : null;
    }

    /// <summary>All library roots from steamapps/libraryfolders.vdf ("path" keys), plus Steam root itself.</summary>
    public static List<string> Libraries(string steamRoot)
    {
        var libs = new List<string>();
        if (steamRoot == null) return libs;
        libs.Add(steamRoot);
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase))
                libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        return libs.Select(p => p.TrimEnd('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static Dictionary<string, string> ReadAcf(string acfPath)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(File.ReadAllText(acfPath), "^\\s*\"(\\w+)\"\\s+\"([^\"]*)\"", RegexOptions.Multiline))
            d.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
        return d;
    }

    /// <summary>buildid from the acf next to a manually chosen game folder (…/steamapps/common/x -> …/steamapps/appmanifest).</summary>
    public static string BuildIdFor(string gameDir, int appId)
    {
        try
        {
            var acf = Path.GetFullPath(Path.Combine(gameDir, "..", "..", $"appmanifest_{appId}.acf"));
            return File.Exists(acf) ? ReadAcf(acf).GetValueOrDefault("buildid") : null;
        }
        catch { return null; }
    }

    public record GameInstall(string Library, string Dir, string AcfPath, string BuildId, string StateFlags);

    public const string GameExe = "over the hill.exe";

    /// <summary>Accepts the game folder itself, a folder that contains it, or a Steam library root.</summary>
    public static string ResolveGameDir(string picked)
    {
        if (string.IsNullOrWhiteSpace(picked)) return null;
        try
        {
            picked = picked.Trim().Trim('"');
            if (File.Exists(picked) && Path.GetFileName(picked).Equals(GameExe, StringComparison.OrdinalIgnoreCase))
                picked = Path.GetDirectoryName(picked);
            foreach (var c in new[] { picked, Path.Combine(picked, "over the hill"), Path.Combine(picked, "common", "over the hill"),
                                      Path.Combine(picked, "steamapps", "common", "over the hill") })
                if (Directory.Exists(c) && File.Exists(Path.Combine(c, GameExe))) return Path.GetFullPath(c);
        }
        catch { }
        return null;
    }

    /// <summary>Libraries from Steam's own config, plus common install locations on every fixed drive.</summary>
    static IEnumerable<string> CandidateLibraries()
    {
        foreach (var l in Libraries(SteamRoot())) yield return l;
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); } catch { yield break; }
        foreach (var d in drives)
        {
            bool ok; try { ok = d.DriveType == DriveType.Fixed && d.IsReady; } catch { ok = false; }
            if (!ok) continue;
            foreach (var rel in new[] { "SteamLibrary", "Steam", @"Program Files (x86)\Steam", @"Program Files\Steam", @"Games\Steam", @"Games\SteamLibrary" })
                yield return Path.Combine(d.RootDirectory.FullName, rel);
        }
    }

    public static GameInstall Find(int appId)
    {
        foreach (var lib in CandidateLibraries().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var acf = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
            try { if (!File.Exists(acf)) continue; } catch { continue; }
            var a = ReadAcf(acf);
            var dir = Path.Combine(lib, "steamapps", "common", a.GetValueOrDefault("installdir", "Over The Hill"));
            if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, GameExe)))
                return new GameInstall(lib, dir, acf, a.GetValueOrDefault("buildid"), a.GetValueOrDefault("StateFlags"));
        }
        return null;
    }
}

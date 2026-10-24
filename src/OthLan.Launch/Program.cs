using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using OthLan;
using OthLan.Installer;

namespace OthLan.Launch;

/// <summary>
/// Starts Over The Hill, watches BepInEx LogOutput.log, and if InteropManager hangs &gt;10 s downloading
/// unity base libraries from unity.bepinex.dev, fetches <c>6000.0.75.zip</c> from the public GitHub release
/// (sha256 via SHA256SUMS.txt), places it in <c>BepInEx/unity-libs/</c>, and restarts the game once.
/// </summary>
static class Program
{
    const int AppId = 2929250;
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Log.Info($"OTH-LAN-Launch {typeof(Program).Assembly.GetName().Version}  log={Log.PathOnDisk}");
        try
        {
            var game = ResolveGame(args);
            if (game == null) { Log.Err("Game folder not found. Pass --game \"…\\over the hill\" or install via Steam."); return 2; }
            Log.Info("game: " + game);

            var libsName = UnityLibsFileName();
            var libsDest = Path.Combine(game, "BepInEx", "unity-libs", libsName);
            var expectSha = UnityLibsSha256();

            if (File.Exists(libsDest) && Sha256(libsDest).Equals(expectSha, StringComparison.OrdinalIgnoreCase))
            {
                Log.Ok("unity-libs already present and verified — starting game");
                StartGame(game);
                return 0;
            }

            if (!StartGame(game)) return 3;
            if (!WatchForHang(game, out var hung)) return 4;
            if (!hung)
            {
                Log.Ok("boot progressed without a unity-libs hang");
                return 0;
            }

            Log.Warn("InteropManager hung on unity base libraries — fetching offline zip from GitHub");
            KillGame();
            Thread.Sleep(1500);
            if (!InstallUnityLibs(game, libsName, libsDest, expectSha)) return 5;
            Log.Ok("unity-libs installed — restarting game");
            if (!StartGame(game)) return 6;
            return 0;
        }
        catch (Exception e)
        {
            Log.Err(e.ToString());
            return 1;
        }
    }

    static string ResolveGame(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] is "--game" or "-g") return Steam.ResolveGameDir(args[i + 1]);
        var found = Steam.Find(AppId);
        return found?.Dir;
    }

    static string UnityLibsFileName()
    {
        try
        {
            using var doc = Manifest();
            return doc.RootElement.GetProperty("bepinex").GetProperty("unityLibsZip").GetString() ?? UpdateLogic.UnityLibsAssetName;
        }
        catch { return UpdateLogic.UnityLibsAssetName; }
    }

    static string UnityLibsSha256()
    {
        using var doc = Manifest();
        return doc.RootElement.GetProperty("bepinex").GetProperty("unityLibsSha256").GetString()
               ?? throw new InvalidDataException("manifest missing unityLibsSha256");
    }

    static JsonDocument Manifest()
    {
        var s = typeof(Program).Assembly.GetManifestResourceStream("supported-builds.json")
                ?? throw new InvalidDataException("embedded supported-builds.json missing");
        return JsonDocument.Parse(s);
    }

    static bool StartGame(string game)
    {
        KillGame();
        var exe = Path.Combine(game, Steam.GameExe);
        if (!File.Exists(exe)) { Log.Err("missing " + exe); return false; }
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = game,
            UseShellExecute = false,
        };
        psi.Environment["SteamAppId"] = AppId.ToString();
        psi.Environment["SteamGameId"] = AppId.ToString();
        Process.Start(psi);
        Log.Info("started " + exe);
        return true;
    }

    static void KillGame()
    {
        foreach (var p in Process.GetProcessesByName("over the hill"))
        {
            try { Log.Info($"killing pid {p.Id}"); p.Kill(true); p.WaitForExit(8000); } catch { }
        }
    }

    /// <summary>Poll LogOutput.log until BootReady, hang, process exit, or 3 minutes.</summary>
    static bool WatchForHang(string game, out bool hung)
    {
        hung = false;
        var logPath = Path.Combine(game, "BepInEx", "LogOutput.log");
        var deadline = DateTime.UtcNow.AddMinutes(3);
        long pos = 0;
        bool sawDl = false, sawReady = false;
        var lastProgress = DateTime.UtcNow;
        // Wait for the log file to appear (BepInEx creates it after doorstop).
        while (DateTime.UtcNow < deadline && !File.Exists(logPath))
        {
            if (Process.GetProcessesByName("over the hill").Length == 0) { Log.Warn("game exited before log appeared"); return true; }
            Thread.Sleep(400);
        }
        if (!File.Exists(logPath)) { Log.Warn("no LogOutput.log yet"); return true; }

        while (DateTime.UtcNow < deadline)
        {
            if (Process.GetProcessesByName("over the hill").Length == 0)
            {
                Log.Warn("game exited during watch");
                hung = sawDl && !sawReady;
                return true;
            }
            try
            {
                using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length < pos) pos = 0;
                fs.Seek(pos, SeekOrigin.Begin);
                using var sr = new StreamReader(fs);
                string line;
                var grew = false;
                while ((line = sr.ReadLine()) != null)
                {
                    grew = true;
                    var kind = HangLogic.Classify(line);
                    if (kind == HangLogic.LineKind.UnityLibsDownload) { sawDl = true; lastProgress = DateTime.UtcNow; Log.Info("saw: " + Trunc(line, 120)); }
                    else if (kind == HangLogic.LineKind.BootReady) { sawReady = true; lastProgress = DateTime.UtcNow; Log.Ok("boot ready: " + Trunc(line, 100)); }
                    else if (sawDl) lastProgress = DateTime.UtcNow; // any further lines count as progress while downloading
                }
                pos = fs.Position;
                if (grew && !sawDl) lastProgress = DateTime.UtcNow;
            }
            catch (IOException) { Thread.Sleep(200); continue; }

            if (sawReady) { hung = false; return true; }
            var idle = (DateTime.UtcNow - lastProgress).TotalSeconds;
            if (HangLogic.IsHung(sawDl, sawReady, idle))
            {
                hung = true;
                Log.Warn($"hang detected ({idle:0.0}s since progress after unity-libs download line)");
                return true;
            }
            Thread.Sleep(500);
        }
        Log.Warn("watch timed out");
        hung = sawDl && !sawReady;
        return true;
    }

    static bool InstallUnityLibs(string game, string libsName, string dest, string expectSha)
    {
        Log.Info("GET " + UpdateLogic.LatestApi);
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("OTH-LAN-Launch/0.2.1");
        var json = Http.GetStringAsync(UpdateLogic.LatestApi).GetAwaiter().GetResult();
        if (!UpdateLogic.TryFindAsset(json, libsName, out var url, out var why))
        { Log.Err("release asset: " + why); return false; }
        var sumsUrl = UpdateLogic.ParseRelease(json, out var pwhy)?.SumsUrl;
        if (string.IsNullOrEmpty(sumsUrl))
        {
            // ParseRelease requires installer zip; fall back to finding SHA256SUMS by name.
            if (!UpdateLogic.TryFindAsset(json, UpdateLogic.SumsName, out sumsUrl, out why))
            { Log.Err("sums: " + (pwhy ?? why)); return false; }
        }
        Log.Info("sums: " + sumsUrl);
        var sumsText = Http.GetStringAsync(sumsUrl).GetAwaiter().GetResult();
        var sums = UpdateLogic.ParseSums(sumsText);
        if (!sums.TryGetValue(libsName, out var sumSha) || !UpdateLogic.HashEquals(sumSha, expectSha))
        { Log.Err($"SHA256SUMS mismatch for {libsName} (sums={sumSha}, manifest={expectSha})"); return false; }

        Log.Info("GET " + url);
        var bytes = Http.GetByteArrayAsync(url).GetAwaiter().GetResult();
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var tmp = dest + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        var h = Sha256(tmp);
        if (!UpdateLogic.HashEquals(h, expectSha))
        { File.Delete(tmp); Log.Err($"downloaded sha256 {h} != {expectSha}"); return false; }
        File.Copy(tmp, dest, true);
        File.Delete(tmp);
        Log.Ok($"wrote {dest}");
        return true;
    }

    static string Sha256(string file)
    {
        using var f = File.OpenRead(file);
        using var s = SHA256.Create();
        return Convert.ToHexString(s.ComputeHash(f)).ToLowerInvariant();
    }

    static string Trunc(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s ?? "" : s.Substring(0, n - 1) + "…";
}

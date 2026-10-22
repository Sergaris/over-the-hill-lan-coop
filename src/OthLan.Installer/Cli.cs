using System;
using System.IO;
using System.Linq;

namespace OthLan.Installer;

/// <summary>Command-line mode: OTH-LAN-Setup install|uninstall|verify [--game DIR] [--port N] [--force] [--yes]</summary>
static class Cli
{
    public static int Run(string[] args)
    {
        var manifest = Payload.Manifest().RootElement;
        var appId = manifest.GetProperty("steamAppId").GetInt32();
        Log.Info($"OTH-LAN Setup {manifest.GetProperty("modVersion").GetString()}  (log: {Log.PathOnDisk})");

        string Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var force = args.Contains("--force");
        var port = int.TryParse(Arg("--port"), out var p) ? p : 27015;
        var cmd = args.FirstOrDefault(a => !a.StartsWith("--") && a != Arg("--game") && a != Arg("--port"));

        string gameDir = Arg("--game") is string ga ? (Steam.ResolveGameDir(ga) ?? ga) : null;
        if (gameDir == null)
        {
            var g = Steam.Find(appId);
            if (g == null) { Log.Err($"Over The Hill (app {appId}) not found in any Steam library. Use --game \"<folder>\"."); return Pause(2); }
            gameDir = g.Dir;
            Log.Info($"Game: {g.Dir}  (library {g.Library}, Steam buildid {g.BuildId}, StateFlags {g.StateFlags})");
            if (g.StateFlags != "4") Log.Warn("Steam reports the game is not fully installed/updated (StateFlags != 4).");
        }
        if (!Directory.Exists(gameDir)) { Log.Err($"Folder not found: {gameDir}"); return Pause(2); }

        var radmin = Net.RadminIp(out var adapter);
        Log.Info(radmin != null ? $"Radmin VPN IP: {radmin}  ({adapter})  <- friends join {radmin}:{port}" : "Radmin VPN adapter not found (install/connect Radmin VPN).");

        var inst = new Installer(gameDir, manifest);
        if (cmd == null)
        {
            var st = inst.LoadState();
            Console.WriteLine(st == null ? "\nStatus: not installed" : $"\nStatus: installed {st.ModVersion} (build {st.BuildId}, {st.InstalledAt})");
            Console.Write("[1] Install/Update  [2] Uninstall (full rollback)  [3] Verify game build  [Q] Quit > ");
            cmd = (Console.ReadLine() ?? "").Trim() switch { "1" => "install", "2" => "uninstall", "3" => "verify", _ => "quit" };
        }

        switch (cmd)
        {
            case "install": return Pause(inst.Install(force, port) ? 0 : 1);
            case "uninstall": return Pause(inst.Uninstall() ? 0 : 1);
            case "verify":
                var b = inst.MatchBuild(out var actual);
                foreach (var kv in actual) Log.Info($"  {kv.Key} = {kv.Value}");
                if (b != null) Log.Ok($"Supported build {b.Value.GetProperty("buildId").GetString()}"); else Log.Warn("Untested build: install is allowed; the mod checks compatibility at game start");
                return Pause(b != null ? 0 : 1);
            case "quit": return 0;
            default: Log.Err("usage: OTH-LAN-Setup [install|uninstall|verify] [--game DIR] [--port N] [--force] [--yes]"); return 2;
        }

        int Pause(int code)
        {
            if (!args.Contains("--yes") && !Console.IsInputRedirected) { Console.WriteLine("Press Enter to exit."); Console.ReadLine(); }
            return code;
        }
    }
}

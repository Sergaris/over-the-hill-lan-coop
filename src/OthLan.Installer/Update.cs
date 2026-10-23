using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace OthLan.Installer;

/// <summary>
/// 0.1.8 "OTH-LAN-Setup update --game DIR --wait-pid N": the second half of the in-game updater. The plugin has
/// downloaded this release zip, checked its sha256 against the release's SHA256SUMS.txt, unpacked it to a temp folder
/// and started this exe from there. We wait until the game (pid N) has exited, then replace only
/// BepInEx/plugins/OTH-LAN/OthLan.dll with the one embedded in this exe. The old DLL goes to OTH-LAN/backup-update-*;
/// configs, saves, BepInEx and the firewall rule are not touched. No admin: Steam library folders are writable by the
/// user (BepInEx writes its logs/config there too). A release that needs a different BepInEx refuses and points to
/// the full installer.
/// </summary>
static class Update
{
    public static int Run(string[] args)
    {
        string Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        try
        {
            var m = Payload.Manifest().RootElement;
            var ver = m.GetProperty("modVersion").GetString();
            Log.Info($"OTH-LAN Setup {ver}: update (log: {Log.PathOnDisk})");
            var game = Arg("--game");
            if (game == null || !File.Exists(Path.Combine(game, "BepInEx", "plugins", "OTH-LAN", "OthLan.dll")))
            { Log.Err($"not an OTH-LAN game folder: {game}"); return 2; }
            if (int.TryParse(Arg("--wait-pid"), out var pid))
            {
                Log.Info($"waiting for the game (pid {pid}) to exit");
                try
                {
                    using var p = Process.GetProcessById(pid);
                    if (!p.WaitForExit(TimeSpan.FromMinutes(60))) { Log.Err("the game is still running after 60 min: update not applied"); return 3; }
                }
                catch (ArgumentException) { }   // already gone
            }
            // the game's file handles may close a moment after exit
            for (int i = 0; i < 40 && Process.GetProcessesByName("over the hill").Any(); i++) Thread.Sleep(500);
            if (Process.GetProcessesByName("over the hill").Any()) { Log.Err("another game process is running: update not applied"); return 3; }

            var bx = m.GetProperty("bepinex").GetProperty("version").GetString();
            var statePath = Path.Combine(game, "OTH-LAN", "install-state.json");
            JsonDocument st = null;
            if (File.Exists(statePath)) { try { st = JsonDocument.Parse(File.ReadAllText(statePath)); } catch { } }
            var have = st != null && st.RootElement.TryGetProperty("BepInExVersion", out var hb) ? hb.GetString() : null;
            if (have != null && have != bx)
            { Log.Err($"this release needs BepInEx {bx}, installed is {have}: run OTH-LAN-Setup.exe from the zip instead"); return 4; }

            var dll = Payload.Embedded("OthLan.dll") ?? throw new InvalidOperationException("plugin not embedded");
            var dst = Path.Combine(game, "BepInEx", "plugins", "OTH-LAN", "OthLan.dll");
            var bakDir = Path.Combine(game, "OTH-LAN", "backup-update-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(bakDir);
            File.Copy(dst, Path.Combine(bakDir, "OthLan.dll"), true);
            Exception last = null;
            for (int i = 0; i < 20; i++)
            {
                try { var tmp = dst + ".new"; File.WriteAllBytes(tmp, dll); File.Move(tmp, dst, true); last = null; break; }
                catch (IOException e) { last = e; Thread.Sleep(500); }
                catch (UnauthorizedAccessException e) { last = e; Thread.Sleep(500); }
            }
            if (last != null) { Log.Err($"could not write {dst}: {last.Message} (old DLL unchanged, backup {bakDir})"); return 5; }
            if (st != null)
            {
                try
                {
                    var o = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
                    o["ModVersion"] = ver; o["UpdatedAt"] = DateTime.Now.ToString("s");
                    File.WriteAllText(statePath, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception e) { Log.Warn($"install-state not updated: {e.Message}"); }
            }
            Log.Ok($"OTH-LAN updated to {ver} (old DLL in {bakDir}). Start the game again.");
            return 0;
        }
        catch (Exception e) { Log.Err($"update failed: {e.Message}"); return 1; }
    }
}

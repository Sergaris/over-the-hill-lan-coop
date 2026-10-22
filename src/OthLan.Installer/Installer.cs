using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace OthLan.Installer;

class InstallState
{
    public string ModVersion { get; set; }
    public string BepInExVersion { get; set; }
    public string BuildId { get; set; }
    public DateTime InstalledAt { get; set; }
    public string BackupDir { get; set; }
    public List<string> Created { get; set; } = new();     // rel paths that did not exist before
    public List<string> Replaced { get; set; } = new();    // rel paths backed up to BackupDir before overwrite
    public List<string> OwnedDirs { get; set; } = new();   // top-level dirs that did not exist before (removed wholesale on uninstall)
    public int FirewallPort { get; set; }
}

class Installer
{
    readonly string _game;
    readonly JsonElement _m;
    string StateDir => Path.Combine(_game, "OTH-LAN");
    string StatePath => Path.Combine(StateDir, "install-state.json");
    public static readonly string[] BepInExTopLevel = { "BepInEx", "dotnet", "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt" };

    public Installer(string gameDir, JsonElement manifest) { _game = gameDir; _m = manifest; }

    public JsonElement? MatchBuild(out Dictionary<string, string> actual)
    {
        actual = new();
        foreach (var b in _m.GetProperty("builds").EnumerateArray())
        {
            var ok = true;
            foreach (var f in b.GetProperty("files").EnumerateObject())
            {
                if (!actual.TryGetValue(f.Name, out var h))
                {
                    var p = Path.Combine(_game, f.Name.Replace('/', Path.DirectorySeparatorChar));
                    h = actual[f.Name] = File.Exists(p) ? Payload.Sha256(p) : "missing";
                }
                if (!h.Equals(f.Value.GetString(), StringComparison.OrdinalIgnoreCase)) ok = false;
            }
            if (ok) return b;
        }
        return null;
    }

    public bool GameRunning() =>
        Process.GetProcessesByName("over the hill").Any();

    public string GameDir => _game;

    public InstallState LoadState() =>
        File.Exists(StatePath) ? JsonSerializer.Deserialize<InstallState>(File.ReadAllText(StatePath)) : null;

    public bool Install(bool force, int port, Action<int, string> progress = null)
    {
        void P(int pct, string what) { try { progress?.Invoke(pct, what); } catch { } }
        P(2, UiText.T("Проверка", "Checking"));
        if (GameRunning()) { Log.Err("Close the game first."); return false; }
        if (LoadState() != null) { Log.Warn("Already installed: rolling back previous install first (upgrade)."); if (!Uninstall()) return false; }

        var build = MatchBuild(out var actual);
        if (build == null)
        {
            foreach (var kv in actual) Log.Warn($"  {kv.Key} = {kv.Value}");
            // 0.1.6: warn, don't refuse. The plugin itself decides at game start: if Fusion's code and layout still
            // match a tested build it asks once ("not tested on this version"), otherwise LAN stays off with a message.
            Log.Warn("Game files differ from every tested build (the game was probably updated). Installing anyway: " +
                     "the mod checks compatibility at game start and asks or stays off if needed.");
        }
        else Log.Ok($"Supported build {build.Value.GetProperty("buildId").GetString()} ({build.Value.GetProperty("label").GetString()})");

        P(10, UiText.T("Подготовка файлов", "Preparing files"));
        var bx = _m.GetProperty("bepinex");
        var work = Directory.CreateTempSubdirectory("othlan-").FullName;
        var bepZip = Payload.Obtain("bepinex.zip", bx.GetProperty("zip").GetString(), bx.GetProperty("url").GetString(), bx.GetProperty("sha256").GetString(), work);
        // Unity base libraries: optional. Without them (public build) BepInEx downloads {UnityVersion}.zip from
        // unity.bepinex.dev itself on the first start (Il2CppInteropManager, UnityBaseLibrariesSource).
        string libsZip = null, libsName = null;
        if (bx.TryGetProperty("unityLibsZip", out var ul) && ul.GetString() is { Length: > 0 } uln)
        {
            libsName = uln;
            libsZip = Payload.Obtain("unity-libs.zip", uln, bx.GetProperty("unityLibsUrl").GetString(), bx.GetProperty("unityLibsSha256").GetString(), work);
        }
        else Log.Info("Unity base libraries not bundled: BepInEx downloads them on the first game start (internet needed once).");
        var pluginDll = Payload.Embedded("OthLan.dll") ?? throw new InvalidOperationException("plugin not embedded");

        // Plan every file we will write: rel path -> source writer
        var plan = new Dictionary<string, Action<string>>(StringComparer.OrdinalIgnoreCase);
        using var za = ZipFile.OpenRead(bepZip);
        foreach (var e in za.Entries.Where(e => !string.IsNullOrEmpty(e.Name)))
        {
            var entry = e;
            plan[entry.FullName.Replace('/', Path.DirectorySeparatorChar)] = dst => entry.ExtractToFile(dst, true);
        }
        if (libsZip != null) plan[Path.Combine("BepInEx", "unity-libs", libsName)] = dst => File.Copy(libsZip, dst, true);
        plan[Path.Combine("BepInEx", "plugins", "OTH-LAN", "OthLan.dll")] = dst => File.WriteAllBytes(dst, pluginDll);

        var st = new InstallState
        {
            ModVersion = _m.GetProperty("modVersion").GetString(),
            BepInExVersion = bx.GetProperty("version").GetString(),
            BuildId = build?.GetProperty("buildId").GetString(),
            InstalledAt = DateTime.Now,
            BackupDir = Path.Combine(StateDir, "backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")),
        };
        foreach (var top in BepInExTopLevel.Where(t => t is "BepInEx" or "dotnet"))
            if (!Directory.Exists(Path.Combine(_game, top))) st.OwnedDirs.Add(top);
        if (!st.OwnedDirs.Contains("BepInEx")) Log.Warn("An existing BepInEx folder was found: it will be kept; overwritten files are backed up.");

        Directory.CreateDirectory(st.BackupDir);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(st, new JsonSerializerOptions { WriteIndented = true })); // early, so a crash can still roll back
        try
        {
            var done = 0;
            foreach (var (rel, write) in plan)
            {
                P(20 + 70 * done++ / Math.Max(1, plan.Count), UiText.T("Копирование файлов", "Copying files"));
                var dst = Path.Combine(_game, rel);
                if (File.Exists(dst))
                {
                    var bak = Path.Combine(st.BackupDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(bak)!);
                    File.Copy(dst, bak, true);
                    st.Replaced.Add(rel);
                }
                else st.Created.Add(rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                write(dst);
            }
            Log.Ok($"Wrote {plan.Count} files ({st.Created.Count} new, {st.Replaced.Count} replaced+backed up)");

            P(92, UiText.T("Правило брандмауэра", "Firewall rule"));
            var exe = Directory.GetFiles(_game, "*.exe").FirstOrDefault(f => !Path.GetFileName(f).StartsWith("UnityCrashHandler", StringComparison.OrdinalIgnoreCase));
            if (exe != null && Net.AddFirewall(port, exe)) { st.FirewallPort = port; Log.Ok($"Firewall: inbound UDP {port},{port + 1} allowed for {Path.GetFileName(exe)}"); }
            else Log.Warn("Firewall rule NOT added (netsh failed; see log). The host must allow inbound UDP manually.");
        }
        catch (Exception e)
        {
            Log.Err($"Install failed: {e.Message}. Rolling back.");
            Save(st); Uninstall(); return false;
        }
        Save(st);
        P(100, UiText.T("Готово", "Done"));
        Log.Ok($"Installed OTH-LAN {st.ModVersion} with BepInEx {st.BepInExVersion}. First game start takes 1-3 min (BepInEx generates interop assemblies).");
        return true;
    }

    void Save(InstallState st) => File.WriteAllText(StatePath, JsonSerializer.Serialize(st, new JsonSerializerOptions { WriteIndented = true }));

    public bool Uninstall(Action<int, string> progress = null)
    {
        void P(int pct, string what) { try { progress?.Invoke(pct, what); } catch { } }
        P(5, UiText.T("Удаление", "Removing"));
        if (GameRunning()) { Log.Err("Close the game first."); return false; }
        var st = LoadState();
        if (st == null) { Log.Warn("No OTH-LAN install state found; nothing to roll back."); return true; }
        var ok = true;
        foreach (var rel in st.Created)
            try { var p = Path.Combine(_game, rel); if (File.Exists(p)) File.Delete(p); } catch (Exception e) { ok = false; Log.Err($"delete {rel}: {e.Message}"); }
        foreach (var rel in st.Replaced)
            try { File.Copy(Path.Combine(st.BackupDir, rel), Path.Combine(_game, rel), true); } catch (Exception e) { ok = false; Log.Err($"restore {rel}: {e.Message}"); }
        // dirs we created (BepInEx/, dotnet/) also contain runtime-generated files (interop, cache, logs, config): remove wholesale
        foreach (var d in st.OwnedDirs)
            try { var p = Path.Combine(_game, d); if (Directory.Exists(p)) Directory.Delete(p, true); } catch (Exception e) { ok = false; Log.Err($"rmdir {d}: {e.Message}"); }
        P(70, UiText.T("Брандмауэр", "Firewall"));
        if (st.FirewallPort != 0) Net.RemoveFirewall(st.FirewallPort);
        if (ok)
        {
            try { Directory.Delete(StateDir, true); } catch (Exception e) { Log.Warn($"could not remove {StateDir}: {e.Message}"); }
            Log.Ok("Uninstalled: all OTH-LAN/BepInEx files removed or restored.");
        }
        else Log.Err($"Rollback incomplete; state kept in {StatePath}. Fix the errors above and run uninstall again (or Steam > Verify integrity).");
        P(90, UiText.T("Проверка файлов игры", "Checking the game files"));
        var b = MatchBuild(out _);
        P(100, UiText.T("Готово", "Done"));
        Log.Info(b != null ? "Game files verified: still a supported vanilla build." : "Game file hashes do not match a supported build (was the game updated?).");
        return ok;
    }
}

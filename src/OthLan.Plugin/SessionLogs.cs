using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;

namespace OthLan;

/// <summary>
/// 0.2.0 T0.2: snapshot and export session logs without losing files that BepInEx/Unity keep open for write.
/// Owner of log retention + diag zip contents. Callers: F7 export, session end, Overlay quit.
/// </summary>
internal static class SessionLogs
{
    public static string LastExportPath;
    static DateTime _sessionStartedUtc = DateTime.UtcNow;

    public static void NoteSessionStart() => _sessionStartedUtc = DateTime.UtcNow;

    /// <summary>Called from <c>Overlay.OnApplicationQuit</c> (IL2CPP-safe; <c>Application.quitting</c> is unreliable here).</summary>
    public static void OnQuit()
    {
        try { SnapshotNow("quit"); }
        catch (Exception e) { Plugin.L?.LogWarning($"[Logs] quit snapshot: {e.Message}"); }
    }

    /// <summary>Copy <c>LogOutput.log</c> into <c>BepInEx/OthLan-logs/</c>, keep the last <see cref="SessionLogsLogic.KeepCount"/>.</summary>
    public static string SnapshotNow(string why)
    {
        try
        {
            var src = Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput.log");
            if (!File.Exists(src)) { Plugin.L?.LogInfo($"[Logs] snapshot skip ({why}): LogOutput.log missing"); return null; }
            var dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "OthLan-logs");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, SessionLogsLogic.SnapshotFileName(DateTime.Now, why));
            CopyOpenFile(src, dest);
            Rotate(dir);
            Plugin.L?.LogInfo($"[Logs] snapshot {Path.GetFileName(dest)} ({why})");
            return dest;
        }
        catch (Exception e)
        {
            Plugin.L?.LogWarning($"[Logs] snapshot failed ({why}): {e.Message}");
            return null;
        }
    }

    static void Rotate(string dir)
    {
        string[] names;
        try { names = Directory.GetFiles(dir); }
        catch (Exception e) { Plugin.L?.LogWarning($"[Logs] rotate list: {e.Message}"); return; }
        var basenames = new string[names.Length];
        for (var i = 0; i < names.Length; i++) basenames[i] = Path.GetFileName(names[i]);
        foreach (var drop in SessionLogsLogic.PickExcess(basenames))
        {
            try { File.Delete(Path.Combine(dir, drop)); Plugin.L?.LogInfo($"[Logs] rotated out {drop}"); }
            catch (Exception e) { Plugin.L?.LogWarning($"[Logs] rotate delete {drop}: {e.Message}"); }
        }
    }

    /// <summary>Zip logs + cfg + netstats + session.txt to the Desktop. Snapshots LogOutput first.</summary>
    public static string ExportZip(string why)
    {
        try
        {
            SnapshotNow("export");
            var desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desk) || !Directory.Exists(desk))
                desk = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var path = Path.Combine(desk, $"OTH-LAN-diag-{stamp}.zip");
            using (var zs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(zs, ZipArchiveMode.Create))
            {
                AddOpenFile(zip, Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput.log"), "LogOutput.log");
                AddOpenFile(zip, Path.Combine(BepInEx.Paths.BepInExRootPath, "ErrorLog.log"), "ErrorLog.log");
                try
                {
                    var pd = Application.persistentDataPath;
                    AddOpenFile(zip, Path.Combine(pd, "Player.log"), "Player.log");
                    AddOpenFile(zip, Path.Combine(pd, "Player-prev.log"), "Player-prev.log");
                }
                catch (Exception e) { Plugin.L?.LogWarning($"[Logs] skip Player.log: {e.Message}"); }
                try { AddOpenFile(zip, Plugin.CfgPath, "OthLan.cfg"); }
                catch (Exception e) { Plugin.L?.LogWarning($"[Logs] skip cfg: {e.Message}"); }

                WriteEntry(zip, "netstats.txt", BuildNetstats(why));
                WriteEntry(zip, "session.txt", BuildSession(why));
            }
            LastExportPath = path;
            Plugin.L?.LogInfo($"[Logs] exported {path}");
            return path;
        }
        catch (Exception e)
        {
            Plugin.L?.LogWarning($"[Logs] export failed: {e.Message}");
            LastExportPath = null;
            return null;
        }
    }

    static string BuildNetstats(string why)
    {
        var snap = new StringBuilder();
        snap.AppendLine($"OTH-LAN {Plugin.Version} diag ({why}) {DateTime.Now:o}");
        snap.AppendLine($"role={LanState.Active} runner={(LanState.LanRunner != IntPtr.Zero)}");
        try { snap.AppendLine(NetStats.Describe()); } catch (Exception e) { snap.AppendLine("NetStats: " + e.Message); }
        try { snap.AppendLine(NetStats.FrameDescribe()); } catch { }
        try { snap.AppendLine(FrameCost.Describe()); } catch { }
        try { snap.AppendLine(RemoteSmooth.Describe()); } catch { }
        try { snap.AppendLine(RemoteWheelVisuals.Describe()); } catch { }
        try { snap.AppendLine(OwnCar.Describe()); } catch { }
        try { snap.AppendLine(OwnRender.Describe()); } catch { }
        try { snap.AppendLine(OwnLocal.Describe()); } catch { }
        return snap.ToString();
    }

    static string BuildSession(string why)
    {
        var sb = new StringBuilder();
        var endUtc = DateTime.UtcNow;
        sb.AppendLine($"mod={Plugin.Version}");
        sb.AppendLine($"gameBuild={BuildGate.BuildKey ?? "?"}");
        sb.AppendLine($"gaHash={BuildGate.GaHash ?? ""}");
        sb.AppendLine($"role={LanState.Active}");
        sb.AppendLine($"slot={LanState.ClientSlot}");
        sb.AppendLine($"assignedSlot={LanState.AssignedSlot}");
        sb.AppendLine($"players={LanState.EffectivePlayers}");
        sb.AppendLine($"sessionStartUtc={_sessionStartedUtc:o}");
        sb.AppendLine($"sessionEndUtc={endUtc:o}");
        sb.AppendLine($"exportUtc={endUtc:o}");
        sb.AppendLine($"why={why}");
        try { AppendLiveCfgSections(sb, Plugin.Cfg); }
        catch (Exception e) { sb.AppendLine("cfgError=" + e.Message); }
        return sb.ToString();
    }

    /// <summary>Dump every bound entry in <see cref="SessionLogsLogic.SessionCfgSections"/> (live values from Bind).</summary>
    internal static void AppendLiveCfgSections(StringBuilder sb, ConfigFile cfg)
    {
        if (sb == null || cfg == null) return;
        foreach (ConfigDefinition def in cfg.Keys)
        {
            if (!SessionLogsLogic.IsSessionCfgSection(def.Section)) continue;
            try
            {
                var entry = cfg[def];
                sb.Append(def.Section).Append('.').Append(def.Key).Append('=')
                    .Append(entry?.BoxedValue ?? "").AppendLine();
            }
            catch (Exception e)
            {
                sb.Append(def.Section).Append('.').Append(def.Key).Append("=<err:").Append(e.Message).AppendLine(">");
            }
        }
    }

    static void AddOpenFile(ZipArchive zip, string src, string entry)
    {
        if (string.IsNullOrEmpty(src) || !File.Exists(src))
        {
            Plugin.L?.LogInfo($"[Logs] skip {entry}: missing");
            return;
        }
        try
        {
            using var fs = OpenSharedRead(src);
            var e = zip.CreateEntry(entry, System.IO.Compression.CompressionLevel.Optimal);
            using var dst = e.Open();
            fs.CopyTo(dst);
        }
        catch (Exception ex) { Plugin.L?.LogWarning($"[Logs] skip {entry}: {ex.Message}"); }
    }

    static void WriteEntry(ZipArchive zip, string name, string text)
    {
        var e = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open(), Encoding.UTF8);
        w.Write(text);
    }

    static void CopyOpenFile(string src, string dest)
    {
        using var fs = OpenSharedRead(src);
        using var outFs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.Read);
        fs.CopyTo(outFs);
    }

    static FileStream OpenSharedRead(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}

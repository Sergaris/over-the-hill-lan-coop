using System;
using System.Collections.Generic;
using UnityEngine;

namespace OthLan;

/// <summary>
/// IL2CPP bridge for Modio livery mods: collect host requirements, local readiness, temp-subscribe.
/// Owner of game API calls for the pre-join mod gate; wire format stays in <see cref="ModLiveryGateLogic"/>.
/// Download is a small state machine: init → auth → RequestTempMod (with retries) → Refresh until ready.
/// </summary>
internal static class ModLiveryBridge
{
    public enum Phase { Idle, Ensuring, Waiting, Done, Failed }

    public static Phase DownloadPhase { get; private set; } = Phase.Idle;
    /// <summary>Short status for the gate UI (already localized keys or plain progress).</summary>
    public static string StatusKey { get; private set; } = "";
    public static string StatusArg { get; private set; } = "";

    static float _nextRefresh, _nextRetry, _ensureDeadline, _waitDeadline;
    static List<ModLiveryGateLogic.ModRef> _target = new();
    static readonly HashSet<long> _asked = new();
    static int _ensureKicks, _retries;

    /// <summary>Host: unique equipped mod liveries from the local profile (and session RuntimePlayer when available).</summary>
    public static List<ModLiveryGateLogic.ModRef> CollectHostRequired()
    {
        var list = new List<ModLiveryGateLogic.ModRef>();
        var seen = new HashSet<ModLiveryGateLogic.ModRef>();
        void Add(long id, int sub)
        {
            if (id == 0) return;
            var m = new ModLiveryGateLogic.ModRef(id, sub);
            if (seen.Add(m)) list.Add(m);
        }
        try
        {
            var save = OverlandSaver.CurrentProfileSave;
            var profile = save != null ? save.Profile : null;
            var map = profile != null ? profile.EquippedModLiveries : null;
            if (map != null)
            {
                foreach (var kv in map)
                {
                    if (string.IsNullOrEmpty(kv.Value)) continue;
                    var parsed = ModLiveryGateLogic.Parse(kv.Value);
                    if (parsed.Count > 0) { foreach (var p in parsed) Add(p.Id, p.SubId); continue; }
                    if (ModLiveryEquipper.TryGetEquippedIds(kv.Key, out var id, out var sub)) Add(id, sub);
                }
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[ModLivery] profile scan: {e.Message}"); }

        try
        {
            foreach (var fp in UnityEngine.Object.FindObjectsOfType<FusionPlayer>())
            {
                if (fp == null || fp.WasCollected) continue;
                var vcc = fp.GetComponentInChildren<VehicleCustomizationController>() ?? fp.GetComponent<VehicleCustomizationController>();
                if (vcc != null)
                {
                    try { Add(vcc.EquippedModLiveryId, vcc.EquippedModLiverySubId); } catch { }
                }
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[ModLivery] session scan: {e.Message}"); }

        return list;
    }

    public static bool HasLocal(ModLiveryGateLogic.ModRef m)
    {
        try
        {
            var svc = ModManager.Instance?.Liveries;
            if (svc == null) return false;
            return svc.TryGetLivery(m.Id, m.SubId, out _);
        }
        catch { return false; }
    }

    public static List<ModLiveryGateLogic.ModRef> MissingOf(IReadOnlyList<ModLiveryGateLogic.ModRef> required) =>
        ModLiveryGateLogic.Missing(required, HasLocal);

    /// <summary>Start / restart download of the given missing set. Non-blocking; drive with <see cref="TickDownload"/>.</summary>
    public static void BeginDownload(IReadOnlyList<ModLiveryGateLogic.ModRef> missing)
    {
        _target = missing != null ? new List<ModLiveryGateLogic.ModRef>(missing) : new();
        _asked.Clear();
        _ensureKicks = _retries = 0;
        _nextRetry = 0;
        StatusKey = "modGate.boot"; StatusArg = "";
        DownloadPhase = Phase.Ensuring;
        _ensureDeadline = Time.realtimeSinceStartup + 45f;
        _waitDeadline = 0;
        Plugin.L.LogInfo($"[ModLivery] download begin: {ModLiveryGateLogic.Format(_target)}");
        KickEnsure();
    }

    /// <summary>Compatibility: same as <see cref="BeginDownload"/>.</summary>
    public static void RequestDownload(IReadOnlyList<ModLiveryGateLogic.ModRef> missing) => BeginDownload(missing);

    public static void AbortDownload()
    {
        DownloadPhase = Phase.Idle;
        _target.Clear(); _asked.Clear();
        StatusKey = ""; StatusArg = "";
    }

    static void KickEnsure()
    {
        _ensureKicks++;
        try
        {
            var mm = ModManager.Instance;
            if (mm == null)
            {
                StatusKey = "modGate.noManager";
                Plugin.L.LogWarning("[ModLivery] ModManager missing");
                return;
            }
            if (!mm.IsInitialized)
            {
                StatusKey = "modGate.init";
                try { mm.EnsureInitializedAsync(); } catch (Exception e) { Plugin.L.LogWarning($"[ModLivery] init: {e.Message}"); }
                try { mm.BootAsync(); } catch { }
                return;
            }
            if (!mm.IsAuthenticated)
            {
                StatusKey = "modGate.auth";
                // Silent first; then with terms UI so the player can accept if needed.
                try { mm.TrySilentBootAuthenticationAsync(); } catch (Exception e) { Plugin.L.LogWarning($"[ModLivery] silent auth: {e.Message}"); }
                try { mm.EnsureAuthenticatedAsync(_ensureKicks > 2); } catch (Exception e) { Plugin.L.LogWarning($"[ModLivery] auth: {e.Message}"); }
                return;
            }
            StatusKey = "modGate.requesting";
            AskMissing();
            DownloadPhase = Phase.Waiting;
            _waitDeadline = Time.realtimeSinceStartup + 180f;
            _nextRetry = Time.realtimeSinceStartup + 6f;
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[ModLivery] ensure: {e}");
            StatusKey = "modGate.failed"; StatusArg = e.Message;
            DownloadPhase = Phase.Failed;
        }
    }

    static void AskMissing()
    {
        var mm = ModManager.Instance;
        if (mm == null) return;
        var still = MissingOf(_target);
        foreach (var m in still)
        {
            if (m.Id == 0 || !_asked.Add(m.Id)) continue;
            try
            {
                mm.RequestTempMod(m.Id);
                Plugin.L.LogInfo($"[ModLivery] RequestTempMod {m.Id}");
            }
            catch (Exception e) { Plugin.L.LogWarning($"[ModLivery] RequestTempMod {m.Id}: {e.Message}"); }
        }
        // Re-ask already-asked ids that are still missing (Modio sometimes drops the first request).
        if (_retries > 0)
        {
            var ids = new HashSet<long>();
            foreach (var m in still)
            {
                if (m.Id == 0 || !ids.Add(m.Id)) continue;
                try { mm.RequestTempMod(m.Id); Plugin.L.LogInfo($"[ModLivery] RequestTempMod retry {m.Id} (#{_retries})"); }
                catch (Exception e) { Plugin.L.LogWarning($"[ModLivery] retry {m.Id}: {e.Message}"); }
            }
        }
        try { mm.Liveries?.Refresh(); } catch { }
        try { mm.Liveries?.RefreshLocalLiveries(); } catch { }
        try { mm.Liveries?.RefreshAvailability(); } catch { }
    }

    /// <summary>Main thread: boot / auth / retries / refresh while a download is active.</summary>
    public static void TickDownload()
    {
        if (DownloadPhase is Phase.Idle or Phase.Done or Phase.Failed) return;
        var now = Time.realtimeSinceStartup;

        if (DownloadPhase == Phase.Ensuring)
        {
            if (now > _ensureDeadline)
            {
                DownloadPhase = Phase.Failed;
                StatusKey = "modGate.authTimeout";
                Plugin.L.LogWarning("[ModLivery] ensure timeout (init/auth)");
                return;
            }
            if (now >= _nextRetry)
            {
                _nextRetry = now + 2f;
                KickEnsure();
            }
            return;
        }

        // Waiting
        if (now >= _nextRefresh)
        {
            _nextRefresh = now + 1.5f;
            try
            {
                var svc = ModManager.Instance?.Liveries;
                svc?.Refresh();
                svc?.RefreshLocalLiveries();
                svc?.RefreshAvailability();
            }
            catch { }
        }

        var missing = MissingOf(_target);
        StatusKey = "modGate.downloading";
        StatusArg = missing.Count.ToString();

        if (missing.Count == 0)
        {
            DownloadPhase = Phase.Done;
            StatusKey = ""; StatusArg = "";
            Plugin.L.LogInfo("[ModLivery] download done — all required liveries local");
            return;
        }

        if (now > _waitDeadline)
        {
            DownloadPhase = Phase.Failed;
            StatusKey = "modGate.timeout";
            Plugin.L.LogWarning("[ModLivery] download timeout — still missing " + ModLiveryGateLogic.Format(missing));
            return;
        }

        if (now >= _nextRetry)
        {
            _nextRetry = now + 8f;
            _retries++;
            _asked.Clear(); // allow fresh RequestTempMod for every still-missing id
            AskMissing();
        }
    }

    /// <summary>Legacy alias used by the gate while downloading.</summary>
    public static void TickRefresh() => TickDownload();
}

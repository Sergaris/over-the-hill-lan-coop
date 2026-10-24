using System;
using System.Collections.Generic;
using UnityEngine;

namespace OthLan;

/// <summary>
/// IL2CPP bridge for Modio livery mods: collect host requirements, local readiness, temp-subscribe.
/// Owner of game API calls for the pre-join mod gate; wire format stays in <see cref="ModLiveryGateLogic"/>.
/// </summary>
internal static class ModLiveryBridge
{
    static float _nextRefresh;

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
                    // Keys are ModLivery.Key ("modId" or "modId:sub"); also accept raw "id:sub".
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
                // Prefer networked customization on the car when already in a session.
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

    /// <summary>Ask Modio for temporary session access to each mod (subscribe + install). Non-blocking.</summary>
    public static void RequestDownload(IReadOnlyList<ModLiveryGateLogic.ModRef> missing)
    {
        if (missing == null || missing.Count == 0) return;
        try
        {
            var mm = ModManager.Instance;
            if (mm == null) { Plugin.L.LogWarning("[ModLivery] ModManager missing"); return; }
            try { mm.EnsureInitializedAsync(); } catch (Exception e) { Plugin.L.LogWarning($"[ModLivery] init: {e.Message}"); }
            try { mm.EnsureAuthenticatedAsync(false); } catch (Exception e) { Plugin.L.LogWarning($"[ModLivery] auth: {e.Message}"); }
            var ids = new HashSet<long>();
            foreach (var m in missing)
            {
                if (m.Id == 0 || !ids.Add(m.Id)) continue;
                try
                {
                    mm.RequestTempMod(m.Id);
                    Plugin.L.LogInfo($"[ModLivery] RequestTempMod {m.Id}");
                }
                catch (Exception e) { Plugin.L.LogWarning($"[ModLivery] RequestTempMod {m.Id}: {e.Message}"); }
            }
            try { mm.Liveries?.Refresh(); } catch { }
        }
        catch (Exception e) { Plugin.L.LogError($"[ModLivery] download: {e}"); }
    }

    /// <summary>Main thread: occasional Refresh while waiting for Modio installs.</summary>
    public static void TickRefresh()
    {
        var t = Time.realtimeSinceStartup;
        if (t < _nextRefresh) return;
        _nextRefresh = t + 2f;
        try { ModManager.Instance?.Liveries?.Refresh(); } catch { }
    }
}

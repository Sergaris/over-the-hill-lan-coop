using System;
using System.Collections.Generic;
using UnityEngine;

namespace OthLan;

/// <summary>
/// IL2CPP bridge for Modio livery mods: collect host requirements, local readiness, temp-subscribe.
/// Owner of game API calls for the pre-join mod gate; wire format stays in <see cref="ModLiveryGateLogic"/>.
/// Download: init → auth → <see cref="ModManager.AddTempModAsync"/> (real install) + RequestTempMod once →
/// Refresh / OnRegistryChanged until <see cref="ModLiveryService.TryGetLivery"/>.
/// </summary>
internal static class ModLiveryBridge
{
    public enum Phase { Idle, Ensuring, Waiting, Done, Failed }

    public static Phase DownloadPhase { get; private set; } = Phase.Idle;
    /// <summary>Short status for the gate UI (localized key).</summary>
    public static string StatusKey { get; private set; } = "";
    public static string StatusArg { get; private set; } = "";

    static float _nextRefresh, _nextRetry, _ensureDeadline, _waitDeadline, _nextDiag;
    static List<ModLiveryGateLogic.ModRef> _target = new();
    static readonly HashSet<long> _asked = new();
    static readonly Dictionary<long, string> _names = new();
    static readonly Dictionary<long, float> _progress = new(); // 0..1 when known; absent = unknown
    static readonly Dictionary<long, Modio.Mods.Mod> _watched = new();
    static int _ensureKicks, _retries;
    static bool _registryHooked, _progressHooked;
    static Il2CppSystem.Action _onRegistry;
    static Il2CppSystem.Action<Modio.Mods.Mod, Modio.Mods.ModChangeType> _onModProgress;

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
                    // Profile map key = car name; TryGetEquippedIds(carName, …) is the game's path.
                    if (ModLiveryEquipper.TryGetEquippedIds(kv.Key, out var id, out var sub)) Add(id, sub);
                    else if (ModLiveryEquipper.TryParseKey(kv.Value, out id, out sub)) Add(id, sub);
                }
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[ModLivery] profile scan: {e.Message}"); }

        try
        {
            CarRegistry.RefreshNow();
            foreach (var fp in CarRegistry.Players)
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
            if (svc.TryGetLivery(m.Id, m.SubId, out _)) return true;
            // Exact pair missing: if the mod is installed under another SubId, still not ready for the host's paint,
            // but log once so we can see registry vs wire mismatch.
            return false;
        }
        catch { return false; }
    }

    public static List<ModLiveryGateLogic.ModRef> MissingOf(IReadOnlyList<ModLiveryGateLogic.ModRef> required) =>
        ModLiveryGateLogic.Missing(required, HasLocal);

    /// <summary>Display name from livery cache / Modio watch; null if only the id is known.</summary>
    public static string TryGetDisplayName(ModLiveryGateLogic.ModRef m)
    {
        if (m.Id == 0) return null;
        if (_names.TryGetValue(m.Id, out var cached) && !string.IsNullOrWhiteSpace(cached)) return cached;
        try
        {
            var svc = ModManager.Instance?.Liveries;
            if (svc != null)
            {
                if (TryReadLiveryName(svc, m.Id, m.SubId, out var n) || TryReadLiveryName(svc, m.Id, 0, out n))
                {
                    _names[m.Id] = n;
                    return n;
                }
            }
        }
        catch { }
        return TryNameFromWatched(m.Id);
    }

    static string TryNameFromWatched(long id)
    {
        if (!_watched.TryGetValue(id, out var mod) || mod == null || mod.WasCollected) return null;
        try
        {
            var n = mod.Name;
            if (!string.IsNullOrWhiteSpace(n)) { _names[id] = n; return n; }
        }
        catch { }
        return null;
    }

    static bool TryReadLiveryName(ModLiveryService svc, long id, int sub, out string name)
    {
        name = null;
        try
        {
            if (!svc.TryGetLivery(id, sub, out var L) || L == null || L.WasCollected) return false;
            name = L.DisplayName;
            return !string.IsNullOrWhiteSpace(name);
        }
        catch { return false; }
    }

    /// <summary>0..1 download progress when Modio reported it; otherwise -1.</summary>
    public static float TryGetProgress(ModLiveryGateLogic.ModRef m)
    {
        if (m.Id == 0) return -1f;
        if (_progress.TryGetValue(m.Id, out var p)) return p;
        try
        {
            if (_watched.TryGetValue(m.Id, out var mod) && mod != null && !mod.WasCollected)
            {
                var file = mod.File;
                if (file != null && !file.WasCollected)
                {
                    var pct = file.FileStateProgress;
                    if (pct >= 0f && pct <= 1.0001f) { _progress[m.Id] = pct; return pct; }
                }
            }
        }
        catch { }
        return -1f;
    }

    /// <summary>UI row kind for one required mod (decision table in <see cref="ModLiveryGateLogic.ClassifyItem"/>).</summary>
    public static ModLiveryGateLogic.ItemKind Classify(ModLiveryGateLogic.ModRef m)
    {
        var active = DownloadPhase is Phase.Ensuring or Phase.Waiting;
        var failed = DownloadPhase == Phase.Failed;
        return ModLiveryGateLogic.ClassifyItem(HasLocal(m), active, _asked.Contains(m.Id), failed);
    }

    /// <summary>Start / restart download of the given missing set. Non-blocking; drive with <see cref="TickDownload"/>.</summary>
    public static void BeginDownload(IReadOnlyList<ModLiveryGateLogic.ModRef> missing)
    {
        ClearWatch();
        _target = missing != null ? new List<ModLiveryGateLogic.ModRef>(missing) : new();
        _asked.Clear();
        _progress.Clear();
        _ensureKicks = _retries = 0;
        _nextRetry = 0;
        _nextDiag = 0;
        StatusKey = "modGate.boot"; StatusArg = "";
        DownloadPhase = Phase.Ensuring;
        _ensureDeadline = Time.realtimeSinceStartup + 45f;
        _waitDeadline = 0;
        HookRegistry();
        EnsureProgressHook();
        Plugin.L.LogInfo($"[ModLivery] download begin: {ModLiveryGateLogic.Format(_target)}");
        KickEnsure();
    }

    /// <summary>Compatibility: same as <see cref="BeginDownload"/>.</summary>
    public static void RequestDownload(IReadOnlyList<ModLiveryGateLogic.ModRef> missing) => BeginDownload(missing);

    public static void AbortDownload()
    {
        DownloadPhase = Phase.Idle;
        _target.Clear(); _asked.Clear();
        _progress.Clear();
        ClearWatch();
        StatusKey = ""; StatusArg = "";
        UnhookRegistry();
    }

    static void ClearWatch()
    {
        if (_progressHooked && _onModProgress != null)
        {
            try { Modio.Mods.Mod.RemoveChangeListener(Modio.Mods.ModChangeType.DownloadProgress, _onModProgress); }
            catch { }
        }
        _progressHooked = false;
        _watched.Clear();
        _onModProgress = null;
    }

    static void EnsureProgressHook()
    {
        if (_progressHooked) return;
        _onModProgress ??= (Il2CppSystem.Action<Modio.Mods.Mod, Modio.Mods.ModChangeType>)((m, _) =>
        {
            try
            {
                if (m == null || m.WasCollected) return;
                var mid = m.Id;
                if (mid == 0) return;
                _watched[mid] = m;
                try
                {
                    var nn = m.Name;
                    if (!string.IsNullOrWhiteSpace(nn)) _names[mid] = nn;
                }
                catch { }
                var file = m.File;
                if (file != null && !file.WasCollected)
                {
                    var pct = file.FileStateProgress;
                    if (pct >= 0f && pct <= 1.0001f) _progress[mid] = pct;
                }
            }
            catch { }
        });
        try
        {
            Modio.Mods.Mod.AddChangeListener(Modio.Mods.ModChangeType.DownloadProgress, _onModProgress);
            _progressHooked = true;
        }
        catch (Exception e) { Plugin.L.LogWarning($"[ModLivery] progress hook: {e.Message}"); }
    }

    /// <summary>Best-effort: remember a Modio <see cref="Modio.Mods.Mod"/> for name + download %.</summary>
    public static void WatchMod(Modio.Mods.Mod mod)
    {
        if (mod == null || mod.WasCollected) return;
        long id;
        try { id = mod.Id; } catch { return; }
        if (id == 0) return;
        try
        {
            var n = mod.Name;
            if (!string.IsNullOrWhiteSpace(n)) _names[id] = n;
        }
        catch { }
        _watched[id] = mod;
        EnsureProgressHook();
    }

    static void HookRegistry()
    {
        if (_registryHooked) return;
        try
        {
            var svc = ModManager.Instance?.Liveries;
            if (svc == null) return;
            _onRegistry = (Il2CppSystem.Action)(() =>
            {
                try
                {
                    // Force a re-check on next tick; Refresh* may already have run inside the game.
                    _nextRefresh = 0;
                }
                catch { }
            });
            svc.add_OnRegistryChanged(_onRegistry);
            _registryHooked = true;
            Plugin.L.LogInfo("[ModLivery] hooked Liveries.OnRegistryChanged");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[ModLivery] registry hook: {e.Message}"); }
    }

    static void UnhookRegistry()
    {
        if (!_registryHooked) return;
        try
        {
            var svc = ModManager.Instance?.Liveries;
            if (svc != null && _onRegistry != null) svc.remove_OnRegistryChanged(_onRegistry);
        }
        catch { }
        _registryHooked = false;
        _onRegistry = null;
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
                try { mm.TrySilentBootAuthenticationAsync(); } catch (Exception e) { Plugin.L.LogWarning($"[ModLivery] silent auth: {e.Message}"); }
                try { mm.EnsureAuthenticatedAsync(_ensureKicks > 2); } catch (Exception e) { Plugin.L.LogWarning($"[ModLivery] auth: {e.Message}"); }
                return;
            }
            HookRegistry();
            TryEnableModsOnRunner();
            StatusKey = "modGate.requesting";
            AskMissing();
            DownloadPhase = Phase.Waiting;
            _waitDeadline = Time.realtimeSinceStartup + 180f;
            _nextRetry = Time.realtimeSinceStartup + 15f;
            _nextDiag = Time.realtimeSinceStartup + 5f;
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[ModLivery] ensure: {e}");
            StatusKey = "modGate.failed"; StatusArg = e.Message;
            DownloadPhase = Phase.Failed;
        }
    }

    /// <summary>
    /// Pre-join gate runs before <c>PushStart</c>, so the menu <see cref="OverlandNetworkRunner"/> often has
    /// <c>ModsAllowed == false</c>. <see cref="ModManager.RequestTempMod"/> then returns without queuing.
    /// Flip the runner's room flag so the game path is allowed; <see cref="ModManager.AddTempModAsync"/> still
    /// works without it, but RequestTempMod and in-game policy expect AllowMods.
    /// </summary>
    static void TryEnableModsOnRunner()
    {
        try
        {
            var onr = OverlandNetworkRunner.Instance;
            if (onr == null || onr.WasCollected)
            {
                Plugin.L.LogInfo("[ModLivery] runner: none (ModsAllowed n/a)");
                return;
            }
            bool before = false;
            try { before = onr.ModsAllowed; } catch { }
            var ctx = onr.GameSetupContext;
            if (ctx == null)
            {
                try { ctx = new GameSetupContext(); onr.GameSetupContext = ctx; } catch (Exception e)
                { Plugin.L.LogWarning($"[ModLivery] GameSetupContext: {e.Message}"); }
            }
            if (ctx != null)
            {
                var props = ctx.RoomProperties;
                if (props == null)
                {
                    try { props = new OverlandRoomProperties(); ctx.RoomProperties = props; } catch { }
                }
                if (props != null)
                {
                    try { props.AllowMods = true; } catch (Exception e) { Plugin.L.LogWarning($"[ModLivery] AllowMods=: {e.Message}"); }
                }
            }
            bool after = before;
            try { after = onr.ModsAllowed; } catch { }
            Plugin.L.LogInfo($"[ModLivery] runner ModsAllowed {before} → {after}");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[ModLivery] enable mods: {e.Message}"); }
    }

    /// <summary>
    /// Start installs once per mod id. Prefer <see cref="ModManager.AddTempModAsync"/> (real
    /// <c>StartTempModSession</c>); also call <see cref="ModManager.RequestTempMod"/> after enabling
    /// ModsAllowed. Do not re-call AddTempModAsync on a timer — that restarts the temp session and can
    /// abort an in-flight install.
    /// </summary>
    static void AskMissing()
    {
        var mm = ModManager.Instance;
        if (mm == null) return;
        TryEnableModsOnRunner();
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
            try
            {
                _ = mm.AddTempModAsync(m.Id);
                Plugin.L.LogInfo($"[ModLivery] AddTempModAsync {m.Id}");
            }
            catch (Exception e) { Plugin.L.LogWarning($"[ModLivery] AddTempModAsync {m.Id}: {e.Message}"); }
        }
        RefreshRegistry(mm);
    }

    static void RefreshRegistry(ModManager mm)
    {
        try { mm.Liveries?.Refresh(); } catch { }
        try { mm.Liveries?.RefreshLocalLiveries(); } catch { }
        try { mm.Liveries?.RefreshAvailability(); } catch { }
    }

    static void LogDiag(IReadOnlyList<ModLiveryGateLogic.ModRef> missing)
    {
        try
        {
            bool mods = false; bool hasRunner = false;
            try
            {
                var onr = OverlandNetworkRunner.Instance;
                hasRunner = onr != null && !onr.WasCollected;
                if (hasRunner) mods = onr.ModsAllowed;
            }
            catch { }
            Plugin.L.LogInfo($"[ModLivery] diag runner={hasRunner} ModsAllowed={mods} missing={missing.Count}");
            var svc = ModManager.Instance?.Liveries;
            if (svc == null) { Plugin.L.LogWarning("[ModLivery] diag: Liveries null"); return; }
            foreach (var m in missing)
            {
                var exact = svc.TryGetLivery(m.Id, m.SubId, out _);
                var any0 = svc.TryGetLivery(m.Id, 0, out _);
                Plugin.L.LogInfo($"[ModLivery] diag id={m.Id} wantSub={m.SubId} exact={exact} sub0={any0}");
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[ModLivery] diag: {e.Message}"); }
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
            ClearWatch();
            UnhookRegistry();
            Plugin.L.LogInfo("[ModLivery] download done — all required liveries local");
            return;
        }

        if (now >= _nextDiag)
        {
            _nextDiag = now + 15f;
            LogDiag(missing);
        }

        if (now > _waitDeadline)
        {
            DownloadPhase = Phase.Failed;
            StatusKey = "modGate.timeout";
            LogDiag(missing);
            Plugin.L.LogWarning("[ModLivery] download timeout — still missing " + ModLiveryGateLogic.Format(missing));
            return;
        }

        if (now >= _nextRetry)
        {
            _nextRetry = now + 15f;
            _retries++;
            // Refresh only — re-calling AddTempModAsync restarts StartTempModSession and can abort installs.
            try { RefreshRegistry(ModManager.Instance); } catch { }
            Plugin.L.LogInfo($"[ModLivery] refresh #{_retries}, still missing {missing.Count}");
        }
    }

    /// <summary>Legacy alias used by the gate while downloading.</summary>
    public static void TickRefresh() => TickDownload();
}

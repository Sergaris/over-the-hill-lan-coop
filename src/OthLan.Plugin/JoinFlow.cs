using System;
using UnityEngine;

namespace OthLan;

/// <summary>
/// LAN join and the start watchdog. Overlay draws F7 and calls <see cref="Tick"/>;
/// Fusion's StartGame hook calls <see cref="NoteStartSeen"/> when the game already began.
/// </summary>
internal static class JoinFlow
{
    const float TimeoutBeforeStartGame = 90f; // region fetch over a flaky link can take a while
    const float TimeoutClientConnect = 25f;
    const float TimeoutHostStart = 60f;

    static volatile bool _querying, _queryDone;
    static volatile HostInfo.Info _queryResult;
    static volatile string _queryError;
    static int? _slotResult;
    static string _slotError;
    static bool _joinAllowMods = true;
    static string _queryIp;
    static int _queryPort;

    /// <summary>AllowMods decided while joining (host session props, or false if the client skipped liveries).</summary>
    public static bool AllowMods => _joinAllowMods;

    /// <summary>Main thread: finish a host query, then run the start watchdog.</summary>
    public static void Tick()
    {
        try { Poll(); }
        catch (Exception e) { _querying = false; Plugin.L.LogError($"[LAN] join: {e}"); }
        try { Watchdog(); }
        catch (Exception e) { Plugin.L.LogError($"[LAN] watchdog: {e}"); }
    }

    /// <summary>Arm the watchdog before pushing StateStartGame. StartGame itself has not been seen yet.</summary>
    public static void BeginWatch(LanRole role)
    {
        LanState.Watching = true;
        LanState.WatchRole = role;
        LanState.WatchStart = Time.realtimeSinceStartup;
        LanState.StartGameSeenAt = -1;
        LanState.FailureText = null;
    }

    /// <summary>Vanilla Create Server already set <see cref="LanState.StartGameSeenAt"/>. Arm only if nothing is watching.</summary>
    public static void NoteStartSeen(LanRole role)
    {
        if (LanState.Watching) return;
        LanState.Watching = true;
        LanState.WatchRole = role;
        LanState.WatchStart = LanState.StartGameSeenAt;
        LanState.FailureText = null;
    }

    /// <summary>Unity log listener: remember a start failure for the watchdog. No-op when not watching.</summary>
    public static void NoteUnityLog(string msg, LogType type)
    {
        if (!LanState.Watching || msg == null) return;
        if (msg.Contains("StartGameAsync failed") || msg.Contains("StartGame failed") ||
            (type == LogType.Exception && LanState.StartGameSeenAt < 0))
            LanState.FailureText = msg.Length > 200 ? msg[..200] : msg;
    }

    /// <summary>F10 / disarm: drop an in-flight host query and stop the watchdog.</summary>
    public static void Cancel()
    {
        _querying = false;
        LanState.Watching = false;
    }

    public static void Join(string endpoint)
    {
        if (LanState.Disabled) { LanState.SetStatus(BuildGate.StatusMsg()); Overlay.Show(); return; }
        if (LanState.Watching || _querying) { LanState.Status("status.joinBusy"); Overlay.Show(); return; }
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Trim().StartsWith("26.0.0.1") ||
            !LanState.TryParseEndpoint(endpoint, out var ip, out var port))
        {
            if (string.IsNullOrWhiteSpace(endpoint)) LanState.Status("status.noAddress");
            else LanState.Status("status.badAddress", endpoint);
            Plugin.L.LogWarning("[LAN] " + LanState.LastStatus.En());
            Overlay.Show();   // never start a join without a valid address
            return;
        }
        if (OverlandNetworkRunner.Instance == null) { LanState.Status("status.waitMenu"); Overlay.Show(); return; }
        if (!Autopilot.Active) Plugin.JoinAddress.Value = $"{ip}:{port}";   // self-test instances share one config file
        LanState.TargetIp = ip; LanState.TargetPort = port;
        // Ask the host which area/session it runs before starting anything (HostInfo, UDP port+1).
        _queryIp = ip; _queryPort = port; _queryResult = null; _queryError = null; _queryDone = false; _querying = true;
        LanState.Status("status.querying", $"{ip}:{port + 1}");
        Plugin.L.LogInfo("[LAN] " + LanState.LastStatus.En());
        // 0.1.9: do not auto-open F7 on join (Local Co-op / F9 / freeze-rejoin). Errors below still Show();
        // successful world entry Hide()s via FusionHooks → OnClientEnteredWorld.
        LanState.HostPlayers = 0;
        var hint = FusionHooks.PickClientSlot();   // main thread (reads the Radmin IP); the host prefers it if free
        new System.Threading.Thread(() =>
        {
            var r = HostInfo.Query(ip, port + 1, out var err);
            int? slot = null; string slotErr = null;
            if (r != null && r.Mod == Plugin.Version)   // same mod version: ask the host for a player slot (0.1.6)
                slot = HostInfo.RequestSlot(ip, port + 1, LanState.ClientId, hint, out slotErr);
            _slotResult = slot; _slotError = slotErr;
            _queryResult = r; _queryError = err; _queryDone = true;
        }) { IsBackground = true, Name = "OTH-LAN query" }.Start();
    }

    static void Poll()
    {
        if (!_querying || !_queryDone) return;
        _querying = false;
        var ip = _queryIp;
        var port = (ushort)_queryPort;
        var info = _queryResult;
        if (info == null)
        {
            if (!FallbackArea(ip, port, out var fallback)) return;
            FinishClientJoin(ip, port, fallback);
            return;
        }
        if (!TryAcceptHost(info, ip, port, out var areaName)) return;
        FinishClientJoin(ip, port, areaName);
    }

    static bool FallbackArea(string ip, ushort port, out string areaName)
    {
        areaName = Plugin.JoinArea.Value;
        if (string.IsNullOrEmpty(areaName))
        {
            LanState.Status("status.hostSilent", ip, _queryError, Plugin.Version, $"{port}-{port + 1}");
            Plugin.L.LogWarning("[LAN] " + LanState.LastStatus.En());
            Overlay.Show();
            return false;
        }
        _joinAllowMods = Plugin.AllowMods.Value;
        LanState.HostPlayers = 0;
        LanState.AssignedSlot = -1;
        Plugin.L.LogWarning($"[LAN] host info unavailable ({_queryError}); using config JoinArea={areaName}");
        return true;
    }

    /// <returns>False when the join was rejected or handed to the livery gate (that gate calls <see cref="FinishClientJoin"/>).</returns>
    static bool TryAcceptHost(HostInfo.Info info, string ip, ushort port, out string areaName)
    {
        areaName = null;
        Plugin.L.LogInfo($"[LAN] host info: mod={info.Mod} build={info.Build} players={info.Players} area={info.Area} props={string.Join(", ", info.Props)}");
        if (info.Mod != Plugin.Version)
        { LanState.Status("status.modMismatch", info.Mod, Plugin.Version); Overlay.Show(); return false; }
        if (!GateLogic.SameBuild(info.Build, info.GaHash, BuildGate.BuildKey, BuildGate.GaHash))
        { LanState.Status("status.gameMismatch", info.Build, BuildGate.BuildKey); Plugin.L.LogWarning("[LAN] " + LanState.LastStatus.En()); Overlay.Show(); return false; }
        if (string.IsNullOrEmpty(info.Area))
        { LanState.Status("status.noArea"); Overlay.Show(); return false; }
        areaName = info.Area;
        LanState.HostPlayers = info.Players;
        if (_slotResult == -1)
        { LanState.Status("status.full", info.Players); Plugin.L.LogWarning("[LAN] " + LanState.LastStatus.En()); Overlay.Show(); return false; }
        LanState.AssignedSlot = _slotResult ?? -1;
        Plugin.L.LogInfo(_slotResult.HasValue ? $"[LAN] host assigned player slot {_slotResult}" : $"[LAN] no slot from the host ({_slotError}); using the IP-derived slot");
        if (info.CarSync >= 0) CarSync.OnHostFlags(info.CarSync, ClientLink.Now);
        _joinAllowMods = RoomPropsLogic.AllowMods(info.Props) ?? Plugin.AllowMods.Value;
        Plugin.L.LogInfo($"[LAN] host session: mods {(_joinAllowMods ? "allowed" : "off")}");
        return !DeferLiveries(info, ip, port, areaName);
    }

    static bool DeferLiveries(HostInfo.Info info, string ip, ushort port, string areaName)
    {
        // Pre-join Modio livery gate: host advertises liverymods=…; client must have them locally.
        if (!_joinAllowMods || string.IsNullOrEmpty(info.LiveryMods)) return false;
        var required = ModLiveryGateLogic.Parse(info.LiveryMods);
        var missing = ModLiveryBridge.MissingOf(required);
        if (missing.Count == 0) return false;
        Plugin.L.LogInfo($"[LAN] missing host liveries: {ModLiveryGateLogic.Format(missing)}");
        LanState.Status("status.modsMissing", missing.Count);
        var areaPending = areaName;
        ModLiveryGate.Begin(required, missing,
            () => FinishClientJoin(ip, port, areaPending),
            () =>
            {
                LanState.Armed = LanRole.Off;
                LanState.Watching = false;
                LanState.Status("status.modsCancelled");
                Overlay.Show();
            },
            () =>
            {
                // Join without waiting for Modio: this client's session reports AllowMods=false.
                _joinAllowMods = false;
                Plugin.L.LogInfo("[LAN] joining without host liveries (AllowMods=false for this client)");
                LanState.Status("status.modsSkipped", missing.Count);
                FinishClientJoin(ip, port, areaPending);
            });
        return true;
    }

    static void FinishClientJoin(string ip, ushort port, string areaName)
    {
        var area = Overlay.FindArea(areaName, out var avail);
        if (area == null)
        {
            LanState.Status("status.noSuchArea", areaName, avail);
            Plugin.L.LogWarning("[LAN] " + LanState.LastStatus.En());
            Overlay.Show();
            return;
        }
        LanState.Armed = LanRole.Client;
        try
        {
            Overlay.PushStart(Fusion.GameMode.Client, $"LAN {ip}:{port}", area);
            LanState.Status("status.joining", $"{ip}:{port}", area.name);
            Plugin.L.LogInfo("[LAN] " + LanState.LastStatus.En());
            // World start is imminent: close F7 even if the user had it open to click Join.
            Overlay.Hide();
        }
        catch (Exception e)
        {
            LanState.Watching = false;
            LanState.Armed = LanRole.Off;
            LanState.Status("status.joinNotStarted", e.Message);
            Overlay.Show();
            Plugin.L.LogError($"[LAN] Join: {e}");
        }
    }

    static void Watchdog()
    {
        if (!LanState.Watching) return;
        var now = Time.realtimeSinceStartup;
        if (LanState.FailureText != null) { Recover(new Msg("recover.gameSaid", LanState.FailureText)); return; }
        if (LanState.StartGameSeenAt < 0)
        {
            if (now - LanState.WatchStart > TimeoutBeforeStartGame) Recover(new Msg("recover.netNotStarted", TimeoutBeforeStartGame));
            return;
        }
        var runner = OverlandNetworkRunner.Instance?.NetworkRunner;
        if (LanState.WatchRole == LanRole.Client)
        {
            if (runner != null && runner.IsConnectedToServer)
            {
                LanState.Watching = false;
                LanState.Status("status.connected", $"{LanState.TargetIp}:{LanState.TargetPort}");
                Plugin.L.LogInfo("[LAN] " + LanState.LastStatus.En());
            }
            else if (now - LanState.StartGameSeenAt > TimeoutClientConnect)
                Recover(new Msg("recover.clientTimeout", $"{LanState.TargetIp}:{LanState.TargetPort}", TimeoutClientConnect));
        }
        else if (runner != null && runner.IsRunning)
        {
            LanState.Watching = false;
            LanState.Status("status.hosting", Plugin.Port.Value);
            Plugin.L.LogInfo("[LAN] host running");
        }
        else if (now - LanState.StartGameSeenAt > TimeoutHostStart) Recover(new Msg("recover.hostTimeout", TimeoutHostStart));
    }

    static void Recover(Msg why)
    {
        LanState.Watching = false;
        LanState.Armed = LanRole.Off;
        LanState.Active = LanRole.Off;
        HostInfo.Clear();
        LanState.Status("recover.status", why);
        Plugin.L.LogError("[LAN] " + LanState.LastStatus.En());
        Overlay.Show();
        try
        {
            var onr = OverlandNetworkRunner.Instance;
            if (onr == null) { Plugin.L.LogError("[LAN] recover: no OverlandNetworkRunner"); return; }
            // The game's own path back to the menu (used on shutdown/disconnect); shows its "unable to start" notice.
            onr.QuitToMenuAsync(MainMenuContext.Reason.UnableToStartGame, "OTH-LAN: " + why);
            Plugin.L.LogInfo("[LAN] QuitToMenuAsync(UnableToStartGame) called");
        }
        catch (Exception e) { Plugin.L.LogError($"[LAN] recover failed: {e}"); }
    }
}

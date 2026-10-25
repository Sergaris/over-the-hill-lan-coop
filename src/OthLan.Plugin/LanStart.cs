using System;
using FunCore.Managers;
using UnityEngine;

namespace OthLan;

/// <summary>
/// 0.2.3 §5.4: arm/disarm, AreaInfo resolve, and StateStartGame push.
/// Extracted from Overlay so F7 stays UI-only; JoinFlow / AutoTest call here.
/// </summary>
internal static class LanStart
{
    public static void ArmHostFromUi()
    {
        if (LanState.Disabled) { LanState.SetStatus(BuildGate.StatusMsg()); Overlay.Show(); return; }
        LanState.Armed = LanRole.Host; LanState.HostPlayers = 0;
        LanState.Status("status.armedHost", $"{NetInfo.DisplayIp() ?? "<IP>"}:{Plugin.Port.Value}");
        Plugin.L.LogInfo("[LAN] " + LanState.LastStatus.En());
        Overlay.Show();
    }

    public static void Disarm()
    {
        LanState.Armed = LanRole.Off; JoinFlow.Cancel(); HostInfo.Clear();
        LanState.Status("status.off");
        Plugin.L.LogInfo("[LAN] disarmed");
    }

    /// <summary>AreaInfo ScriptableObject by asset name (null name = first loaded).
    /// Uses BenchmarkBootRunner.ResolveArea when the regions list is not yet in memory.</summary>
    public static AreaInfo FindArea(string name, out string available)
    {
        var names = new System.Collections.Generic.List<string>();
        AreaInfo first = null, hit = null;
        void Scan()
        {
            names.Clear(); first = null; hit = null;
            foreach (var a in Resources.FindObjectsOfTypeAll<AreaInfo>())
            {
                if (a == null) continue;
                names.Add(a.name);
                first ??= a;
                if (!string.IsNullOrEmpty(name) && a.name == name) hit = a;
            }
        }
        Scan();
        var res = string.IsNullOrEmpty(name) ? first : hit;
        if (res == null)
        {
            var want = string.IsNullOrEmpty(name) ? (Autopilot.Active ? Autopilot.Area : null) : name;
            try
            {
                if (want != null)
                {
                    var m = typeof(BenchmarkBootRunner).GetMethod("ResolveArea", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    res = (AreaInfo)m.Invoke(null, new object[] { want });
                    Plugin.L.LogInfo($"[LAN] area '{want}' not loaded yet: the game's resolver (RegionsList) -> {(res != null ? res.name : "null")}");
                }
            }
            catch (Exception e) { Plugin.L.LogError($"[LAN] ResolveArea('{want}'): {e}"); }
            if (res == null) Scan();
        }
        available = names.Count > 0 ? string.Join(", ", names) : "-";
        return res;
    }

    /// <summary>Starts the game's own StateStartGame flow with a minimal, valid GameSetupContext.</summary>
    public static void PushStart(Fusion.GameMode mode, string roomName, AreaInfo area)
    {
        var onr = OverlandNetworkRunner.Instance ?? throw new InvalidOperationException("game network runner not ready (wait for the main menu)");
        if (area == null) throw new InvalidOperationException("no AreaInfo loaded yet (wait for the main menu)");
        string region = null;
        try { region = onr.ConnectedRegion; }
        catch (Exception e) { Plugin.L.LogWarning($"[LAN] ConnectedRegion unavailable ({e.GetType().Name}); using none"); }
        var ctx = new GameSetupContext
        {
            GameMode = mode,
            RoomName = roomName,
            RoomCode = "",
            ServerRegion = region,
            MaxPlayers = LanState.EffectivePlayers,
            RoomProperties = new OverlandRoomProperties { AllowMods = mode == Fusion.GameMode.Client ? JoinFlow.AllowMods : Plugin.AllowMods.Value },
            AreaInfo = area,
        };
        Plugin.L.LogInfo($"[LAN] start ctx built");
        Plugin.L.LogInfo($"[LAN] start ctx: mode={mode} area={area.name} players={LanState.EffectivePlayers} region={ctx.ServerRegion ?? "null"} allowMods={ctx.RoomProperties.AllowMods}");
        JoinFlow.BeginWatch(mode == Fusion.GameMode.Client ? LanRole.Client : LanRole.Host);
        HybridSingleton<GameStateManager>.Instance.PushState(new StateStartGame(ctx));
    }
}

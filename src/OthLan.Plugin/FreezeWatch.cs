using System;
using UnityEngine;

namespace OthLan;

/// <summary>
/// Client: rejoin the same host when Fusion's simulation is stuck (see <see cref="FreezeLogic"/>). Uses the game's own
/// quit-to-menu and the normal OTH-LAN join (host info + slot request: the host gives the same slot back).
/// cfg [Outage] AutoRejoinOnFreeze (default on).
/// </summary>
internal static class FreezeWatch
{
    static readonly FreezeLogic.Cfg C = new();
    static int _lastTick = int.MinValue; static float _tickMovedAt = -1, _healthySince = -1, _lastAttemptAt = -1e9f;
    static string _rejoinTo; static float _rejoinAt = -1; static bool _instantiated;
    public static int Detected, Rejoins, Attempts; public static float LastFrozenS;

    public static void Tick()
    {
        var now = Time.realtimeSinceStartup;
        if (_rejoinTo != null) { PendingJoin(now); return; }
        if (LanState.Active != LanRole.Client) { _lastTick = int.MinValue; _tickMovedAt = -1; _healthySince = -1; return; }
        Fusion.NetworkRunner r = null;
        try { r = OverlandNetworkRunner.Instance?.NetworkRunner; } catch { }
        if (r == null || !r.IsRunning) { _tickMovedAt = -1; return; }
        int tick; try { tick = r.Tick.Raw; } catch { return; }
        if (tick != _lastTick || _tickMovedAt < 0) { _lastTick = tick; _tickMovedAt = now; }
        bool to; double loss;
        lock (ClientLink.Tracker) { to = !ClientLink.Tracker.HasReply || ClientLink.Tracker.TimedOut(NetClock.Now); loss = ClientLink.Tracker.LossPercent; }
        if (FreezeLogic.Healthy(to, loss, C)) { if (_healthySince < 0) _healthySince = now; } else _healthySince = -1;
        var frozen = now - _tickMovedAt; var healthy = _healthySince < 0 ? 0 : now - _healthySince;
        LastFrozenS = frozen;
        if (!Plugin.OutAutoRejoin.Value) return;
        if (!FreezeLogic.ShouldRejoin(frozen, healthy, Attempts, now - _lastAttemptAt, C)) return;
        Detected++; Attempts++; _lastAttemptAt = now;
        var ep = $"{LanState.TargetIp}:{LanState.TargetPort}";
        Plugin.L.LogWarning($"[Freeze] Fusion tick stuck at {tick} for {frozen:0.0} s while the side channel is healthy ({healthy:0.0} s, loss {loss:0} %): rejoining {ep} (attempt {Attempts}, slot {LanState.ClientSlot})");
        LanState.Status("status.freezeRejoin", ep);
        // 0.1.9: do not pop F7 on auto-rejoin; status stays in logs / next explicit F7.
        try { OverlandNetworkRunner.Instance?.QuitToMenuAsync(MainMenuContext.Reason.PlayerChoice, "OTH-LAN: Fusion simulation stuck, rejoining"); }
        catch (Exception e) { Plugin.L.LogWarning($"[Freeze] quit: {e.Message}"); }
        Autopilot.OnAutoRejoin();
        _rejoinTo = ep; _rejoinAt = now + 8f; _instantiated = false;
    }

    static void PendingJoin(float now)
    {
        if (now < _rejoinAt) return;
        Fusion.NetworkRunner r = null;
        try { r = OverlandNetworkRunner.Instance?.NetworkRunner; } catch { }
        if (r != null && r.IsRunning) { _rejoinAt = now + 2f; if (now - _lastAttemptAt > 60) { Plugin.L.LogWarning("[Freeze] still in the old session after 60 s; giving up"); _rejoinTo = null; } return; }
        if (OverlandNetworkRunner.Instance == null)
        {
            if (!_instantiated) { _instantiated = true; try { OverlandNetworkRunner.Instantiate(); Plugin.L.LogInfo("[Freeze] OverlandNetworkRunner.Instantiate()"); } catch (Exception e) { Plugin.L.LogWarning($"[Freeze] runner: {e.Message}"); } }
            _rejoinAt = now + 3f; if (now - _lastAttemptAt > 60) _rejoinTo = null; return;
        }
        var ep = _rejoinTo; _rejoinTo = null; Rejoins++;
        Plugin.L.LogWarning($"[Freeze] back in the menu: joining {ep}");
        try { JoinFlow.Join(ep); } catch (Exception e) { Plugin.L.LogError($"[Freeze] join: {e}"); }
    }

    public static string Describe() => $"freeze: detected={Detected} rejoins={Rejoins} lastFrozen={LastFrozenS:0.0}s";
}

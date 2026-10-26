using System;
using UnityEngine;

namespace OthLan;

/// <summary>
/// Client: the game delivers "release winch" only as an input button (VehicleButtons.ReleaseWinch), read by the host
/// from the client's Fusion input. Under packet loss the client's Fusion inputs stall for seconds, the host never sees
/// the press and the client's predicted detach is rolled back. A press while the own winch is attached is mirrored
/// with the game's reliable detach RPC.
///
/// That RPC also sets networked <c>WinchPointsDisabled</c>. Vanilla play re-enables points later; our mirror left them
/// stuck, so after the first release nobody could attach <em>to</em> that car (0.2.5.1 live: only client→host kept
/// working — the host never runs this client path). 0.2.5.2 re-enabled the local layer on the releasing client and on
/// the host; peers still saw layer 7 because the networked flag stayed true. 0.2.5.4: host clears
/// <c>WinchPointsDisabled</c> so OnChanged restores layer 11 on every peer (client↔client after detach).
/// </summary>
internal static class WinchRelease
{
    const float AttachedGraceS = 1f;
    const float HostReenableEveryS = 0.25f;
    static bool _wasDown;
    static float _attachedAt = -1;
    static float _nextHostScan;
    public static int Sent, HostReenabled;

    public static void Tick()
    {
        if (LanState.Active == LanRole.Host) { HostReenableTick(); Forget(); return; }
        if (LanState.Active != LanRole.Client) { Forget(); return; }
        var w = HostFollower.OwnWinch(out var car);
        if (w == null || car == null || car.WasCollected) { Forget(); return; }
        var now = Time.realtimeSinceStartup;
        if (w.WinchIsAttached) _attachedAt = now;
        var down = car.ButtonDown(NetworkInputData.VehicleButtons.ReleaseWinch);
        // the client's own prediction may already show the rope detached when the press is first seen
        if (down && !_wasDown && _attachedAt >= 0 && now - _attachedAt <= AttachedGraceS)
        {
            try
            {
                w.RPC_DetachWinchAndDisableWinchPoints();
                // RPC disables points for "put away" UX; re-enable locally until the host clears the networked flag.
                if (w.WinchPointsDisabled) w.EnableWinchPoints(true);
                Sent++;
                Plugin.L.LogInfo($"[WinchRelease] release press mirrored by the detach RPC (#{Sent})");
            }
            catch (Exception e) { Plugin.L.LogWarning($"[WinchRelease] detach RPC: {e.Message}"); }
            _attachedAt = -1;
        }
        _wasDown = down;
    }

    /// <summary>Host (state authority): clear stuck WinchPointsDisabled so every peer restores layer 11.</summary>
    static void HostReenableTick()
    {
        var now = Time.realtimeSinceStartup;
        if (now < _nextHostScan) return;
        _nextHostScan = now + HostReenableEveryS;
        try
        {
            foreach (var e in CarRegistry.Cars)
            {
                if (e.Fp == null || e.Fp.WasCollected || e.Fp.Object == null) continue;
                if (!e.Fp.Object.InputAuthority.IsRealPlayer) continue;
                var w = HostFollower.WinchOf(e.Fp);
                if (w == null || w.WasCollected) continue;
                if (!WinchReleaseLogic.ShouldClearDisabled(w.WinchPointsDisabled, w.WinchIsAttached)) continue;
                // Networked clear → OnChanged EnableWinchPoints(true) on every peer (client↔client).
                // Local EnableWinchPoints covers the host's own overlap until the next Render.
                if (e.Fp.Object.HasStateAuthority)
                    w.WinchPointsDisabled = false;
                w.EnableWinchPoints(true);
                HostReenabled++;
                if (HostReenabled <= 12)
                    Plugin.L.LogInfo($"[WinchRelease] host cleared WinchPointsDisabled on {e.Fp.gameObject.name} (#{HostReenabled})");
            }
        }
        catch (Exception ex) { Plugin.L.LogWarning($"[WinchRelease] host re-enable: {ex.Message}"); }
    }

    static void Forget() { _wasDown = false; _attachedAt = -1; }
}

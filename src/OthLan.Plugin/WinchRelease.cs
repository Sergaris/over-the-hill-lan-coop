using System;
using UnityEngine;

namespace OthLan;

/// <summary>
/// Client: the game delivers "release winch" only as an input button (VehicleButtons.ReleaseWinch), read by the host
/// from the client's Fusion input. Under packet loss the client's Fusion inputs stall for seconds, the host never sees
/// the press and the client's predicted detach is rolled back. A press while the own winch is attached is mirrored
/// with the game's reliable detach RPC, which the host applies regardless of input loss.
/// </summary>
internal static class WinchRelease
{
    const float AttachedGraceS = 1f;
    static bool _wasDown;
    static float _attachedAt = -1;
    public static int Sent;

    public static void Tick()
    {
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
                Sent++;
                Plugin.L.LogInfo($"[WinchRelease] release press mirrored by the detach RPC (#{Sent})");
            }
            catch (Exception e) { Plugin.L.LogWarning($"[WinchRelease] detach RPC: {e.Message}"); }
            _attachedAt = -1;
        }
        _wasDown = down;
    }

    static void Forget() { _wasDown = false; _attachedAt = -1; }
}

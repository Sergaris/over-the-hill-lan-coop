using System;
using UnityEngine;

namespace OthLan;

/// <summary>
/// 0.2.0 T3.5: where to draw a remote car — OTC3 StatePlayback when fresh, else Fusion + RemoteSmoother.
/// </summary>
internal static class RemoteCarView
{
    public static int StreamFrames, FusionFrames, Snaps;

    public static void Reset() { StreamFrames = FusionFrames = Snaps = 0; }

    /// <summary>True when this slot has a non-stale stream sample (RemoteSmooth should not fight it).</summary>
    public static bool HasFreshStream(int slot, double nowLocal)
    {
        if (slot < 0) return false;
        var r = CarRelay.PlaybackOf(slot).SampleAt(nowLocal);
        return r.Ok && !r.Stale;
    }

    public static void AfterFusionRender()
    {
        if (LanState.Active == LanRole.Off) return;
        var now = NetClock.Now;
        foreach (var e in CarRegistry.Cars)
        {
            try
            {
                if (e.IsOwn || e.Fp == null || e.Slot < 0) continue;
                var root = e.Fp.transform;
                if (root == null) continue;
                var r = CarRelay.PlaybackOf(e.Slot).SampleAt(now);
                if (!r.Ok || r.Stale) { FusionFrames++; continue; }
                root.SetPositionAndRotation(new Vector3(r.Px, r.Py, r.Pz), new Quaternion(r.Qx, r.Qy, r.Qz, r.Qw));
                if (e.Rb != null && e.Rb.isKinematic)
                {
                    e.Rb.position = root.position;
                    e.Rb.rotation = root.rotation;
                }
                if (r.Snap) Snaps++;
                StreamFrames++;
            }
            catch (Exception ex)
            {
                if (StreamFrames + FusionFrames < 4) Plugin.L.LogWarning($"[RemoteView] {ex.Message}");
            }
        }
    }
}

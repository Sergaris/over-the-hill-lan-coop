using System;
using UnityEngine;

namespace OthLan;

/// <summary>
/// 0.2.0 T2.3: single owner of netcode call order in a frame (plan §2.4).
/// </summary>
internal static class NetFrame
{
    static int _afterFusionFrame = -1;
    static bool _warnedNoRenderHook;

    /// <summary>Overlay.Update — session/UI already ran; this is the netcode half.</summary>
    public static void Update()
    {
        try { CarRegistry.Tick(); } catch (Exception e) { LogOnce("carreg", e); }
        try { PingHud.Tick(); } catch (Exception e) { LogOnce("ping", e); }
        try { CarSync.Tick(); } catch (Exception e) { LogOnce("carsync", e); }
        try { CarStreamSender.Tick(); } catch (Exception e) { LogOnce("stream", e); }
        try { OwnCar.Tick(); } catch (Exception e) { LogOnce("owncar", e); }
        try { FreezeWatch.Tick(); } catch (Exception e) { LogOnce("freeze", e); }
        try { ClientPhysics.Tick(); } catch (Exception e) { LogOnce("clientphys", e); }
        try { Diagnostics.Tick(); } catch (Exception e) { LogOnce("diag", e); }
        try { NetStats.Tick(); } catch (Exception e) { LogOnce("netstats", e); }
    }

    /// <summary>
    /// First NetworkRigidbody.Render this frame (OwnRender detour), after Fusion sim.
    /// Safe to call multiple times — runs once per frameCount.
    /// </summary>
    public static void AfterFusion()
    {
        var f = Time.frameCount;
        if (f == _afterFusionFrame) return;
        _afterFusionFrame = f;
        try { OwnClock.FrameAfterFusion(); } catch (Exception e) { LogOnce("ownclock", e); }
        try { RemoteCarView.AfterFusionRender(); } catch (Exception e) { LogOnce("remoteview", e); }
    }

    /// <summary>Overlay.LateUpdate — remote view/wheels; AfterFusion fallback if Render hook did not run.</summary>
    public static void Late()
    {
        if (_afterFusionFrame != Time.frameCount)
        {
            if (!OwnRender.HookOk && !_warnedNoRenderHook)
            {
                _warnedNoRenderHook = true;
                Plugin.L.LogWarning("[NetFrame] NetworkRigidbody.Render hook unavailable; AfterFusion runs from LateUpdate");
            }
            AfterFusion();
        }
        try { RemoteSmooth.LateTick(); } catch (Exception e) { LogOnce("remotesmooth", e); }
        try { RemoteVisuals.LateTick(); } catch (Exception e) { LogOnce("remotevisuals", e); }
    }

    static readonly System.Collections.Generic.HashSet<string> _logged = new();
    static void LogOnce(string key, Exception e)
    {
        if (!_logged.Add(key)) return;
        Plugin.L.LogWarning($"[NetFrame] {key}: {e.Message}");
    }
}

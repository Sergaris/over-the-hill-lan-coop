using UnityEngine;

namespace OthLan;

/// <summary>
/// 0.2.0 T2.3: own-car frame steps and render alpha after Fusion (plan OwnClock).
/// Debt / local steps stay in <see cref="OwnLocal"/>; this is the call site + alpha for <see cref="OwnRender"/>.
/// </summary>
internal static class OwnClock
{
    /// <summary>Alpha along the last own step for render lerp (Debt clamped). Same formula OwnRender used inline.</summary>
    public static float Alpha => Mathf.Clamp((float)OwnLocal.Debt, -1f, 2f);

    /// <summary>NetClock seconds since the previous frame's after-Fusion point (0 on the first frame): the time step the
    /// own car is drawn with this frame. Unity's deltaTime is the previous frame's length, not this one.</summary>
    public static double FrameSeconds { get; private set; }
    static double _lastAt = -1;

    /// <summary>After Fusion sim (NetFrame.AfterFusion): accumulate debt and fill local steps if Fusion did not tick.</summary>
    public static void FrameAfterFusion()
    {
        var now = NetClock.Now;
        FrameSeconds = _lastAt < 0 ? 0 : now - _lastAt; _lastAt = now;
        OwnLocal.Frame();
    }
}

using System;

namespace OthLan;

/// <summary>
/// 0.2.3 T3.2: pure owner of wheel roll wrap / integrate / phase-pull / suspension delta (§8.2).
/// Sample uses signed body-forward spin; SuspM may come from WTU restY. RollDeg is never taken from a
/// “rest” snapshot that already includes spin (that zeroed phase — see review of afb406f).
/// </summary>
public static class WheelVisualLogic
{
    public const float SoftPullDegPerSec = 90f;

    public static float WrapDeg360(float deg)
    {
        if (float.IsNaN(deg) || float.IsInfinity(deg)) return 0;
        deg %= 360f;
        if (deg < 0) deg += 360f;
        return deg;
    }

    /// <summary>Integrate signed spin (rad/s) over dt into roll degrees.</summary>
    public static float IntegrateRollDeg(float prevDeg, float spinRadS, float dt)
    {
        if (float.IsNaN(spinRadS)) spinRadS = 0;
        if (float.IsNaN(dt) || dt <= 0 || dt > 0.25f) return WrapDeg360(prevDeg);
        return WrapDeg360(prevDeg + spinRadS * (180f / (float)Math.PI) * dt);
    }

    public static float DeltaDeg(float from, float to)
    {
        var d = to - from;
        while (d > 180f) d -= 360f;
        while (d < -180f) d += 360f;
        return d;
    }

    /// <summary>Integrate from A then soft-pull phase toward B (≤ SoftPullDegPerSec).</summary>
    public static float SoftPullRoll(float fromDeg, float targetDeg, float spinRadS, float dt,
        float maxDegPerSec = SoftPullDegPerSec)
    {
        var integrated = IntegrateRollDeg(fromDeg, spinRadS, dt);
        if (float.IsNaN(dt) || dt <= 0) return integrated;
        var err = DeltaDeg(integrated, targetDeg);
        var maxPull = Math.Max(0f, maxDegPerSec) * dt;
        if (err > maxPull) err = maxPull;
        if (err < -maxPull) err = -maxPull;
        return WrapDeg360(integrated + err);
    }

    /// <summary>Suspension compression along local Y relative to rest pose (metres).</summary>
    public static float SuspDeltaM(float restY, float localY)
    {
        if (float.IsNaN(restY) || float.IsNaN(localY)) return 0;
        return localY - restY;
    }

    /// <summary>True when it is safe to snapshot rest localPosition (near stop; steer irrelevant for Y).</summary>
    public static bool CanCaptureRestPos(float speedMs, float maxSpeed = 0.5f)
        => Math.Abs(speedMs) <= maxSpeed;
}

using System;

namespace OthLan;

/// <summary>
/// 0.2.0 T1.1: which angular rate (rad/s) to use for a remote wheel visual.
/// Owner of the netAng-vs-bodyV rule. <c>RemoteVisuals</c> applies the result to transforms.
/// </summary>
public static class WheelSpinLogic
{
    public const double NetAngAliveThresholdRadS = 0.5;
    public const double NetAngAliveHoldSeconds = 1.0;
    public const double BodyFastMs = 2.0;
    public const double NetAngMinFractionOfBody = 0.20;
    public const double DefaultRadius = 0.38;

    /// <summary>True when any wheel angular velocity is |ω| &gt; threshold (proxy not stuck at zeros).</summary>
    public static bool AnyNetAngAlive(float[] netAng, double threshold = NetAngAliveThresholdRadS)
    {
        if (netAng == null) return false;
        for (var i = 0; i < netAng.Length; i++)
            if (Math.Abs(netAng[i]) > threshold) return true;
        return false;
    }

    /// <summary>
    /// Wheel spin rate in rad/s.
    /// Use <paramref name="netAng"/> only while <paramref name="netAngAlive"/>; otherwise bodyV/radius.
    /// If body is fast but netAng implies &lt; 20 % of that speed, fall back to bodyV/radius.
    /// </summary>
    public static double RateRadS(double bodyV, double netAng, double radius, bool netAngAlive)
    {
        if (double.IsNaN(bodyV)) bodyV = 0;
        if (double.IsNaN(netAng)) netAng = 0;
        if (double.IsNaN(radius) || radius < 1e-3) radius = DefaultRadius;

        if (!netAngAlive) return bodyV / radius;

        if (Math.Abs(bodyV) > BodyFastMs
            && Math.Abs(netAng * radius) < NetAngMinFractionOfBody * Math.Abs(bodyV))
            return bodyV / radius;

        return netAng;
    }

    /// <summary>Deadline extension when any netAng is alive this frame.</summary>
    public static double ExtendAliveUntil(double now, double currentUntil, bool anyAliveNow, double holdSeconds = NetAngAliveHoldSeconds)
    {
        if (anyAliveNow) return now + Math.Max(0, holdSeconds);
        return currentUntil;
    }

    public static bool IsAlive(double now, double aliveUntil) => now < aliveUntil;
}

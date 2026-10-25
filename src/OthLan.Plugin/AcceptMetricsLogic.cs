using System;

namespace OthLan;

/// <summary>
/// 0.2.3: acceptance metric formulas for plan §5.1–5.2 (wheelErr, extrapErr bookkeeping, proxyPull).
/// Pure — unit-tested. Counters live in RemoteWheelVisuals / RemoteCarView / NetStats.
/// </summary>
public static class AcceptMetricsLogic
{
    public const double WheelFastMs = 2.0;
    public const double WheelErrLimit = 0.10; // 10 % of v/r
    public const double ProxyEnterM = 0.15;
    public const double ProxyLeaveM = 0.05;

    /// <summary>
    /// Signed body-forward speed (m/s) from world velocity and sample orientation — same basis as
    /// <c>CarStreamSender</c> spin (<c>Dot(v, forward)</c>). Used so reverse does not look like 200% wheelErr.
    /// </summary>
    public static double BodyForwardMs(double vx, double vy, double vz, double qx, double qy, double qz, double qw)
    {
        if (double.IsNaN(vx) || double.IsNaN(vy) || double.IsNaN(vz)) return 0;
        if (double.IsNaN(qx) || double.IsNaN(qy) || double.IsNaN(qz) || double.IsNaN(qw)) return 0;
        // Unity Quaternion * Vector3.forward
        var fx = 2.0 * (qx * qz + qw * qy);
        var fy = 2.0 * (qy * qz - qw * qx);
        var fz = 1.0 - 2.0 * (qx * qx + qy * qy);
        var len = Math.Sqrt(fx * fx + fy * fy + fz * fz);
        if (len < 1e-8) return 0;
        return (vx * fx + vy * fy + vz * fz) / len;
    }

    /// <summary>
    /// Relative wheel spin error vs signed body-forward speed: |ω·r − v| / max(|v|, ε).
    /// NaN when |v| ≤ <see cref="WheelFastMs"/> (gate does not apply).
    /// </summary>
    public static double WheelErrFrac(double spinRadS, double bodyV, double radius = WheelSpinLogic.DefaultRadius)
    {
        if (double.IsNaN(bodyV) || Math.Abs(bodyV) <= WheelFastMs) return double.NaN;
        if (double.IsNaN(spinRadS)) spinRadS = 0;
        if (double.IsNaN(radius) || radius < 1e-3) radius = WheelSpinLogic.DefaultRadius;
        var expect = bodyV / radius;
        var got = spinRadS;
        return Math.Abs(got - expect) * radius / Math.Abs(bodyV);
    }

    public static bool WheelErrOverLimit(double frac, double limit = WheelErrLimit)
        => !double.IsNaN(frac) && frac > limit;

    /// <summary>Euclidean gap between extrapolated draw pose and newest stream sample (extrapErr).</summary>
    public static double ExtrapErrM(double drawX, double drawY, double drawZ, double newestX, double newestY, double newestZ)
        => SyncProbeLogic.DiffMeters((float)drawX, (float)drawY, (float)drawZ, (float)newestX, (float)newestY, (float)newestZ);

    /// <summary>
    /// Advance proxy-pull timer: starts when err ≥ enterM, ends when err ≤ leaveM.
    /// Returns completed pull duration (seconds), or NaN if still open / idle.
    /// </summary>
    public static double ProxyPullStep(ref double pullSince, double errM, double now,
        double enterM = ProxyEnterM, double leaveM = ProxyLeaveM)
    {
        if (double.IsNaN(errM) || double.IsNaN(now)) return double.NaN;
        if (pullSince < 0)
        {
            if (errM >= enterM) pullSince = now;
            return double.NaN;
        }
        if (errM > leaveM) return double.NaN;
        var dt = now - pullSince;
        pullSince = -1;
        return dt < 0 ? 0 : dt;
    }

    /// <summary>Drop an open pull without counting it (stream gap / leave Servo). Returns abandoned age, or NaN.</summary>
    public static double ProxyPullAbort(ref double pullSince, double now)
    {
        if (pullSince < 0 || double.IsNaN(now)) { pullSince = -1; return double.NaN; }
        var dt = now - pullSince;
        pullSince = -1;
        return dt < 0 ? 0 : dt;
    }

    public static double ProxyPullOpenAge(double pullSince, double now)
        => pullSince < 0 || double.IsNaN(now) ? double.NaN : Math.Max(0, now - pullSince);
}

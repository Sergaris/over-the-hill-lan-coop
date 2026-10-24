using System;

namespace OthLan;

/// <summary>
/// 0.2.0 T0.5: speed from successive rendered poses (kinematic remotes have zero rigidbody velocity).
/// Pure logic (unit-tested). Owner of «v from Δpose / dt» for NetStats hitch lines.
/// </summary>
public static class PoseSpeedLogic
{
    public const double MinDt = 1e-3;

    /// <summary>Metres per second from a position delta over <paramref name="dt"/> seconds.</summary>
    public static double FromDelta(double dx, double dy, double dz, double dt)
    {
        if (double.IsNaN(dx) || double.IsNaN(dy) || double.IsNaN(dz) || double.IsNaN(dt)) return 0;
        if (dt < MinDt) return 0;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz) / dt;
    }

    public static double FromDelta(double x0, double y0, double z0, double x1, double y1, double z1, double dt)
        => FromDelta(x1 - x0, y1 - y0, z1 - z0, dt);
}

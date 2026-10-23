using System;

namespace OthLan;

/// <summary>
/// 0.1.8 netcode v3 (docs/NETCODE-v3-RESEARCH.md): visual smoothing of ANOTHER player's car, pure logic (unit-tested).
/// Input every frame: the pose Fusion would draw (snapshot interpolation; on the host: the host's own simulation).
/// Output: the pose we draw. Physics and collisions keep using the real rigidbody; only the drawn transform changes.
///  1. Dead reckoning while Fusion's interpolation is starved (host snapshot stall: Fusion clamps to the newest snapshot
///     and the car freezes): the drawn reference keeps moving with the last measured velocity for up to
///     ExtrapolateSeconds, then stops.
///  2. Error decay instead of jumps (Fiedler "visual smoothing", Murphy "projective velocity blending" idea): a
///     discontinuity in Fusion's pose (snapshots resume, time re-sync, follower correction on the host) is absorbed into
///     a visual offset that decays exponentially (Tau) with a bounded correction speed (MaxCorrection + SpeedFactor*v),
///     so the drawn car never moves faster than plausible because of the network.
///  3. Teleports (above SnapMeters) are shown at once (respawn, rescue, map change).
/// Small per-frame deviations (normal acceleration, below AbsorbMeters) are passed through untouched: no added latency.
/// </summary>
public sealed class RemoteSmoother
{
    public sealed class Cfg
    {
        public double ExtrapolateSeconds = 0.5, Tau = 0.25, TauLarge = 0.1, LargeMeters = 1.0, SmallMeters = 0.25, MaxCorrection = 6, SpeedFactor = 0.5, SnapMeters = 15, AbsorbMeters = 0.1;
        public double FrozenEps = 1e-4, MinSpeed = 0.5, VelAlpha = 0.5;
    }
    public enum State { Idle, Follow, Extrapolate, Hold }

    bool _has, _hasV, _stalled, _wasAbsorb;
    public double Rx, Ry, Rz;          // reference (Fusion's pose, or dead-reckoned)
    public double Vx, Vy, Vz;          // velocity estimate (m/s) from Fusion's motion
    public double Ox, Oy, Oz;          // visual offset (drawn = reference + offset)
    double _fx, _fy, _fz;              // last Fusion pose
    public double FrozenFor;
    public State Last = State.Idle;
    public int Absorbed, Snaps, ExtrapolatedFrames;
    public bool LastAbsorbed;          // this step absorbed a discontinuity (caller: absorb rotation too)

    public void Reset() { _has = _hasV = _stalled = false; Ox = Oy = Oz = Vx = Vy = Vz = 0; FrozenFor = 0; Last = State.Idle; }
    public double OffsetLength => Math.Sqrt(Ox * Ox + Oy * Oy + Oz * Oz);
    public double Speed => Math.Sqrt(Vx * Vx + Vy * Vy + Vz * Vz);

    /// <summary>One frame. (fx,fy,fz) = Fusion's pose this frame; <paramref name="starved"/> = no new host snapshot for a while
    /// (a car that really stopped, e.g. against a wall, is never extrapolated). Returns the pose to draw.</summary>
    public (double x, double y, double z) Step(double fx, double fy, double fz, double dt, Cfg c, bool starved = true)
    {
        LastAbsorbed = false;
        if (!_has || dt <= 0 || dt > 0.5)
        {
            if (!_has || dt > 0.5) { Vx = Vy = Vz = 0; Ox = Oy = Oz = 0; _hasV = false; }
            _stalled = false;
            _has = true; Rx = _fx = fx; Ry = _fy = fy; Rz = _fz = fz; FrozenFor = 0; Last = State.Follow;
            return (Rx + Ox, Ry + Oy, Rz + Oz);
        }
        double mx = fx - _fx, my = fy - _fy, mz = fz - _fz;
        var moved = Math.Sqrt(mx * mx + my * my + mz * mz);
        if (moved < c.FrozenEps && (_stalled || (Speed > c.MinSpeed && starved)))
        {   // Fusion is not moving the car although it was moving and no host data arrives: interpolation starved.
            // Stays "stalled" until Fusion moves the car again (no slide back to the frozen point meanwhile).
            _stalled = true; FrozenFor += dt;
            if (FrozenFor <= c.ExtrapolateSeconds) { Rx += Vx * dt; Ry += Vy * dt; Rz += Vz * dt; Last = State.Extrapolate; ExtrapolatedFrames++; }
            else Last = State.Hold;
        }
        else
        {
            var hold = _stalled && FrozenFor > c.ExtrapolateSeconds;
            _stalled = false; FrozenFor = 0;
            double ex = Rx + (hold ? 0 : Vx * dt), ey = Ry + (hold ? 0 : Vy * dt), ez = Rz + (hold ? 0 : Vz * dt);
            double jx = ex - fx, jy = ey - fy, jz = ez - fz;
            var jump = Math.Sqrt(jx * jx + jy * jy + jz * jz);
            if (!_hasV) { Vx = mx / dt; Vy = my / dt; Vz = mz / dt; _hasV = true; }
            else if (jump > c.SnapMeters || OffsetLength + jump > c.SnapMeters * 1.5)
            { Ox = Oy = Oz = 0; Vx = Vy = Vz = 0; _hasV = false; Snaps++; }
            else if (jump > c.AbsorbMeters)
            {   // discontinuity: keep the drawn car where it would have been, decay the difference below
                Ox += jx; Oy += jy; Oz += jz; Absorbed++; LastAbsorbed = true;
                if (!_wasAbsorb) { } // velocity kept: the next normal frame re-estimates it
            }
            else
            {
                var a = c.VelAlpha;
                Vx += a * (mx / dt - Vx); Vy += a * (my / dt - Vy); Vz += a * (mz / dt - Vz);
            }
            _wasAbsorb = LastAbsorbed;
            Rx = fx; Ry = fy; Rz = fz; Last = State.Follow;
        }
        _fx = fx; _fy = fy; _fz = fz;
        Decay(dt, c);
        return (Rx + Ox, Ry + Oy, Rz + Oz);
    }

    /// <summary>Exponential decay of the offset with a speed limit (m/s) on the correction. Adaptive time constant
    /// (Fiedler, "State Synchronization", visual smoothing): slow for small errors (smooths jitter), fast for big pops
    /// (a long slide is more disorienting than a quick one); blended linearly between SmallMeters and LargeMeters.</summary>
    void Decay(double dt, Cfg c)
    {
        var len = OffsetLength; if (len < 1e-6) { Ox = Oy = Oz = 0; return; }
        var tau = TauFor(len, c);
        var step = len * (1 - Math.Exp(-dt / Math.Max(tau, 1e-3)));
        var cap = (c.MaxCorrection + c.SpeedFactor * Speed) * dt;
        if (step > cap) step = cap;
        var k = (len - step) / len;
        Ox *= k; Oy *= k; Oz *= k;
        if (OffsetLength < 0.002) Ox = Oy = Oz = 0;
    }

    public static double TauFor(double err, Cfg c)
    {
        var u = Math.Clamp((err - c.SmallMeters) / Math.Max(c.LargeMeters - c.SmallMeters, 1e-6), 0, 1);
        return c.Tau + (c.TauLarge - c.Tau) * u;
    }

    /// <summary>Same decay factor for a rotation offset of <paramref name="deg"/> degrees: fraction to keep.</summary>
    public static double RotKeep(double deg, double dt, double tau, double maxDegPerS)
    {
        if (deg < 1e-3) return 0;
        var step = deg * (1 - Math.Exp(-dt / Math.Max(tau, 1e-3)));
        if (step > maxDegPerS * dt) step = maxDegPerS * dt;
        return (deg - step) / deg;
    }
}

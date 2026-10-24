using System;

namespace OthLan;

/// <summary>
/// 0.1.8 / 0.2.0 T1.2: visual smoothing of ANOTHER player's car, pure logic (unit-tested).
/// Owner of remote visual pose vs Fusion pose. Physics stays on the rigidbody; only the drawn transform changes.
/// T1.2: starve = no host snapshots (caller); real stop is not absorb; extrapolate XZ only; floor + Hold timeout.
/// </summary>
public sealed class RemoteSmoother
{
    public sealed class Cfg
    {
        public double ExtrapolateSeconds = 0.5, Tau = 0.25, TauLarge = 0.1, LargeMeters = 1.0, SmallMeters = 0.25, MaxCorrection = 6, SpeedFactor = 0.5, SnapMeters = 15, AbsorbMeters = 0.1;
        public double FrozenEps = 1e-4, MinSpeed = 0.5, VelAlpha = 0.5;
        /// <summary>Drawn y never below Fusion y − this (stops underground after fall+stop).</summary>
        public double MaxBelowMeters = 0.25;
        /// <summary>Seconds in Hold before snapping to Fusion pose.</summary>
        public double HoldMaxSeconds = 1.0;
        /// <summary>If |offset| stays above this for <see cref="OffsetCapSeconds"/>, snap.</summary>
        public double OffsetCapMeters = 3.0;
        public double OffsetCapSeconds = 0.5;
    }
    public enum State { Idle, Follow, Extrapolate, Hold }

    bool _has, _hasV, _stalled, _wasAbsorb;
    public double Rx, Ry, Rz;          // reference (Fusion's pose, or dead-reckoned)
    public double Vx, Vy, Vz;          // velocity estimate (m/s) from Fusion's motion
    public double Ox, Oy, Oz;          // visual offset (drawn = reference + offset)
    double _fx, _fy, _fz;              // last Fusion pose
    double _holdFor;                   // time spent in Hold this stall
    double _bigOffsetFor;              // time |O| > OffsetCapMeters
    public double FrozenFor;
    public State Last = State.Idle;
    public int Absorbed, Snaps, ExtrapolatedFrames;
    public bool LastAbsorbed;          // this step absorbed a discontinuity (caller: absorb rotation too)

    public void Reset()
    {
        _has = _hasV = _stalled = false; Ox = Oy = Oz = Vx = Vy = Vz = 0; FrozenFor = _holdFor = _bigOffsetFor = 0; Last = State.Idle;
    }
    public double OffsetLength => Math.Sqrt(Ox * Ox + Oy * Oy + Oz * Oz);
    public double Speed => Math.Sqrt(Vx * Vx + Vy * Vy + Vz * Vz);

    /// <summary>One frame. (fx,fy,fz) = Fusion's pose; <paramref name="starved"/> = no new host snapshot for a while.</summary>
    public (double x, double y, double z) Step(double fx, double fy, double fz, double dt, Cfg c, bool starved = true)
    {
        LastAbsorbed = false;
        if (!_has || dt <= 0 || dt > 0.5)
        {
            if (!_has || dt > 0.5) { Vx = Vy = Vz = 0; Ox = Oy = Oz = 0; _hasV = false; _holdFor = _bigOffsetFor = 0; }
            _stalled = false;
            _has = true; Rx = _fx = fx; Ry = _fy = fy; Rz = _fz = fz; FrozenFor = 0; Last = State.Follow;
            return (Rx + Ox, Ry + Oy, Rz + Oz);
        }
        double mx = fx - _fx, my = fy - _fy, mz = fz - _fz;
        var moved = Math.Sqrt(mx * mx + my * my + mz * mz);

        if (moved < c.FrozenEps)
        {
            if (starved)
            {
                // Host snapshots stopped: dead-reckon horizontally only (Y stays at last Fusion height).
                _stalled = true; FrozenFor += dt;
                if (FrozenFor <= c.ExtrapolateSeconds)
                {
                    Rx += Vx * dt;
                    Rz += Vz * dt;
                    // Ry unchanged
                    Last = State.Extrapolate; ExtrapolatedFrames++;
                    _holdFor = 0;
                }
                else
                {
                    Last = State.Hold;
                    _holdFor += dt;
                    if (_holdFor > c.HoldMaxSeconds)
                        SnapToFusion(fx, fy, fz);
                }
            }
            else
            {
                // Real stop (Fusion pose frozen, snapshots still arriving): do not absorb V·dt into offset.
                _stalled = false; FrozenFor = 0; _holdFor = 0;
                Vx = Vy = Vz = 0; _hasV = true;
                Rx = fx; Ry = fy; Rz = fz;
                Last = State.Follow;
            }
        }
        else
        {
            var wasHold = _stalled && FrozenFor > c.ExtrapolateSeconds;
            _stalled = false; FrozenFor = 0; _holdFor = 0;
            double ex = Rx + (wasHold ? 0 : Vx * dt), ey = Ry + (wasHold ? 0 : Vy * dt), ez = Rz + (wasHold ? 0 : Vz * dt);
            double jx = ex - fx, jy = ey - fy, jz = ez - fz;
            var jump = Math.Sqrt(jx * jx + jy * jy + jz * jz);
            if (!_hasV) { Vx = mx / dt; Vy = my / dt; Vz = mz / dt; _hasV = true; }
            else if (jump > c.SnapMeters || OffsetLength + jump > c.SnapMeters * 1.5)
            { Ox = Oy = Oz = 0; Vx = Vy = Vz = 0; _hasV = false; Snaps++; }
            else if (jump > c.AbsorbMeters)
            {
                Ox += jx; Oy += jy; Oz += jz; Absorbed++; LastAbsorbed = true;
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
        // Floor: drawn y never below Fusion y − MaxBelowMeters (clamp offset, not reference).
        var floor = fy - c.MaxBelowMeters;
        if (Ry + Oy < floor) Oy = floor - Ry;
        EnforceOffsetCap(dt, fx, fy, fz, c);
        return (Rx + Ox, Ry + Oy, Rz + Oz);
    }

    void SnapToFusion(double fx, double fy, double fz)
    {
        Ox = Oy = Oz = 0; Vx = Vy = Vz = 0; _hasV = false;
        Rx = _fx = fx; Ry = _fy = fy; Rz = _fz = fz;
        FrozenFor = _holdFor = _bigOffsetFor = 0; _stalled = false;
        Snaps++; Last = State.Follow;
    }

    void EnforceOffsetCap(double dt, double fx, double fy, double fz, Cfg c)
    {
        if (OffsetLength > c.OffsetCapMeters) _bigOffsetFor += dt;
        else _bigOffsetFor = 0;
        if (_bigOffsetFor > c.OffsetCapSeconds) SnapToFusion(fx, fy, fz);
    }

    void Decay(double dt, Cfg c)
    {
        DecayOffset(ref Ox, ref Oy, ref Oz, dt, c, Speed);
    }

    /// <summary>0.2.0 T3.5: shared offset decay (RemoteCarView source-switch blend). Single owner of the formula.</summary>
    public static void DecayOffset(ref double ox, ref double oy, ref double oz, double dt, Cfg c, double speed = 0)
    {
        var len = Math.Sqrt(ox * ox + oy * oy + oz * oz);
        if (len < 1e-6) { ox = oy = oz = 0; return; }
        var tau = TauFor(len, c);
        var step = len * (1 - Math.Exp(-dt / Math.Max(tau, 1e-3)));
        var cap = (c.MaxCorrection + c.SpeedFactor * speed) * dt;
        if (step > cap) step = cap;
        var k = (len - step) / len;
        ox *= k; oy *= k; oz *= k;
        if (Math.Sqrt(ox * ox + oy * oy + oz * oz) < 0.002) ox = oy = oz = 0;
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

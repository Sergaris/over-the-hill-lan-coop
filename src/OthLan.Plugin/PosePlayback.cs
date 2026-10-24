using System;

namespace OthLan;

/// <summary>
/// 0.1.9 host-side pose jitter buffer + Hermite playback (docs/NETCODE-v4 §2.3). Pure logic, unit-tested.
/// Wall-clock stamped samples; adaptive playback delay from inter-arrival jitter; Hermite between two bracketing
/// poses. When the buffer is dry, constant-velocity dead reckoning from the newest sample (capped).
/// Does not own Unity types — CarSync feeds floats and consumes the sampled target.
/// </summary>
public sealed class PosePlayback
{
    public sealed class Cfg
    {
        public int Capacity = 16;
        // Delay = inter-arrival jitter only. One-way / prediction lead stays in CarSync PoseAgeFromTicks.
        // When the play head reaches the newest sample we HOLD it (no dead-reckon here) — CarSync tick-age
        // extrapolates; doing both made p6L host-remote worse (p99 8→17 cm).
        public double MinDelayS = 0.033;      // ~1 packet at 30 Hz
        public double MaxDelayS = 0.10;
        public double DelayEwma = 0.25;
        public double JitterEwma = 0.3;
        public double MaxInterarrivalS = 0.5;
    }

    public struct Sample
    {
        public double T;                      // host wall-clock receive time (s)
        public double Px, Py, Pz, Vx, Vy, Vz;
        public double Rx, Ry, Rz, Rw;         // quaternion
        public double Ax, Ay, Az;
        public int Tick; public uint Seq;
    }

    public struct Target
    {
        public double Px, Py, Pz, Vx, Vy, Vz;
        public double Rx, Ry, Rz, Rw, Ax, Ay, Az;
        public int Tick;
        public bool Ok; public bool Extrapolated; public double DelayS; public int Buffered;
    }

    readonly Cfg _c;
    Sample[] _ring;
    int _count, _head;                        // head = next write; samples are [_head-_count .. _head)
    double _delay = 0.06, _jitter, _lastArrive = -1;
    public int Pushes, Samples, Extraps, Snaps;

    public PosePlayback(Cfg c = null)
    {
        _c = c ?? new Cfg();
        _ring = new Sample[Math.Max(4, _c.Capacity)];
        _delay = Math.Clamp(_c.MinDelayS, _c.MinDelayS, _c.MaxDelayS);
    }

    public void Reset()
    {
        _count = _head = 0; _lastArrive = -1; _jitter = 0;
        _delay = Math.Clamp(_c.MinDelayS, _c.MinDelayS, _c.MaxDelayS);
        Pushes = Samples = Extraps = Snaps = 0;
    }

    public int Count => _count;
    public double DelayS => _delay;

    /// <summary>Push a newly received client pose (host wall clock <paramref name="recvT"/>).</summary>
    public void Push(in Sample s)
    {
        if (_lastArrive >= 0)
        {
            var gap = s.T - _lastArrive;
            if (gap > 0 && gap < _c.MaxInterarrivalS)
            {
                // deviation from expected 1/SendHz is absorbed into EWMA jitter (we do not know SendHz here;
                // use half the gap as a crude one-way jitter proxy when gaps vary).
                var mean = _jitter > 0 ? _jitter : gap;
                var d = Math.Abs(gap - mean);
                _jitter += _c.JitterEwma * (d - _jitter);
                var want = Math.Clamp(_c.MinDelayS + 2.0 * _jitter, _c.MinDelayS, _c.MaxDelayS);
                _delay += _c.DelayEwma * (want - _delay);
            }
        }
        _lastArrive = s.T;
        _ring[_head] = s;
        _head = (_head + 1) % _ring.Length;
        if (_count < _ring.Length) _count++;
        Pushes++;
    }

    Sample At(int i) // 0 = oldest, Count-1 = newest
    {
        var idx = (_head - _count + i + _ring.Length * 4) % _ring.Length;
        return _ring[idx];
    }

    /// <summary>Sample the playback pose at host wall time <paramref name="now"/> (play head = now − delay).</summary>
    public Target SampleAt(double now)
    {
        Samples++;
        var t = new Target { DelayS = _delay, Buffered = _count };
        if (_count == 0) return t;
        var play = now - _delay;
        var newest = At(_count - 1);
        var oldest = At(0);

        if (play <= oldest.T)
        {   // play head behind buffer: hold oldest
            Fill(ref t, oldest); t.Ok = true; return t;
        }
        if (play >= newest.T)
        {   // play head at/past newest: hold newest; CarSync applies tick-age (no double extrapolation)
            Fill(ref t, newest); t.Ok = true; t.Extrapolated = false; return t;
        }

        // find i such that At(i).T <= play <= At(i+1).T
        int lo = 0;
        for (int i = 0; i < _count - 1; i++)
            if (At(i).T <= play && play <= At(i + 1).T) { lo = i; break; }
        var a = At(lo); var b = At(lo + 1);
        var span = b.T - a.T;
        if (span < 1e-6) { Fill(ref t, b); t.Ok = true; return t; }
        var u = (play - a.T) / span;
        HermitePos(a, b, span, u, ref t);
        SlerpRot(a, b, u, ref t);
        t.Vx = Lerp(a.Vx, b.Vx, u); t.Vy = Lerp(a.Vy, b.Vy, u); t.Vz = Lerp(a.Vz, b.Vz, u);
        t.Ax = Lerp(a.Ax, b.Ax, u); t.Ay = Lerp(a.Ay, b.Ay, u); t.Az = Lerp(a.Az, b.Az, u);
        t.Tick = u < 0.5 ? a.Tick : b.Tick;
        t.Ok = true;
        return t;
    }

    static void Fill(ref Target t, in Sample s)
    {
        t.Px = s.Px; t.Py = s.Py; t.Pz = s.Pz;
        t.Vx = s.Vx; t.Vy = s.Vy; t.Vz = s.Vz;
        t.Rx = s.Rx; t.Ry = s.Ry; t.Rz = s.Rz; t.Rw = s.Rw;
        t.Ax = s.Ax; t.Ay = s.Ay; t.Az = s.Az;
        t.Tick = s.Tick;
    }

    /// <summary>Cubic Hermite on each axis; tangents = velocity × span.</summary>
    static void HermitePos(in Sample a, in Sample b, double span, double u, ref Target t)
    {
        var u2 = u * u; var u3 = u2 * u;
        var h00 = 2 * u3 - 3 * u2 + 1;
        var h10 = u3 - 2 * u2 + u;
        var h01 = -2 * u3 + 3 * u2;
        var h11 = u3 - u2;
        t.Px = h00 * a.Px + h10 * (a.Vx * span) + h01 * b.Px + h11 * (b.Vx * span);
        t.Py = h00 * a.Py + h10 * (a.Vy * span) + h01 * b.Py + h11 * (b.Vy * span);
        t.Pz = h00 * a.Pz + h10 * (a.Vz * span) + h01 * b.Pz + h11 * (b.Vz * span);
    }

    static void SlerpRot(in Sample a, in Sample b, double u, ref Target t)
    {
        // nlerp (fast, good enough for host servo target)
        var dot = a.Rx * b.Rx + a.Ry * b.Ry + a.Rz * b.Rz + a.Rw * b.Rw;
        double bx = b.Rx, by = b.Ry, bz = b.Rz, bw = b.Rw;
        if (dot < 0) { bx = -bx; by = -by; bz = -bz; bw = -bw; dot = -dot; }
        t.Rx = a.Rx + u * (bx - a.Rx);
        t.Ry = a.Ry + u * (by - a.Ry);
        t.Rz = a.Rz + u * (bz - a.Rz);
        t.Rw = a.Rw + u * (bw - a.Rw);
        var len = Math.Sqrt(t.Rx * t.Rx + t.Ry * t.Ry + t.Rz * t.Rz + t.Rw * t.Rw);
        if (len > 1e-8) { t.Rx /= len; t.Ry /= len; t.Rz /= len; t.Rw /= len; }
        else { t.Rx = a.Rx; t.Ry = a.Ry; t.Rz = a.Rz; t.Rw = a.Rw; }
    }

    static void RotateYaw(ref Target t, double yawRad)
    {
        var half = yawRad * 0.5;
        var sy = Math.Sin(half); var cy = Math.Cos(half);
        // q_yaw * q
        var rx = cy * t.Rx + sy * t.Rz;
        var ry = cy * t.Ry + sy * t.Rw;
        var rz = cy * t.Rz - sy * t.Rx;
        var rw = cy * t.Rw - sy * t.Ry;
        t.Rx = rx; t.Ry = ry; t.Rz = rz; t.Rw = rw;
    }

    static double Lerp(double a, double b, double u) => a + (b - a) * u;
}

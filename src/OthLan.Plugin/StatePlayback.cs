using System;

namespace OthLan;

/// <summary>
/// 0.2.0 T3.4: stream playback on the sender's time scale (owner of remote/host pose from OTC3).
/// Evolves 0.1.9 PosePlayback: sendTime-based offset, jitter delay, Hermite, capped extrap, epoch snap.
/// </summary>
public sealed class StatePlayback
{
    public sealed class Cfg
    {
        public int Capacity = 64;
        public double MinDelayS = 0.05;
        public double MaxDelayS = 0.25;
        public double DelayEwma = 0.1;
        public double SendHz = 30;
        public double MaxExtrapS = 0.25;
        public double MaxBelowNewestM = 0.5;
        public double OffsetWindowS = 2.0;
    }

    public struct Sample
    {
        public uint Seq;
        public double SendTime;
        public byte Epoch;
        public float Px, Py, Pz;
        public float Qx, Qy, Qz, Qw;
        public float Vx, Vy, Vz;
        public float Wx, Wy, Wz;
        public byte Flags;
        public float SteerDeg;
        public byte WheelCount;
        public CarWire.WheelSample W0, W1, W2, W3, W4, W5;
    }

    public struct PlaybackResult
    {
        public bool Ok, Snap, Stale, Extrapolated;
        public double DelayS;
        public float Px, Py, Pz;
        public float Qx, Qy, Qz, Qw;
        public float Vx, Vy, Vz;
        public float Wx, Wy, Wz;
        public byte Flags;
        public float SteerDeg;
        public byte WheelCount;
        public CarWire.WheelSample W0, W1, W2, W3, W4, W5;
        public byte Epoch;
        public uint Seq;
    }

    readonly Cfg _c;
    Sample[] _ring;
    int _count, _head;
    double _delay, _off = double.NaN;
    readonly double[] _skew = new double[64];
    int _skewN, _skewI;
    byte _epoch;
    bool _pendingSnap;
    public int Pushes, Dupes, Reorders, EpochDrops;

    public StatePlayback(Cfg c = null)
    {
        _c = c ?? new Cfg();
        _ring = new Sample[Math.Max(8, _c.Capacity)];
        _delay = Math.Clamp(_c.MinDelayS, _c.MinDelayS, _c.MaxDelayS);
    }

    public void Reset()
    {
        _count = _head = 0; _off = double.NaN; _skewN = _skewI = 0;
        _delay = Math.Clamp(_c.MinDelayS, _c.MinDelayS, _c.MaxDelayS);
        _epoch = 0; _pendingSnap = false;
        Pushes = Dupes = Reorders = EpochDrops = 0;
    }

    public int Count => _count;
    public double DelayS => _delay;
    public double OffsetS => double.IsNaN(_off) ? 0 : _off;

    public void Push(in Sample s, double arrivalLocal)
    {
        if (_count > 0)
        {
            var newest = At(_count - 1);
            if (s.Epoch != newest.Epoch && s.SendTime >= newest.SendTime - 1e-6)
            {
                // new epoch: drop old samples
                _count = 0; _head = 0; EpochDrops++;
                _pendingSnap = true;
                _epoch = s.Epoch;
            }
            else if (s.Epoch != newest.Epoch)
            {
                // older epoch — ignore
                return;
            }
            // dedupe / reorder by seq (within epoch)
            for (var i = 0; i < _count; i++)
                if (At(i).Seq == s.Seq) { Dupes++; return; }
            if (s.Seq < newest.Seq) Reorders++;
        }
        else _epoch = s.Epoch;

        var skew = arrivalLocal - s.SendTime;
        if (!double.IsNaN(skew) && !double.IsInfinity(skew))
        {
            _skew[_skewI] = skew;
            _skewI = (_skewI + 1) % _skew.Length;
            if (_skewN < _skew.Length) _skewN++;
            // off = min skew over samples with sendTime in last OffsetWindowS (approx: all ring + skew buf)
            var minSkew = skew;
            for (var i = 0; i < _skewN; i++)
                if (_skew[i] < minSkew) minSkew = _skew[i];
            _off = minSkew;
            // jit = p95(skew - off)
            var tmp = new double[_skewN];
            var n = 0;
            for (var i = 0; i < _skewN; i++)
            {
                var d = _skew[i] - _off;
                if (d >= 0) tmp[n++] = d;
            }
            Array.Sort(tmp, 0, n);
            var p95 = n == 0 ? 0 : tmp[Math.Clamp((int)Math.Ceiling(n * 0.95) - 1, 0, n - 1)];
            var want = Math.Clamp(p95 + 0.5 / Math.Max(1, _c.SendHz), _c.MinDelayS, _c.MaxDelayS);
            _delay += _c.DelayEwma * (want - _delay);
            _delay = Math.Clamp(_delay, _c.MinDelayS, _c.MaxDelayS);
        }

        // insert sorted by SendTime (usually append)
        if (_count < _ring.Length)
        {
            _ring[_head] = s;
            _head = (_head + 1) % _ring.Length;
            _count++;
            // bubble back if out of order
            for (var i = _count - 1; i > 0; i--)
            {
                var a = At(i - 1); var b = At(i);
                if (a.SendTime <= b.SendTime) break;
                SetAt(i - 1, b); SetAt(i, a);
            }
        }
        else
        {
            // full: drop oldest, append
            _ring[_head] = s;
            _head = (_head + 1) % _ring.Length;
            for (var i = _count - 1; i > 0; i--)
            {
                var a = At(i - 1); var b = At(i);
                if (a.SendTime <= b.SendTime) break;
                SetAt(i - 1, b); SetAt(i, a);
            }
        }
        Pushes++;
    }

    /// <param name="delayOverride">Host follower: pass 0 to sample the newest «present» without jitter delay.</param>
    public PlaybackResult SampleAt(double nowLocal, double? delayOverride = null)
    {
        var r = new PlaybackResult { DelayS = delayOverride ?? _delay };
        if (_count == 0) return r;
        var off = double.IsNaN(_off) ? 0 : _off;
        var D = delayOverride ?? _delay;
        var play = nowLocal - off - D;
        var newest = At(_count - 1);
        var oldest = At(0);

        if (_pendingSnap)
        {
            Fill(ref r, newest);
            r.Ok = true; r.Snap = true; _pendingSnap = false;
            return r;
        }

        if (play <= oldest.SendTime)
        {
            Fill(ref r, oldest); r.Ok = true; return r;
        }

        if (play >= newest.SendTime)
        {
            var delta = play - newest.SendTime;
            if (delta <= _c.MaxExtrapS)
            {
                Extrap(ref r, newest, delta);
                r.Ok = true; r.Extrapolated = delta > 1e-4;
                return r;
            }
            Fill(ref r, newest); r.Ok = true; r.Stale = true; return r;
        }

        int lo = 0;
        for (var i = 0; i < _count - 1; i++)
            if (At(i).SendTime <= play && play <= At(i + 1).SendTime) { lo = i; break; }
        var a = At(lo); var b = At(lo + 1);
        var span = b.SendTime - a.SendTime;
        if (span < 1e-6) { Fill(ref r, b); r.Ok = true; return r; }
        var u = (play - a.SendTime) / span;
        Hermite(a, b, span, u, ref r);
        Slerp(a, b, u, ref r);
        r.Vx = Lerp(a.Vx, b.Vx, (float)u); r.Vy = Lerp(a.Vy, b.Vy, (float)u); r.Vz = Lerp(a.Vz, b.Vz, (float)u);
        r.Wx = Lerp(a.Wx, b.Wx, (float)u); r.Wy = Lerp(a.Wy, b.Wy, (float)u); r.Wz = Lerp(a.Wz, b.Wz, (float)u);
        r.SteerDeg = Lerp(a.SteerDeg, b.SteerDeg, (float)u);
        r.Flags = u < 0.5f ? a.Flags : b.Flags;
        BlendWheels(a, b, u, (float)(play - a.SendTime), ref r);
        r.Epoch = b.Epoch; r.Seq = u < 0.5 ? a.Seq : b.Seq;
        r.Ok = true;
        return r;
    }

    Sample At(int i)
    {
        var idx = (_head - _count + i + _ring.Length * 4) % _ring.Length;
        return _ring[idx];
    }

    void SetAt(int i, in Sample s)
    {
        var idx = (_head - _count + i + _ring.Length * 4) % _ring.Length;
        _ring[idx] = s;
    }

    static void Fill(ref PlaybackResult r, in Sample s)
    {
        r.Px = s.Px; r.Py = s.Py; r.Pz = s.Pz;
        r.Qx = s.Qx; r.Qy = s.Qy; r.Qz = s.Qz; r.Qw = s.Qw;
        r.Vx = s.Vx; r.Vy = s.Vy; r.Vz = s.Vz;
        r.Wx = s.Wx; r.Wy = s.Wy; r.Wz = s.Wz;
        r.Flags = s.Flags; r.SteerDeg = s.SteerDeg;
        r.WheelCount = s.WheelCount;
        r.W0 = s.W0; r.W1 = s.W1; r.W2 = s.W2; r.W3 = s.W3; r.W4 = s.W4; r.W5 = s.W5;
        r.Epoch = s.Epoch; r.Seq = s.Seq;
    }

    void Extrap(ref PlaybackResult r, in Sample n, double dt)
    {
        Fill(ref r, n);
        r.Px = n.Px + n.Vx * (float)dt;
        r.Pz = n.Pz + n.Vz * (float)dt;
        var airborne = (n.Flags & CarWire.FlagAirborne) != 0;
        if (airborne)
        {
            var y = n.Py + n.Vy * (float)dt;
            var floor = n.Py - (float)_c.MaxBelowNewestM;
            r.Py = y < floor ? floor : y;
        }
        // yaw only from Wy
        var yaw = n.Wy * (float)dt;
        if (Math.Abs(yaw) > 1e-6f)
        {
            var half = yaw * 0.5f;
            var sy = MathF.Sin(half); var cy = MathF.Cos(half);
            var qx = cy * r.Qx + sy * r.Qz;
            var qy = cy * r.Qy + sy * r.Qw;
            var qz = cy * r.Qz - sy * r.Qx;
            var qw = cy * r.Qw - sy * r.Qy;
            r.Qx = qx; r.Qy = qy; r.Qz = qz; r.Qw = qw;
        }
        // integrate wheel roll from spin
        for (var i = 0; i < r.WheelCount; i++)
        {
            var w = Wheel(r, i);
            w.RollDeg = NormDeg(w.RollDeg + w.SpinRadS * (float)dt * (180f / MathF.PI));
            SetWheel(ref r, i, w);
        }
    }

    static void Hermite(in Sample a, in Sample b, double span, double u, ref PlaybackResult r)
    {
        var u2 = u * u; var u3 = u2 * u;
        var h00 = 2 * u3 - 3 * u2 + 1;
        var h10 = u3 - 2 * u2 + u;
        var h01 = -2 * u3 + 3 * u2;
        var h11 = u3 - u2;
        r.Px = (float)(h00 * a.Px + h10 * (a.Vx * span) + h01 * b.Px + h11 * (b.Vx * span));
        r.Py = (float)(h00 * a.Py + h10 * (a.Vy * span) + h01 * b.Py + h11 * (b.Vy * span));
        r.Pz = (float)(h00 * a.Pz + h10 * (a.Vz * span) + h01 * b.Pz + h11 * (b.Vz * span));
    }

    static void Slerp(in Sample a, in Sample b, double u, ref PlaybackResult r)
    {
        var dot = a.Qx * b.Qx + a.Qy * b.Qy + a.Qz * b.Qz + a.Qw * b.Qw;
        float bx = b.Qx, by = b.Qy, bz = b.Qz, bw = b.Qw;
        if (dot < 0) { bx = -bx; by = -by; bz = -bz; bw = -bw; }
        r.Qx = a.Qx + (float)u * (bx - a.Qx);
        r.Qy = a.Qy + (float)u * (by - a.Qy);
        r.Qz = a.Qz + (float)u * (bz - a.Qz);
        r.Qw = a.Qw + (float)u * (bw - a.Qw);
        var len = MathF.Sqrt(r.Qx * r.Qx + r.Qy * r.Qy + r.Qz * r.Qz + r.Qw * r.Qw);
        if (len > 1e-8f) { r.Qx /= len; r.Qy /= len; r.Qz /= len; r.Qw /= len; }
        else { r.Qx = a.Qx; r.Qy = a.Qy; r.Qz = a.Qz; r.Qw = a.Qw; }
    }

    static void BlendWheels(in Sample a, in Sample b, double u, float dtFromA, ref PlaybackResult r)
    {
        r.WheelCount = a.WheelCount >= b.WheelCount ? a.WheelCount : b.WheelCount;
        for (var i = 0; i < r.WheelCount; i++)
        {
            var wa = i < a.WheelCount ? Wheel(a, i) : default;
            var wb = i < b.WheelCount ? Wheel(b, i) : wa;
            var spin = Lerp(wa.SpinRadS, wb.SpinRadS, (float)u);
            var susp = Lerp(wa.SuspM, wb.SuspM, (float)u);
            // integrate spin from A, soft-pull phase toward B (≤ 90°/s)
            var integrated = NormDeg(wa.RollDeg + spin * dtFromA * (180f / MathF.PI));
            var target = wb.RollDeg;
            var err = DeltaDeg(integrated, target);
            var maxPull = 90f * dtFromA;
            if (err > maxPull) err = maxPull;
            if (err < -maxPull) err = -maxPull;
            var roll = NormDeg(integrated + err);
            SetWheel(ref r, i, new CarWire.WheelSample { RollDeg = roll, SpinRadS = spin, SuspM = susp });
        }
    }

    static CarWire.WheelSample Wheel(in Sample s, int i) => i switch
    {
        0 => s.W0, 1 => s.W1, 2 => s.W2, 3 => s.W3, 4 => s.W4, 5 => s.W5, _ => default
    };
    static CarWire.WheelSample Wheel(in PlaybackResult r, int i) => i switch
    {
        0 => r.W0, 1 => r.W1, 2 => r.W2, 3 => r.W3, 4 => r.W4, 5 => r.W5, _ => default
    };
    static void SetWheel(ref PlaybackResult r, int i, CarWire.WheelSample w)
    {
        switch (i)
        {
            case 0: r.W0 = w; break; case 1: r.W1 = w; break; case 2: r.W2 = w; break;
            case 3: r.W3 = w; break; case 4: r.W4 = w; break; case 5: r.W5 = w; break;
        }
    }

    static float Lerp(float a, float b, float u) => a + (b - a) * u;
    static float NormDeg(float d) { d %= 360f; if (d < 0) d += 360f; return d; }
    static float DeltaDeg(float from, float to)
    {
        var d = to - from;
        while (d > 180) d -= 360;
        while (d < -180) d += 360;
        return d;
    }
}

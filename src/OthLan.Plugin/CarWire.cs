using System;

namespace OthLan;

/// <summary>
/// 0.2.0 T3.1: OTC3 car-state wire format (owner of encode/decode). Unity-free for LogicTests.
/// </summary>
public static class CarWire
{
    public const uint Magic = 0x3343544F; // "OTC3" LE
    public const byte Version = 1;
    public const int MaxStates = 3;
    public const int MaxWheels = 6;
    public const int MaxPacketBytes = 1200;
    public const int HeaderSize = 8; // magic + ver + slot + epoch + count

    public const byte FlagAirborne = 1 << 0;
    public const byte FlagHeadlights = 1 << 1;
    public const byte FlagBrake = 1 << 2;
    public const byte FlagReverse = 1 << 3;
    public const byte FlagHandbrake = 1 << 4;
    public const byte FlagEngineOn = 1 << 5;

    public const float SteerScale = 100f;       // i16 × 0.01 deg
    public const float SpinScale = 20f;         // i16 × 0.05 rad/s  (1/0.05)
    public const float SuspScale = 1000f;       // i16 × 0.001 m
    public const float SpinClipRadS = 1600f;

    public struct WheelSample
    {
        public float RollDeg;   // 0..360
        public float SpinRadS;
        public float SuspM;
    }

    public struct CarStateSample
    {
        public uint Seq;
        public double SendTime;
        public float Px, Py, Pz;
        public float Qx, Qy, Qz, Qw;
        public float Vx, Vy, Vz;
        public float Wx, Wy, Wz;
        public byte Flags;
        public float SteerDeg;
        public byte WheelCount;
        public WheelSample W0, W1, W2, W3, W4, W5;

        public WheelSample WheelAt(int i) => i switch
        {
            0 => W0, 1 => W1, 2 => W2, 3 => W3, 4 => W4, 5 => W5, _ => default
        };

        public void SetWheel(int i, in WheelSample w)
        {
            switch (i)
            {
                case 0: W0 = w; break; case 1: W1 = w; break; case 2: W2 = w; break;
                case 3: W3 = w; break; case 4: W4 = w; break; case 5: W5 = w; break;
            }
        }
    }

    public struct CarStatePacket
    {
        public byte Slot;
        public byte Epoch;
        public byte Count; // 1..3, newest first
        public CarStateSample S0, S1, S2;

        public CarStateSample StateAt(int i) => i switch { 0 => S0, 1 => S1, 2 => S2, _ => default };
        public void SetState(int i, in CarStateSample s)
        {
            switch (i) { case 0: S0 = s; break; case 1: S1 = s; break; case 2: S2 = s; break; }
        }
    }

    public static int StateBytes(int wheelCount)
    {
        if (wheelCount < 0 || wheelCount > MaxWheels) return -1;
        // seq4 + sendTime8 + pos12 + rot16 + vel12 + ang12 + flags1 + steer2 + wheelCount1 + 6*wheels
        return 68 + 6 * wheelCount;
    }

    public static int PacketBytes(in CarStatePacket p)
    {
        if (p.Count < 1 || p.Count > MaxStates) return -1;
        var n = HeaderSize;
        for (var i = 0; i < p.Count; i++)
        {
            var wc = p.StateAt(i).WheelCount;
            var sb = StateBytes(wc);
            if (sb < 0) return -1;
            n += sb;
        }
        return n;
    }

    /// <summary>Encode into <paramref name="dst"/>. Returns bytes written, or -1 on failure.</summary>
    public static int Encode(in CarStatePacket p, byte[] dst)
    {
        if (dst == null) return -1;
        if (p.Count < 1 || p.Count > MaxStates) return -1;
        var need = PacketBytes(p);
        if (need < 0 || need > MaxPacketBytes || need > dst.Length) return -1;
        for (var i = 0; i < p.Count; i++)
            if (!StateFinite(p.StateAt(i))) return -1;

        var o = 0;
        WriteU32(dst, ref o, Magic);
        dst[o++] = Version;
        dst[o++] = p.Slot;
        dst[o++] = p.Epoch;
        dst[o++] = p.Count;
        for (var i = 0; i < p.Count; i++)
            WriteState(dst, ref o, p.StateAt(i));
        return o;
    }

    public static bool TryDecode(ReadOnlySpan<byte> src, out CarStatePacket p)
    {
        p = default;
        if (src.Length < HeaderSize) return false;
        var o = 0;
        if (ReadU32(src, ref o) != Magic) return false;
        var ver = src[o++];
        if (ver != Version) return false;
        p.Slot = src[o++];
        p.Epoch = src[o++];
        p.Count = src[o++];
        if (p.Count < 1 || p.Count > MaxStates) return false;

        for (var i = 0; i < p.Count; i++)
        {
            if (!TryReadState(src, ref o, out var s)) return false;
            p.SetState(i, s);
        }
        if (o != src.Length) return false; // trailing garbage
        return true;
    }

    public static bool TryDecode(byte[] src, out CarStatePacket p)
        => TryDecode(src == null ? ReadOnlySpan<byte>.Empty : src.AsSpan(), out p);

    static bool StateFinite(in CarStateSample s)
    {
        if (!Fin(s.SendTime) || !Fin(s.Px) || !Fin(s.Py) || !Fin(s.Pz)) return false;
        if (!Fin(s.Qx) || !Fin(s.Qy) || !Fin(s.Qz) || !Fin(s.Qw)) return false;
        if (!Fin(s.Vx) || !Fin(s.Vy) || !Fin(s.Vz) || !Fin(s.Wx) || !Fin(s.Wy) || !Fin(s.Wz)) return false;
        if (!Fin(s.SteerDeg) || s.WheelCount > MaxWheels) return false;
        for (var i = 0; i < s.WheelCount; i++)
        {
            var w = s.WheelAt(i);
            if (!Fin(w.RollDeg) || !Fin(w.SpinRadS) || !Fin(w.SuspM)) return false;
        }
        return true;
    }

    static bool Fin(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    static bool Fin(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

    static void WriteState(byte[] dst, ref int o, in CarStateSample s)
    {
        WriteU32(dst, ref o, s.Seq);
        WriteF64(dst, ref o, s.SendTime);
        WriteF32(dst, ref o, s.Px); WriteF32(dst, ref o, s.Py); WriteF32(dst, ref o, s.Pz);
        // normalize quaternion for wire
        var qlen = MathF.Sqrt(s.Qx * s.Qx + s.Qy * s.Qy + s.Qz * s.Qz + s.Qw * s.Qw);
        float qx = s.Qx, qy = s.Qy, qz = s.Qz, qw = s.Qw;
        if (qlen > 1e-8f) { qx /= qlen; qy /= qlen; qz /= qlen; qw /= qlen; }
        else { qx = qy = qz = 0; qw = 1; }
        WriteF32(dst, ref o, qx); WriteF32(dst, ref o, qy); WriteF32(dst, ref o, qz); WriteF32(dst, ref o, qw);
        WriteF32(dst, ref o, s.Vx); WriteF32(dst, ref o, s.Vy); WriteF32(dst, ref o, s.Vz);
        WriteF32(dst, ref o, s.Wx); WriteF32(dst, ref o, s.Wy); WriteF32(dst, ref o, s.Wz);
        dst[o++] = s.Flags;
        WriteI16(dst, ref o, QuantSteer(s.SteerDeg));
        var wc = s.WheelCount > MaxWheels ? (byte)MaxWheels : s.WheelCount;
        dst[o++] = wc;
        for (var i = 0; i < wc; i++)
        {
            var w = s.WheelAt(i);
            WriteU16(dst, ref o, QuantRoll(w.RollDeg));
            WriteI16(dst, ref o, QuantSpin(w.SpinRadS));
            WriteI16(dst, ref o, QuantSusp(w.SuspM));
        }
    }

    static bool TryReadState(ReadOnlySpan<byte> src, ref int o, out CarStateSample s)
    {
        s = default;
        if (src.Length - o < 68) return false; // min with 0 wheels
        s.Seq = ReadU32(src, ref o);
        s.SendTime = ReadF64(src, ref o);
        s.Px = ReadF32(src, ref o); s.Py = ReadF32(src, ref o); s.Pz = ReadF32(src, ref o);
        s.Qx = ReadF32(src, ref o); s.Qy = ReadF32(src, ref o); s.Qz = ReadF32(src, ref o); s.Qw = ReadF32(src, ref o);
        s.Vx = ReadF32(src, ref o); s.Vy = ReadF32(src, ref o); s.Vz = ReadF32(src, ref o);
        s.Wx = ReadF32(src, ref o); s.Wy = ReadF32(src, ref o); s.Wz = ReadF32(src, ref o);
        s.Flags = src[o++];
        s.SteerDeg = ReadI16(src, ref o) / SteerScale;
        s.WheelCount = src[o++];
        if (s.WheelCount > MaxWheels) return false;
        if (src.Length - o < 6 * s.WheelCount) return false;
        for (var i = 0; i < s.WheelCount; i++)
        {
            var w = new WheelSample
            {
                RollDeg = DequantRoll(ReadU16(src, ref o)),
                SpinRadS = ReadI16(src, ref o) / SpinScale,
                SuspM = ReadI16(src, ref o) / SuspScale,
            };
            s.SetWheel(i, w);
        }
        return StateFinite(s);
    }

    public static short QuantSteer(float deg)
    {
        var v = Math.Round(deg * SteerScale);
        if (v > short.MaxValue) return short.MaxValue;
        if (v < short.MinValue) return short.MinValue;
        return (short)v;
    }

    public static short QuantSpin(float radS)
    {
        var c = Math.Clamp(radS, -SpinClipRadS, SpinClipRadS);
        var v = Math.Round(c * SpinScale);
        if (v > short.MaxValue) return short.MaxValue;
        if (v < short.MinValue) return short.MinValue;
        return (short)v;
    }

    public static short QuantSusp(float m)
    {
        var v = Math.Round(m * SuspScale);
        if (v > short.MaxValue) return short.MaxValue;
        if (v < short.MinValue) return short.MinValue;
        return (short)v;
    }

    public static ushort QuantRoll(float deg)
    {
        var d = deg % 360f;
        if (d < 0) d += 360f;
        var v = Math.Round(d / 360.0 * 65535.0);
        if (v < 0) return 0;
        if (v > 65535) return 65535;
        return (ushort)v;
    }

    public static float DequantRoll(ushort u) => (float)(u / 65535.0 * 360.0);

    static void WriteU32(byte[] b, ref int o, uint v) { BitConverter.TryWriteBytes(b.AsSpan(o), v); o += 4; }
    static void WriteU16(byte[] b, ref int o, ushort v) { BitConverter.TryWriteBytes(b.AsSpan(o), v); o += 2; }
    static void WriteI16(byte[] b, ref int o, short v) { BitConverter.TryWriteBytes(b.AsSpan(o), v); o += 2; }
    static void WriteF32(byte[] b, ref int o, float v) { BitConverter.TryWriteBytes(b.AsSpan(o), v); o += 4; }
    static void WriteF64(byte[] b, ref int o, double v) { BitConverter.TryWriteBytes(b.AsSpan(o), v); o += 8; }

    static uint ReadU32(ReadOnlySpan<byte> b, ref int o) { var v = BitConverter.ToUInt32(b.Slice(o, 4)); o += 4; return v; }
    static ushort ReadU16(ReadOnlySpan<byte> b, ref int o) { var v = BitConverter.ToUInt16(b.Slice(o, 2)); o += 2; return v; }
    static short ReadI16(ReadOnlySpan<byte> b, ref int o) { var v = BitConverter.ToInt16(b.Slice(o, 2)); o += 2; return v; }
    static float ReadF32(ReadOnlySpan<byte> b, ref int o) { var v = BitConverter.ToSingle(b.Slice(o, 4)); o += 4; return v; }
    static double ReadF64(ReadOnlySpan<byte> b, ref int o) { var v = BitConverter.ToDouble(b.Slice(o, 8)); o += 8; return v; }
}

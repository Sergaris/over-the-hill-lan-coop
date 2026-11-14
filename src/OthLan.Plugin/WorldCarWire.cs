using System;

namespace OthLan;

/// <summary>
/// Host→client stream for world / evacuate cars (OTW1). Separate magic from OTC3 so player slot bytes stay intact.
/// Pose-only (no wheels/lights). Unity-free for LogicTests.
/// </summary>
public static class WorldCarWire
{
    public const uint Magic = 0x3157544F; // "OTW1" LE
    public const byte Version = 1;
    public const int MaxStates = 3;
    public const int MaxPacketBytes = 512;
    /// <summary>magic4 + ver1 + epoch1 + count1 + networkId4</summary>
    public const int HeaderSize = 11;
    /// <summary>seq4 + sendTime8 + pos12 + rot16 + vel12 + ang12</summary>
    public const int StateBytes = 64;

    public struct PoseSample
    {
        public uint Seq;
        public double SendTime;
        public float Px, Py, Pz;
        public float Qx, Qy, Qz, Qw;
        public float Vx, Vy, Vz;
        public float Wx, Wy, Wz;
    }

    public struct Packet
    {
        public uint NetworkId;
        public byte Epoch;
        public byte Count;
        public PoseSample S0, S1, S2;

        public PoseSample StateAt(int i) => i switch { 0 => S0, 1 => S1, 2 => S2, _ => default };
        public void SetState(int i, in PoseSample s)
        {
            switch (i) { case 0: S0 = s; break; case 1: S1 = s; break; case 2: S2 = s; break; }
        }
    }

    public static int PacketBytes(byte count)
    {
        if (count < 1 || count > MaxStates) return -1;
        return HeaderSize + count * StateBytes;
    }

    public static int Encode(in Packet p, byte[] dst)
    {
        if (dst == null) return -1;
        if (p.Count < 1 || p.Count > MaxStates) return -1;
        var need = PacketBytes(p.Count);
        if (need < 0 || need > MaxPacketBytes || need > dst.Length) return -1;
        for (var i = 0; i < p.Count; i++)
            if (!Finite(p.StateAt(i))) return -1;

        var o = 0;
        WriteU32(dst, ref o, Magic);
        dst[o++] = Version;
        dst[o++] = p.Epoch;
        dst[o++] = p.Count;
        WriteU32(dst, ref o, p.NetworkId);
        for (var i = 0; i < p.Count; i++)
            WriteState(dst, ref o, p.StateAt(i));
        return o;
    }

    public static bool TryDecode(ReadOnlySpan<byte> src, out Packet p)
    {
        p = default;
        if (src.Length < HeaderSize) return false;
        var o = 0;
        if (ReadU32(src, ref o) != Magic) return false;
        if (src[o++] != Version) return false;
        p.Epoch = src[o++];
        p.Count = src[o++];
        if (p.Count < 1 || p.Count > MaxStates) return false;
        p.NetworkId = ReadU32(src, ref o);
        if (src.Length != HeaderSize + p.Count * StateBytes) return false;
        for (var i = 0; i < p.Count; i++)
        {
            if (!TryReadState(src, ref o, out var s)) return false;
            p.SetState(i, s);
        }
        return o == src.Length;
    }

    public static bool TryDecode(byte[] src, out Packet p)
        => TryDecode(src == null ? ReadOnlySpan<byte>.Empty : src.AsSpan(), out p);

    static bool Finite(in PoseSample s)
    {
        if (!Fin(s.SendTime) || !Fin(s.Px) || !Fin(s.Py) || !Fin(s.Pz)) return false;
        if (!Fin(s.Qx) || !Fin(s.Qy) || !Fin(s.Qz) || !Fin(s.Qw)) return false;
        return Fin(s.Vx) && Fin(s.Vy) && Fin(s.Vz) && Fin(s.Wx) && Fin(s.Wy) && Fin(s.Wz);
    }

    static bool Fin(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    static bool Fin(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

    static void WriteState(byte[] dst, ref int o, in PoseSample s)
    {
        WriteU32(dst, ref o, s.Seq);
        WriteF64(dst, ref o, s.SendTime);
        WriteF32(dst, ref o, s.Px); WriteF32(dst, ref o, s.Py); WriteF32(dst, ref o, s.Pz);
        var qlen = MathF.Sqrt(s.Qx * s.Qx + s.Qy * s.Qy + s.Qz * s.Qz + s.Qw * s.Qw);
        float qx = s.Qx, qy = s.Qy, qz = s.Qz, qw = s.Qw;
        if (qlen > 1e-8f) { qx /= qlen; qy /= qlen; qz /= qlen; qw /= qlen; }
        else { qx = qy = qz = 0; qw = 1; }
        WriteF32(dst, ref o, qx); WriteF32(dst, ref o, qy); WriteF32(dst, ref o, qz); WriteF32(dst, ref o, qw);
        WriteF32(dst, ref o, s.Vx); WriteF32(dst, ref o, s.Vy); WriteF32(dst, ref o, s.Vz);
        WriteF32(dst, ref o, s.Wx); WriteF32(dst, ref o, s.Wy); WriteF32(dst, ref o, s.Wz);
    }

    static bool TryReadState(ReadOnlySpan<byte> src, ref int o, out PoseSample s)
    {
        s = default;
        if (src.Length - o < StateBytes) return false;
        s.Seq = ReadU32(src, ref o);
        s.SendTime = ReadF64(src, ref o);
        s.Px = ReadF32(src, ref o); s.Py = ReadF32(src, ref o); s.Pz = ReadF32(src, ref o);
        s.Qx = ReadF32(src, ref o); s.Qy = ReadF32(src, ref o); s.Qz = ReadF32(src, ref o); s.Qw = ReadF32(src, ref o);
        s.Vx = ReadF32(src, ref o); s.Vy = ReadF32(src, ref o); s.Vz = ReadF32(src, ref o);
        s.Wx = ReadF32(src, ref o); s.Wy = ReadF32(src, ref o); s.Wz = ReadF32(src, ref o);
        return Finite(s);
    }

    static void WriteU32(byte[] b, ref int o, uint v) { BitConverter.TryWriteBytes(b.AsSpan(o), v); o += 4; }
    static void WriteF32(byte[] b, ref int o, float v) { BitConverter.TryWriteBytes(b.AsSpan(o), v); o += 4; }
    static void WriteF64(byte[] b, ref int o, double v) { BitConverter.TryWriteBytes(b.AsSpan(o), v); o += 8; }
    static uint ReadU32(ReadOnlySpan<byte> b, ref int o) { var v = BitConverter.ToUInt32(b.Slice(o, 4)); o += 4; return v; }
    static float ReadF32(ReadOnlySpan<byte> b, ref int o) { var v = BitConverter.ToSingle(b.Slice(o, 4)); o += 4; return v; }
    static double ReadF64(ReadOnlySpan<byte> b, ref int o) { var v = BitConverter.ToDouble(b.Slice(o, 8)); o += 8; return v; }
}

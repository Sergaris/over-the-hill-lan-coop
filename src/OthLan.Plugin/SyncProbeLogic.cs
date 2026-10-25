using System;
using System.Collections.Generic;
using System.Globalization;

namespace OthLan;

/// <summary>
/// 0.2.0 T4.4: wire format + recent-sample ring for cross-PC pose sync lines.
/// Owner of <c>[Sync] slot=… seq=… pos=(x,y,z)</c>; plugin notes raw OTC3 samples (not Hermite SampleAt).
/// </summary>
public static class SyncProbeLogic
{
    public const string Tag = "[Sync]";
    /// <summary>Headroom for many cars between dumps (probed samples only).</summary>
    public const int DefaultCapacity = 512;
    /// <summary>Only every Stride-th sender seq is probed (~2 lines/s per car at 30 Hz). The seq is the sender's,
    /// so every PC probes the same samples and they still pair across PCs; logging all of them was ~80 lines/s.</summary>
    public const uint Stride = 15;

    public static bool Probed(uint seq) => seq != 0 && seq % Stride == 0;

    public readonly struct Sample
    {
        public readonly int Slot;
        public readonly uint Seq;
        public readonly float X, Y, Z;
        public Sample(int slot, uint seq, float x, float y, float z)
        { Slot = slot; Seq = seq; X = x; Y = y; Z = z; }
    }

    /// <summary>Ring of raw (slot,seq,pos) samples. Newest overwrites oldest. Pure — no I/O.</summary>
    public sealed class Ring
    {
        readonly Sample[] _buf;
        int _n, _i;
        public Ring(int capacity = DefaultCapacity)
        {
            if (capacity < 2) capacity = 2;
            _buf = new Sample[capacity];
        }
        public int Count => _n;
        public void Clear() { _n = _i = 0; }
        public void Note(int slot, uint seq, float x, float y, float z)
        {
            if (slot < 0 || seq == 0) return;
            _buf[_i] = new Sample(slot, seq, x, y, z);
            _i = (_i + 1) % _buf.Length;
            if (_n < _buf.Length) _n++;
        }
        /// <summary>Oldest→newest copy for dumping as Sync lines.</summary>
        public List<Sample> Snapshot()
        {
            var list = new List<Sample>(_n);
            if (_n == 0) return list;
            var start = _n < _buf.Length ? 0 : _i;
            for (var k = 0; k < _n; k++) list.Add(_buf[(start + k) % _buf.Length]);
            return list;
        }
        /// <summary>Snapshot, then empty: each sample is dumped once.</summary>
        public List<Sample> Drain()
        {
            var list = Snapshot();
            Clear();
            return list;
        }
    }

    public static string Format(int slot, uint seq, float x, float y, float z) =>
        string.Format(CultureInfo.InvariantCulture, "{0} slot={1} seq={2} pos=({3:0.###},{4:0.###},{5:0.###})",
            Tag, slot, seq, x, y, z);

    public static string Format(in Sample s) => Format(s.Slot, s.Seq, s.X, s.Y, s.Z);

    /// <summary>Parse a log line that contains a Sync probe. Returns false if not a Sync line.</summary>
    public static bool TryParse(string line, out int slot, out uint seq, out float x, out float y, out float z)
    {
        slot = 0; seq = 0; x = y = z = 0;
        if (string.IsNullOrEmpty(line)) return false;
        var i = line.IndexOf(Tag, StringComparison.Ordinal);
        if (i < 0) return false;
        var s = line.AsSpan(i + Tag.Length);
        if (!TryKeyInt(s, "slot=", out slot)) return false;
        if (!TryKeyUInt(s, "seq=", out seq)) return false;
        var p = s.IndexOf("pos=(");
        if (p < 0) return false;
        p += 5;
        var end = s.Slice(p).IndexOf(')');
        if (end <= 0) return false;
        var body = s.Slice(p, end);
        var c1 = body.IndexOf(',');
        if (c1 <= 0) return false;
        var c2 = body.Slice(c1 + 1).IndexOf(',');
        if (c2 <= 0) return false;
        if (!float.TryParse(body.Slice(0, c1), NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return false;
        if (!float.TryParse(body.Slice(c1 + 1, c2), NumberStyles.Float, CultureInfo.InvariantCulture, out y)) return false;
        if (!float.TryParse(body.Slice(c1 + 1 + c2 + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return false;
        return true;
    }

    /// <summary>Euclidean distance between two Sync poses (meters).</summary>
    public static double DiffMeters(float ax, float ay, float az, float bx, float by, float bz)
    {
        var dx = (double)ax - bx; var dy = (double)ay - by; var dz = (double)az - bz;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    static bool TryKeyInt(ReadOnlySpan<char> s, string key, out int v)
    {
        v = 0;
        var i = s.IndexOf(key);
        if (i < 0) return false;
        i += key.Length;
        var j = i;
        while (j < s.Length && (char.IsDigit(s[j]) || s[j] == '-')) j++;
        return int.TryParse(s.Slice(i, j - i), NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
    }

    static bool TryKeyUInt(ReadOnlySpan<char> s, string key, out uint v)
    {
        v = 0;
        var i = s.IndexOf(key);
        if (i < 0) return false;
        i += key.Length;
        var j = i;
        while (j < s.Length && char.IsDigit(s[j])) j++;
        return uint.TryParse(s.Slice(i, j - i), NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
    }
}

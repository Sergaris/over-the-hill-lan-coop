using System;
using System.Collections.Generic;
using System.Text;

namespace OthLan;

/// <summary>
/// 0.2.0 T6.4: roster wire (OTPL), diag request (OTDX), resync request (OTRS), and the row status rule.
/// Unity drawing lives in <see cref="SessionPanel"/>; this type is the only owner of the packet layout.
/// </summary>
public static class CoopSessionLogic
{
    public const uint ListMagic = 0x4C50544F;   // "OTPL"
    public const uint DiagMagic = 0x5844544F;   // "OTDX"
    public const uint ResyncMagic = 0x5352544F; // "OTRS"
    public const int MaxRows = 8;
    public const int MaxNameUtf8 = 24;
    public const byte StatusOk = 0, StatusShared = 1, StatusNoLink = 2;

    public struct Row
    {
        public byte Slot;
        public string Name;
        public int PingMs;     // -1 = no sample
        public byte LossPct;
        public byte Status;
        public byte NoLinkS;
    }

    /// <summary>No link wins over shared mode: the row cannot be "ok" while the echo is dead.</summary>
    public static byte StatusOf(bool noLink, bool shared) => noLink ? StatusNoLink : shared ? StatusShared : StatusOk;

    public static bool SharedFromFlags(int flags)
        => (flags & (CarSyncLogic.FlagSuspendContact | CarSyncLogic.FlagSuspendWinch)) != 0;

    public static string ClipName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var chars = new StringBuilder(name.Length);
        foreach (var c in name.Trim()) if (!char.IsControl(c)) chars.Append(c);
        var utf = Encoding.UTF8.GetBytes(chars.ToString());
        if (utf.Length <= MaxNameUtf8) return chars.ToString();
        var n = MaxNameUtf8;
        while (n > 0 && (utf[n] & 0xC0) == 0x80) n--;
        return Encoding.UTF8.GetString(utf, 0, n);
    }

    public static byte[] EncodeList(IReadOnlyList<Row> rows)
    {
        var n = rows == null ? 0 : Math.Min(rows.Count, MaxRows);
        var names = new byte[n][];
        var size = 5;
        for (var i = 0; i < n; i++) { names[i] = NameBytes(rows[i].Name); size += 7 + names[i].Length; }
        var b = new byte[size];
        BitConverter.TryWriteBytes(b.AsSpan(0), ListMagic);
        b[4] = (byte)n;
        var o = 5;
        for (var i = 0; i < n; i++) o = WriteRow(b, o, rows[i], names[i]);
        return b;
    }

    public static bool TryDecodeList(byte[] b, List<Row> into)
    {
        into?.Clear();
        if (b == null || into == null || b.Length < 5 || BitConverter.ToUInt32(b, 0) != ListMagic) return false;
        var n = b[4];
        if (n > MaxRows) return false;
        var o = 5;
        var tmp = new List<Row>(n);
        for (var i = 0; i < n; i++)
        {
            if (!ReadRow(b, ref o, out var row)) return false;
            tmp.Add(row);
        }
        if (o != b.Length) return false;
        into.AddRange(tmp);
        return true;
    }

    public static byte[] EncodeDiag()
    {
        var b = new byte[4];
        BitConverter.TryWriteBytes(b.AsSpan(0), DiagMagic);
        return b;
    }

    public static bool IsDiag(byte[] b) => b != null && b.Length == 4 && BitConverter.ToUInt32(b, 0) == DiagMagic;

    public static byte[] EncodeResync(byte slot)
    {
        var b = new byte[5];
        BitConverter.TryWriteBytes(b.AsSpan(0), ResyncMagic);
        b[4] = slot;
        return b;
    }

    public static bool TryDecodeResync(byte[] b, out byte slot)
    {
        slot = 0;
        if (b == null || b.Length != 5 || BitConverter.ToUInt32(b, 0) != ResyncMagic) return false;
        slot = b[4];
        return true;
    }

    public static string FormatRow(Row r)
    {
        var name = string.IsNullOrEmpty(r.Name) ? Loc.F("session.playerN", r.Slot) : r.Name;
        var ping = r.PingMs < 0 ? Loc.T("session.pingNone") : Loc.F("unit.ms", r.PingMs);
        var loss = Loc.F("session.loss", r.LossPct);
        var st = r.Status == StatusNoLink ? Loc.F("session.statusNoLink", Math.Max(1, (int)r.NoLinkS))
            : r.Status == StatusShared ? Loc.T("session.statusShared") : Loc.T("session.statusOk");
        return Loc.F("session.row", name, ping, loss, st);
    }

    public static string FormatBlock(IReadOnlyList<Row> rows)
    {
        if (rows == null || rows.Count == 0) return "";
        var sb = new StringBuilder();
        for (var i = 0; i < rows.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(FormatRow(rows[i]));
        }
        return sb.ToString();
    }

    static byte[] NameBytes(string name)
    {
        var s = ClipName(name);
        return Encoding.UTF8.GetBytes(s ?? "");
    }

    static int WriteRow(byte[] b, int o, Row r, byte[] name)
    {
        b[o++] = r.Slot;
        var ping = r.PingMs < 0 ? (ushort)0xFFFF : (ushort)Math.Clamp(r.PingMs, 0, 65534);
        BitConverter.TryWriteBytes(b.AsSpan(o), ping);
        o += 2;
        b[o++] = (byte)Math.Clamp((int)r.LossPct, 0, 100);
        b[o++] = r.Status > StatusNoLink ? StatusOk : r.Status;
        b[o++] = r.NoLinkS;
        b[o++] = (byte)name.Length;
        Array.Copy(name, 0, b, o, name.Length);
        return o + name.Length;
    }

    static bool ReadRow(byte[] b, ref int o, out Row row)
    {
        row = default;
        if (o + 7 > b.Length) return false;
        row.Slot = b[o++];
        var ping = BitConverter.ToUInt16(b, o);
        o += 2;
        row.PingMs = ping == 0xFFFF ? -1 : ping;
        row.LossPct = b[o++];
        row.Status = b[o++];
        row.NoLinkS = b[o++];
        var len = b[o++];
        if (row.Status > StatusNoLink || row.LossPct > 100 || len > MaxNameUtf8 || o + len > b.Length) return false;
        row.Name = Encoding.UTF8.GetString(b, o, len);
        o += len;
        return true;
    }
}

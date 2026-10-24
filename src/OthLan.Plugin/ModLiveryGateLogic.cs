using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OthLan;

/// <summary>
/// Pure wire + diff for host-required Modio livery mods vs what the joining client already has.
/// Owner of "liverymods=…" format and missing-mod set. I/O and IL2CPP live in ModLiveryBridge / HostInfo / Overlay.
/// </summary>
public static class ModLiveryGateLogic
{
    public readonly struct ModRef : IEquatable<ModRef>
    {
        public readonly long Id;
        public readonly int SubId;
        public ModRef(long id, int subId) { Id = id; SubId = subId; }
        public bool Equals(ModRef o) => Id == o.Id && SubId == o.SubId;
        public override bool Equals(object obj) => obj is ModRef o && Equals(o);
        public override int GetHashCode() => HashCode.Combine(Id, SubId);
        public override string ToString() => SubId == 0 ? Id.ToString(CultureInfo.InvariantCulture)
            : Id.ToString(CultureInfo.InvariantCulture) + ":" + SubId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Wire value for HostInfo line <c>liverymods=</c>: <c>id</c> or <c>id:sub</c>, comma-separated, unique by id+sub.</summary>
    public static string Format(IEnumerable<ModRef> mods)
    {
        if (mods == null) return "";
        var seen = new HashSet<ModRef>();
        var sb = new StringBuilder();
        foreach (var m in mods)
        {
            if (m.Id == 0 || !seen.Add(m)) continue;
            if (sb.Length > 0) sb.Append(',');
            sb.Append(m.ToString());
        }
        return sb.ToString();
    }

    /// <summary>Parse <c>liverymods=</c> value. Bad tokens skipped.</summary>
    public static List<ModRef> Parse(string csv)
    {
        var list = new List<ModRef>();
        if (string.IsNullOrWhiteSpace(csv)) return list;
        var seen = new HashSet<ModRef>();
        foreach (var raw in csv.Split(','))
        {
            var t = raw.Trim();
            if (t.Length == 0) continue;
            long id; int sub = 0;
            var colon = t.IndexOf(':');
            if (colon < 0)
            {
                if (!long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) || id == 0) continue;
            }
            else
            {
                if (!long.TryParse(t.AsSpan(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out id) || id == 0) continue;
                if (colon + 1 < t.Length)
                    int.TryParse(t.AsSpan(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out sub);
            }
            var m = new ModRef(id, sub);
            if (seen.Add(m)) list.Add(m);
        }
        return list;
    }

    /// <summary>Required host mods that <paramref name="hasLocal"/> reports as missing.</summary>
    public static List<ModRef> Missing(IReadOnlyList<ModRef> required, Func<ModRef, bool> hasLocal)
    {
        var miss = new List<ModRef>();
        if (required == null || hasLocal == null) return miss;
        foreach (var m in required)
            if (m.Id != 0 && !hasLocal(m)) miss.Add(m);
        return miss;
    }
}

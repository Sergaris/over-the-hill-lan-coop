using System;
using System.Collections.Generic;
using System.Text;

namespace OthLan;

/// <summary>
/// 0.1.7 LAN lobby discovery (ROADMAP 6.3), pure and unit-tested.
/// Request (UDP to host:Port+1, broadcast or unicast): "OTHLAN?DISC1 nonce=&lt;n&gt; mod=&lt;ver&gt;".
/// Reply (unicast to the source): "OTHLAN DISC1" then key=value lines: id, name, cur, max, mod, build, gahash,
/// fusion, area, carsync, port, nonce. Strict, bounded parsing: unknown keys ignored, values length-capped,
/// nothing executed.
/// </summary>
public static class DiscoveryLogic
{
    public const string RequestHead = "OTHLAN?DISC1";
    public const string ReplyHead = "OTHLAN DISC1";
    public const int MaxPacket = 1024;
    public const double ExpireSeconds = 6.0;

    public sealed class Lobby
    {
        public string Id = "", Name = "", Mod = "", Build = "", GaHash = "", Fusion = "", Area = "";
        public int Cur, Max, CarSync = -1, Port = 27015;
        public uint Nonce;
        // filled by the list
        public string Ip = ""; public double PingMs = double.NaN; public double SeenAt; public string Via = "";
        public string Endpoint => $"{Ip}:{Port}";
        public bool Full => Max > 0 && Cur >= Max;
    }

    public static string Request(uint nonce, string mod) => $"{RequestHead} nonce={nonce} mod={Clean(mod, 24)}";

    public static bool TryParseRequest(string text, out uint nonce, out string mod)
    {
        nonce = 0; mod = null;
        if (text == null || text.Length > 200 || !text.StartsWith(RequestHead, StringComparison.Ordinal)) return false;
        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("nonce=", StringComparison.Ordinal)) uint.TryParse(part[6..], out nonce);
            else if (part.StartsWith("mod=", StringComparison.Ordinal)) mod = Clean(part[4..], 24);
        }
        return true;
    }

    /// <summary>Strip control characters and '=' / newlines, cap the length (host names come from the user's cfg).</summary>
    public static string Clean(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, max));
        foreach (var ch in s)
        {
            if (sb.Length >= max) break;
            if (char.IsControl(ch) || ch == '\n' || ch == '\r') continue;
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    public static string Reply(Lobby l)
    {
        var sb = new StringBuilder();
        sb.Append(ReplyHead).Append('\n');
        sb.Append("id=").Append(Clean(l.Id, 40)).Append('\n');
        sb.Append("name=").Append(Clean(l.Name, 32)).Append('\n');
        sb.Append("cur=").Append(l.Cur).Append('\n');
        sb.Append("max=").Append(l.Max).Append('\n');
        sb.Append("mod=").Append(Clean(l.Mod, 24)).Append('\n');
        sb.Append("build=").Append(Clean(l.Build, 40)).Append('\n');
        sb.Append("gahash=").Append(Clean(l.GaHash, 80)).Append('\n');
        sb.Append("fusion=").Append(Clean(l.Fusion, 24)).Append('\n');
        sb.Append("area=").Append(Clean(l.Area, 64)).Append('\n');
        sb.Append("carsync=").Append(l.CarSync).Append('\n');
        sb.Append("port=").Append(l.Port).Append('\n');
        sb.Append("nonce=").Append(l.Nonce).Append('\n');
        return sb.ToString();
    }

    public static bool TryParseReply(string text, out Lobby l)
    {
        l = null;
        if (text == null || text.Length > MaxPacket || !text.StartsWith(ReplyHead + "\n", StringComparison.Ordinal)) return false;
        var r = new Lobby();
        foreach (var line in text.Split('\n'))
        {
            var i = line.IndexOf('=');
            if (i <= 0) continue;
            var k = line[..i]; var v = line[(i + 1)..];
            switch (k)
            {
                case "id": r.Id = Clean(v, 40); break;
                case "name": r.Name = Clean(v, 32); break;
                case "cur": int.TryParse(v, out r.Cur); break;
                case "max": int.TryParse(v, out r.Max); break;
                case "mod": r.Mod = Clean(v, 24); break;
                case "build": r.Build = Clean(v, 40); break;
                case "gahash": r.GaHash = Clean(v, 80); break;
                case "fusion": r.Fusion = Clean(v, 24); break;
                case "area": r.Area = Clean(v, 64); break;
                case "carsync": int.TryParse(v, out r.CarSync); break;
                case "port": if (!int.TryParse(v, out r.Port) || r.Port <= 0 || r.Port > 65534) r.Port = 27015; break;
                case "nonce": uint.TryParse(v, out r.Nonce); break;
            }
        }
        if (r.Cur < 0 || r.Max < 0 || r.Max > 64) return false;
        l = r;
        return true;
    }

    public enum Compat { Ok, OtherMod, OtherGame }

    public static Compat Check(Lobby l, string myMod, string myBuildKey, string myGaHash, string myFusion = null)
    {
        if (l.Mod != myMod) return Compat.OtherMod;
        if (!GateLogic.CompatibleSession(l.Build, l.GaHash, myBuildKey, myGaHash, l.Fusion, myFusion)) return Compat.OtherGame;
        return Compat.Ok;
    }

    /// <summary>Address preference when one host answers on several addresses: Radmin (26.x) &gt; other private LAN &gt;
    /// anything else &gt; loopback (loopback only works on the same PC).</summary>
    public static int AddressRank(string ip)
    {
        if (string.IsNullOrEmpty(ip)) return 0;
        if (ip.StartsWith("26.", StringComparison.Ordinal)) return 4;
        if (ip.StartsWith("192.168.", StringComparison.Ordinal) || ip.StartsWith("10.", StringComparison.Ordinal)) return 3;
        if (ip.StartsWith("172.", StringComparison.Ordinal)) return 3;
        if (ip.StartsWith("127.", StringComparison.Ordinal)) return 1;
        return 2;
    }

    /// <summary>Directed broadcast address of ip/mask (e.g. 26.10.20.30/255.0.0.0 -&gt; 26.255.255.255).</summary>
    public static byte[] DirectedBroadcast(byte[] ip, byte[] mask)
    {
        var r = new byte[4];
        for (int i = 0; i < 4; i++) r[i] = (byte)(ip[i] | ~mask[i]);
        return r;
    }

    /// <summary>"26.1.2.3:27015" / "26.1.2.3" (default game port) -&gt; (ip, game port); false for junk.</summary>
    public static bool TryParseManual(string s, out string ip, out int port)
    {
        ip = null; port = 27015;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        var c = s.LastIndexOf(':');
        var host = c > 0 ? s[..c] : s;
        if (c > 0 && (!int.TryParse(s[(c + 1)..], out port) || port <= 0 || port > 65534)) return false;
        var parts = host.Split('.');
        if (parts.Length != 4) return false;
        foreach (var p in parts) if (!byte.TryParse(p, out _)) return false;
        ip = host;
        return true;
    }

    /// <summary>The visible lobby list: one row per host id, best address, smoothed ping, expiry after 6 s.</summary>
    public sealed class LobbyList
    {
        readonly Dictionary<string, Lobby> _byId = new();
        public void Upsert(Lobby l, string fromIp, double rttMs, double now, string via)
        {
            var key = string.IsNullOrEmpty(l.Id) ? $"{fromIp}:{l.Port}" : l.Id;
            l.Ip = fromIp; l.SeenAt = now; l.Via = via;
            if (_byId.TryGetValue(key, out var old))
            {
                var sameAddr = old.Ip == fromIp;
                if (!sameAddr && AddressRank(fromIp) < AddressRank(old.Ip) && now - old.SeenAt < ExpireSeconds)
                {   // a worse address of a host we already see: refresh the time only
                    old.SeenAt = now; return;
                }
                l.PingMs = sameAddr && !double.IsNaN(old.PingMs) && !double.IsNaN(rttMs) ? PingLogic.Smooth(old.PingMs, rttMs, 0.4) : rttMs;
            }
            else l.PingMs = rttMs;
            _byId[key] = l;
        }
        public void Expire(double now)
        {
            var dead = new List<string>();
            foreach (var kv in _byId) if (now - kv.Value.SeenAt > ExpireSeconds) dead.Add(kv.Key);
            foreach (var k in dead) _byId.Remove(k);
        }
        public List<Lobby> Sorted()
        {
            var r = new List<Lobby>(_byId.Values);
            r.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase) is var c && c != 0 ? c : string.CompareOrdinal(a.Ip, b.Ip));
            return r;
        }
        public int Count => _byId.Count;
        public void Clear() => _byId.Clear();
    }
}

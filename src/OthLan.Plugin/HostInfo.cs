using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace OthLan;

/// <summary>
/// Session info that vanilla Fusion clients read from Photon room properties (area, room props), which a LAN client
/// never sees. Without it the client loads its OWN area pick while the host is elsewhere: host-positioned cars float
/// over the wrong terrain (0.1.3: client loaded R2A2_Canyon, host was in another area).
/// The host answers a tiny UDP query on Port+1; the client asks before it starts and loads the host's area.
/// Wire format (UTF-8 lines): request "OTHLAN?1"; reply "OTHLAN 1", "mod=..", "build=..", "gahash=..", "fusion=..",
/// "players=..", "area=..", then "prop.KEY=s:VALUE" / "i:" / "b:" for every session property the game passed to
/// StartGame; 0.1.7 adds "carsync=", "name=" (lobby name), "cur=" (players now) and "max=" (= players) at the end.
/// 0.1.7: also answers the Local Co-op discovery request "OTHLAN?DISC1" (DiscoveryLogic) while hosting.
/// </summary>
internal static class HostInfo
{
    public const string Request = "OTHLAN?1";
    public const string SlotRequest = "OTHLAN?SLOT1";   // "OTHLAN?SLOT1 id=<clientId> hint=<n>" -> "OTHLAN SLOT1 slot=<n>" (-1 = full)
    static readonly object _lock = new();
    static string _reply;            // null = not hosting
    static Thread _thread;
    static UdpClient _udp;
    static int _boundPort;

    public static int InfoPort => Plugin.Port.Value + 1;
    /// <summary>Host: players in the session now (host included), refreshed 1/s on the main thread.</summary>
    public static volatile int CurPlayers = 1;
    static volatile int _maxPlayers; static volatile string _area = "";
    /// <summary>Lobby name (main thread sets it: Steam name / cfg / PC name).</summary>
    public static volatile string LobbyName = Environment.MachineName;
    public static readonly string SessionId = Guid.NewGuid().ToString("N")[..12];
    /// <summary>Host: <c>liverymods=</c> payload (main thread refreshes via <see cref="SetLiveryMods"/>).</summary>
    static volatile string _liveryMods = "";

    /// <summary>Host: update the Modio livery ids required to join (wire: <see cref="ModLiveryGateLogic.Format"/>).</summary>
    public static void SetLiveryMods(string csv) { _liveryMods = csv ?? ""; }

    /// <summary>Host: publish (or replace) the session description. Called from the StartGame detour.</summary>
    public static void Publish(string area, int players, Dictionary<string, string> props)
    {
        var sb = new StringBuilder();
        sb.Append("OTHLAN 1\n");
        sb.Append("mod=").Append(Plugin.Version).Append('\n');
        sb.Append("build=").Append(BuildGate.BuildKey ?? "?").Append('\n');
        sb.Append("gahash=").Append(BuildGate.GaHash ?? "").Append('\n');
        sb.Append("fusion=").Append(BuildGate.FusionVersion ?? "").Append('\n');
        sb.Append("players=").Append(players).Append('\n');
        sb.Append("area=").Append(area ?? "").Append('\n');
        foreach (var kv in props) sb.Append("prop.").Append(kv.Key).Append('=').Append(kv.Value.Replace('\n', ' ')).Append('\n');
        lock (_lock) _reply = sb.ToString();
        _maxPlayers = players; _area = area ?? "";
        EnsureListener();
        Plugin.L.LogInfo($"[LAN] host info published on UDP {InfoPort}: area={area ?? "?"} players={players} props={props.Count}");
    }

    /// <summary>Full reply including the latest liverymods line (appended outside the locked base blob).</summary>
    static string ReplyNow()
    {
        string body; lock (_lock) body = _reply;
        if (body == null) return null;
        var lm = _liveryMods;
        if (string.IsNullOrEmpty(lm)) return body;
        return body + "liverymods=" + lm.Replace('\n', ' ') + "\n";
    }

    public static void Clear() { lock (_lock) _reply = null; }
    public static int DiscoveryAnswers;

    public static void SendTo(byte[] b, IPEndPoint ep) { if (OutageSim.Active) return; try { _udp?.Send(b, b.Length, ep); } catch { } }

    static void EnsureListener()
    {
        if (_thread != null && _boundPort == InfoPort) return;
        try
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, InfoPort));
            try { _udp.Client.IOControl(-1744830452 /*SIO_UDP_CONNRESET*/, new byte[] { 0 }, null); } catch { } // a vanished client must not break Receive
            _boundPort = InfoPort;
            _thread = new Thread(Loop) { IsBackground = true, Name = "OTH-LAN host info" };
            _thread.Start();
        }
        catch (Exception e) { Plugin.L.LogError($"[LAN] host info listener on UDP {InfoPort} failed: {e.Message}"); _thread = null; }
    }

    static void Loop()
    {
        while (true)
        {
            try
            {
                var ep = new IPEndPoint(IPAddress.Any, 0);
                var data = _udp.Receive(ref ep);
                if (OutageSim.Active) continue;   // self-test: full outage of this process
                if (data.Length >= 4)
                {
                    var magic = BitConverter.ToUInt32(data, 0);
                    if (magic == CarSyncLogic.PingMagic)
                    {
                        if (!CarSyncLogic.TryDecodePing(data, out var kind, out var slot, out var seq)) continue;
                        if (kind == 0)
                        {   // reply to the SOURCE port; append the session's CarSync flag (host is authoritative)
                            HostPings.SawClient(slot, ep);
                            var rep = new byte[CarSyncLogic.PingSize + 1];
                            Array.Copy(data, rep, CarSyncLogic.PingSize);
                            rep[4] = 1; rep[CarSyncLogic.PingSize] = (byte)CarSync.HostFlagsFor(slot);
                            _udp.Send(rep, rep.Length, ep);
                        }
                        else HostPings.OnReply(slot, seq);
                        continue;
                    }
                    if (magic == CarSyncLogic.PoseMagic) { HostFollower.OnPosePacket(data, ep); continue; }
                    if (magic == CarWire.Magic) { CarRelay.OnPacket(data, data.Length, ep, NetClock.Now); continue; }
                    if (magic == CoopSessionLogic.ResyncMagic) { SessionResync.Accept(data, ep); continue; }
                    if (magic == CoopSessionLogic.ListMagic || magic == CoopSessionLogic.DiagMagic) continue;
                }
                var text = Encoding.UTF8.GetString(data).Trim();
                if (text.StartsWith(SlotRequest, StringComparison.Ordinal))
                {
                    string hosting; lock (_lock) hosting = _reply;
                    if (hosting == null) continue;
                    var id = Field(text, "id"); int.TryParse(Field(text, "hint"), out var hint);
                    var slot = LanState.Slots.Request(id, hint, NetClock.Now);
                    var rb = Encoding.UTF8.GetBytes($"OTHLAN SLOT1 slot={slot}\n");
                    _udp.Send(rb, rb.Length, ep);
                    Plugin.L.LogInfo($"[LAN] slot request from {ep} id={id} hint={hint} -> {slot}  [{LanState.Slots.Describe()}]");
                    continue;
                }
                if (text.StartsWith(DiscoveryLogic.RequestHead, StringComparison.Ordinal))
                {   // 0.1.7 Local Co-op lobby list: always answer while hosting (no hidden lobbies)
                    string hosting; lock (_lock) hosting = _reply;
                    if (hosting == null || !DiscoveryLogic.TryParseRequest(text, out var nonce, out _)) continue;
                    var lob = new DiscoveryLogic.Lobby
                    {
                        Id = SessionId, Name = LobbyName, Cur = CurPlayers, Max = _maxPlayers, Mod = Plugin.Version, Build = BuildGate.BuildKey ?? "?",
                        GaHash = BuildGate.GaHash ?? "", Fusion = BuildGate.FusionVersion ?? "", Area = _area,
                        CarSync = CarSync.HostEnabled ? 1 : 0, Port = Plugin.Port.Value, Nonce = nonce,
                    };
                    var db = Encoding.UTF8.GetBytes(DiscoveryLogic.Reply(lob));
                    _udp.Send(db, db.Length, ep);
                    DiscoveryAnswers++;
                    continue;
                }
                if (text != Request) continue;
                var r = ReplyNow();
                if (r == null) continue;
                r += $"carsync={(CarSync.HostEnabled ? 1 : 0)}\nname={DiscoveryLogic.Clean(LobbyName, 32)}\ncur={CurPlayers}\nmax={_maxPlayers}\n";
                var b = Encoding.UTF8.GetBytes(r);
                _udp.Send(b, b.Length, ep);
            }
            catch (SocketException) { Thread.Sleep(200); }
            catch (ObjectDisposedException) { return; }
            catch (Exception) { Thread.Sleep(200); }
        }
    }

    static string Field(string text, string key)
    {
        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (part.StartsWith(key + "=", StringComparison.Ordinal)) return part[(key.Length + 1)..];
        return null;
    }

    /// <summary>Client: ask the host to reserve a slot (blocking, off the main thread). Returns the slot, -1 if the
    /// host is full, or null if it did not answer (then the IP-derived fallback is used).</summary>
    public static int? RequestSlot(string ip, int port, string clientId, int hint, out string error)
    {
        error = null;
        try
        {
            using var u = new UdpClient(0);
            u.Client.ReceiveTimeout = 800;
            var ep = new IPEndPoint(IPAddress.Parse(ip), port);
            var req = Encoding.UTF8.GetBytes($"{SlotRequest} id={clientId} hint={hint}");
            for (int attempt = 0; attempt < 4; attempt++)
            {
                u.Send(req, req.Length, ep);
                try
                {
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    var s = Encoding.UTF8.GetString(u.Receive(ref from)).Trim();
                    if (!s.StartsWith("OTHLAN SLOT1", StringComparison.Ordinal)) continue;
                    if (int.TryParse(Field(s, "slot"), out var slot)) return slot;
                }
                catch (SocketException) { }
            }
            error = "no answer to the slot request";
        }
        catch (Exception e) { error = e.Message; }
        return null;
    }

    public sealed class Info
    {
        public string Mod, Build, GaHash, Fusion, Area, Name = "", LiveryMods = ""; public int Players, Cur = -1, Max = -1; public int CarSync = -1;
        public Dictionary<string, string> Props = new();
    }

    /// <summary>Client: blocking query (run off the main thread). Returns null on timeout.</summary>
    public static Info Query(string ip, int port, out string error)
    {
        error = null;
        try
        {
            using var u = new UdpClient(0);
            u.Client.ReceiveTimeout = 1000;
            var ep = new IPEndPoint(IPAddress.Parse(ip), port);
            var req = Encoding.UTF8.GetBytes(Request);
            for (int attempt = 0; attempt < 4; attempt++)
            {
                u.Send(req, req.Length, ep);
                try
                {
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    var data = u.Receive(ref from);
                    return Parse(Encoding.UTF8.GetString(data));
                }
                catch (SocketException) { /* timeout or ICMP unreachable: retry */ }
            }
            error = $"host did not answer on UDP {port}";
        }
        catch (Exception e) { error = e.Message; }
        return null;
    }

    static Info Parse(string s)
    {
        var info = new Info();
        foreach (var line in s.Split('\n'))
        {
            var i = line.IndexOf('=');
            if (i <= 0) continue;
            var k = line[..i]; var v = line[(i + 1)..];
            switch (k)
            {
                case "mod": info.Mod = v; break;
                case "build": info.Build = v; break;
                case "gahash": info.GaHash = v.Trim(); break;
                case "fusion": info.Fusion = v.Trim(); break;
                case "area": info.Area = v; break;
                case "players": int.TryParse(v, out info.Players); break;
                case "carsync": info.CarSync = v.Trim() == "1" ? 1 : 0; break;
                case "name": info.Name = DiscoveryLogic.Clean(v, 32); break;
                case "cur": int.TryParse(v, out info.Cur); break;
                case "max": int.TryParse(v, out info.Max); break;
                case "liverymods": info.LiveryMods = v.Trim(); break;
                default: if (k.StartsWith("prop.")) info.Props[k[5..]] = v; break;
            }
        }
        return info;
    }
}

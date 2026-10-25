using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace OthLan;

/// <summary>
/// 0.1.7 Local Co-op: finds OTH-LAN hosts while the lobby screen is open (every 2 s), on every channel at once
/// (ROADMAP 6.3): 26.255.255.255 from a socket bound to the Radmin IP; 255.255.255.255 + the directed broadcast from a
/// socket bound to each IPv4 adapter; unicast to the connection history, the manual "add by IP" list, cfg JoinAddress,
/// the Radmin adapter's ARP neighbours and 127.0.0.1 (same PC). No scan of 26.0.0.0/8.
/// </summary>
internal static class Discovery
{
    static Thread _thread;
    static volatile bool _run;
    static readonly object _lock = new();
    static readonly DiscoveryLogic.LobbyList _list = new();
    public static volatile string Status = "";
    public static int Rounds, Replies, Sent;
    public static readonly Dictionary<string, int> RepliesVia = new();
    static double Now => NetClock.Now;

    public static void Start()
    {
        if (_run) return;
        _run = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "OTH-LAN discovery" };
        _thread.Start();
        Plugin.L.LogInfo("[Coop] lobby discovery started");
    }

    public static void Stop() { if (!_run) return; _run = false; Plugin.L.LogInfo($"[Coop] lobby discovery stopped ({Rounds} rounds, {Replies} replies)"); }

    public static void RefreshNow() { _nextRound = 0; }
    static volatile int _nextRound;

    public static List<DiscoveryLogic.Lobby> Snapshot()
    {
        lock (_lock) { _list.Expire(Now); return _list.Sorted(); }
    }

    sealed class Sock { public UdpClient U; public string Name; public IPAddress Local; public List<IPEndPoint> Bcast = new(); }

    static List<Sock> OpenSockets(int infoPort)
    {
        var r = new List<Sock>();
        var radmin = NetInfo.RadminIp();
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var a in n.GetIPProperties().UnicastAddresses)
            {
                if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                try
                {
                    var u = new UdpClient(new IPEndPoint(a.Address, 0)) { EnableBroadcast = true };
                    try { u.Client.IOControl(-1744830452 /*SIO_UDP_CONNRESET*/, new byte[] { 0 }, null); } catch { }
                    var s = new Sock { U = u, Local = a.Address, Name = a.Address.ToString() == radmin ? "radmin" : "lan" };
                    s.Bcast.Add(new IPEndPoint(IPAddress.Broadcast, infoPort));
                    if (a.IPv4Mask != null)
                        s.Bcast.Add(new IPEndPoint(new IPAddress(DiscoveryLogic.DirectedBroadcast(a.Address.GetAddressBytes(), a.IPv4Mask.GetAddressBytes())), infoPort));
                    if (a.Address.GetAddressBytes()[0] == 26) s.Bcast.Add(new IPEndPoint(IPAddress.Parse("26.255.255.255"), infoPort));
                    r.Add(s);
                }
                catch (Exception e) { Plugin.L.LogWarning($"[Coop] socket on {a.Address} failed: {e.Message}"); }
            }
        }
        try
        {   // unicast socket (any interface; the OS picks the route)
            var u = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            try { u.Client.IOControl(-1744830452, new byte[] { 0 }, null); } catch { }
            r.Add(new Sock { U = u, Local = IPAddress.Any, Name = "unicast" });
        }
        catch (Exception e) { Plugin.L.LogWarning($"[Coop] unicast socket failed: {e.Message}"); }
        return r;
    }

    /// <summary>Unicast targets: history, manual list, JoinAddress, ARP neighbours on the Radmin adapter, loopback.</summary>
    static List<(IPEndPoint ep, string via)> UnicastTargets()
    {
        var t = new List<(IPEndPoint, string)>();
        var seen = new HashSet<string>();
        void Add(string ip, int gamePort, string via)
        {
            if (!IPAddress.TryParse(ip, out var a)) return;
            var ep = new IPEndPoint(a, gamePort + 1);
            if (seen.Add(ep.ToString())) t.Add((ep, via));
        }
        foreach (var s in (Plugin.ManualHosts.Value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (DiscoveryLogic.TryParseManual(s, out var ip, out var port)) Add(ip, port, "manual");
        foreach (var s in (Plugin.RecentHosts.Value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (DiscoveryLogic.TryParseManual(s, out var ip, out var port)) Add(ip, port, "history");
        if (DiscoveryLogic.TryParseManual(Plugin.JoinAddress.Value, out var jip, out var jport)) Add(jip, jport, "history");
        foreach (var ip in RadminNeighbours()) Add(ip, Plugin.Port.Value, "arp");
        Add("127.0.0.1", Plugin.Port.Value, "loopback");
        return t;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MIB_IPNETROW { public uint dwIndex, dwPhysAddrLen; public ulong bPhysAddr; public uint dwAddr, dwType; }
    [DllImport("iphlpapi.dll")] static extern int GetIpNetTable(IntPtr table, ref int size, bool order);

    /// <summary>26.x neighbours the Radmin adapter knows (ARP cache), excluding broadcast/multicast and ourselves.</summary>
    public static List<string> RadminNeighbours()
    {
        var r = new List<string>();
        try
        {
            int idx = -1; string mine = null;
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                foreach (var a in n.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork && a.Address.GetAddressBytes()[0] == 26)
                    { idx = n.GetIPProperties().GetIPv4Properties()?.Index ?? -1; mine = a.Address.ToString(); }
            if (idx < 0) return r;
            int size = 0; GetIpNetTable(IntPtr.Zero, ref size, false);
            if (size <= 0 || size > 1 << 20) return r;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetIpNetTable(buf, ref size, false) != 0) return r;
                var count = Marshal.ReadInt32(buf);
                var rowSize = Marshal.SizeOf<MIB_IPNETROW>();
                for (int i = 0; i < count && i < 4096; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_IPNETROW>(buf + 4 + i * rowSize);
                    if (row.dwIndex != idx || row.dwType < 3) continue;   // 3 dynamic, 4 static
                    var b = BitConverter.GetBytes(row.dwAddr);
                    if (b[0] != 26 || b[3] == 255 || b[3] == 0) continue;
                    var ip = new IPAddress(b).ToString();
                    if (ip != mine && r.Count < 64) r.Add(ip);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { }
        return r;
    }

    static void Loop()
    {
        var infoPort = Plugin.Port.Value + 1;
        List<Sock> socks = null;
        uint nonce = (uint)Environment.TickCount;
        double roundAt = 0; var sentAt = new Dictionary<uint, double>();
        var buf = new byte[DiscoveryLogic.MaxPacket + 64];
        try
        {
            socks = OpenSockets(infoPort);
            var any = socks.Find(s => s.Name == "unicast");
            Status = $"{socks.Count - 1} adapters";
            while (_run)
            {
                var now = Now;
                if (now - roundAt >= 2.0 || _nextRound == 0)
                {
                    _nextRound = 1; roundAt = now; nonce++; Rounds++;
                    sentAt[nonce] = now; if (sentAt.Count > 8) { var old = new List<uint>(sentAt.Keys); old.Sort(); sentAt.Remove(old[0]); }
                    var req = Encoding.UTF8.GetBytes(DiscoveryLogic.Request(nonce, Plugin.Version));
                    foreach (var s in socks)
                        foreach (var b in s.Bcast) { try { s.U.Send(req, req.Length, b); Sent++; } catch { } }
                    if (any != null)
                        foreach (var (ep, _) in UnicastTargets()) { try { any.U.Send(req, req.Length, ep); Sent++; } catch { } }
                }
                var gotAny = false;
                foreach (var s in socks)
                {
                    while (s.U.Available > 0)
                    {
                        gotAny = true;
                        var from = new IPEndPoint(IPAddress.Any, 0);
                        byte[] d;
                        try { d = s.U.Receive(ref from); } catch { break; }
                        if (d.Length > DiscoveryLogic.MaxPacket) continue;
                        if (!DiscoveryLogic.TryParseReply(Encoding.UTF8.GetString(d), out var l)) continue;
                        var t = Now;
                        var rtt = sentAt.TryGetValue(l.Nonce, out var st) ? (t - st) * 1000.0 : double.NaN;
                        var via = s.Name == "unicast" ? (from.Address.ToString().StartsWith("127.") ? "loopback" : "unicast") : s.Name + "-bcast";
                        lock (_lock) _list.Upsert(l, from.Address.ToString(), rtt, t, via);
                        Replies++;
                        lock (RepliesVia) RepliesVia[via] = RepliesVia.TryGetValue(via, out var c) ? c + 1 : 1;
                    }
                }
                if (!gotAny) Thread.Sleep(15);
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[Coop] discovery stopped: {e.Message}"); Status = "error: " + e.Message; }
        finally { if (socks != null) foreach (var s in socks) { try { s.U.Close(); } catch { } } }
    }

    /// <summary>Remember a host we joined (newest first, max 8).</summary>
    public static void RememberHost(string ip, int port)
    {
        var e = $"{ip}:{port}";
        var list = new List<string> { e };
        foreach (var s in (Plugin.RecentHosts.Value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)) if (s != e && list.Count < 8) list.Add(s);
        Plugin.RecentHosts.Value = string.Join(",", list);
    }

    public static bool AddManual(string text, out string norm)
    {
        norm = null;
        if (!DiscoveryLogic.TryParseManual(text, out var ip, out var port)) return false;
        norm = $"{ip}:{port}";
        var list = new List<string>((Plugin.ManualHosts.Value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries));
        if (!list.Contains(norm)) list.Insert(0, norm);
        while (list.Count > 16) list.RemoveAt(list.Count - 1);
        Plugin.ManualHosts.Value = string.Join(",", list);
        RefreshNow();
        return true;
    }
}

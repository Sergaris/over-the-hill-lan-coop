using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace OthLan;

/// <summary>
/// Client side of the OTH-LAN side channel to the host's real Radmin address, UDP Port+1 (27016).
/// The client only SENDS to host:27016 and receives on the same socket, so replies come back to its source port
/// (stateful firewall return path): no inbound rule is needed on the client. Only the host needs 27016 open.
///  * our pings (kind 0, 5/s) -> host replies kind 1 -> RTT (the ping label's only source)
///  * host pings (kind 0 arriving from the host) -> we echo kind 1 so the host measures its RTT to us
///  * car poses (CarSync)
/// </summary>
internal static class ClientLink
{
    static UdpClient _udp;
    static IPEndPoint _host;
    static Thread _rx;
    public static readonly EchoTracker Tracker = new();
    public static int HostPingsEchoed;

    // ---- self-test network emulation (client side only, our side channel only; Fusion traffic is NOT affected) ----
    /// <summary>Extra round-trip time in ms added to everything on this link (half on send, half on receive).</summary>
    public static volatile int NetemRttMs;
    /// <summary>Drop probability per packet and direction (0..1).</summary>
    public static volatile float NetemLoss;
    static readonly Random Rng = new();
    static bool Drop() { if (OutageSim.Active) return true; var l = Math.Max(NetemLoss, OutageSim.Loss); if (l <= 0) return false; lock (Rng) return Rng.NextDouble() < l; }
    static void Later(Action a)
    {
        var d = NetemRttMs / 2;
        if (d <= 0) { a(); return; }
        new Thread(() => { try { Thread.Sleep(d); a(); } catch { } }) { IsBackground = true }.Start();   // dev-only path (self-test)
    }
    static void Out(UdpClient u, byte[] b, IPEndPoint to)
    {
        if (Drop()) return;
        Later(() => { try { u.Send(b, b.Length, to); } catch { } });
    }

    public static void Ensure(string ip, int infoPort)
    {
        var ep = new IPEndPoint(IPAddress.Parse(ip), infoPort);
        if (_udp != null && _host != null && _host.Equals(ep)) return;
        Stop();
        _host = ep;
        _udp = new UdpClient(0);
        try { _udp.Client.IOControl(-1744830452 /*SIO_UDP_CONNRESET*/, new byte[] { 0 }, null); } catch { } // ICMP unreachable must not kill Receive
        _rx = new Thread(Rx) { IsBackground = true, Name = "OTH-LAN client link" };
        _rx.Start();
        Plugin.L.LogInfo($"[LAN] side channel (ping/echo) to {ep}");
    }

    public static void Stop()
    {
        try { _udp?.Close(); } catch { }
        _udp = null; _host = null;
        lock (Tracker) Tracker.Reset();
    }

    public static void SendPing(byte slot)
    {
        var u = _udp; var h = _host; if (u == null) return;
        uint seq; var now = NetClock.Now;
        lock (Tracker) seq = Tracker.NextSeq(now);
        var b = CarSyncLogic.EncodePing(0, slot, seq, now);
        Out(u, b, h);
    }

    public static void Send(byte[] b)
    {
        var u = _udp; var h = _host; if (u == null) return;
        Out(u, b, h);
    }

    static void Rx()
    {
        var u = _udp;
        while (u != null && _udp == u)
        {
            try
            {
                var from = new IPEndPoint(IPAddress.Any, 0);
                var d = u.Receive(ref from);
                if (d.Length >= 5 && BitConverter.ToUInt32(d, 0) == OutageLogic.StatusMagic) { if (!Drop()) LinkLabels.OnStatus(d, NetClock.Now); continue; }
                if (OutageLogic.TryDecodeAccept(d, out var aslot, out var actr)) { if (!Drop()) OwnCar.OnAccept(aslot, actr); continue; }
                if (d.Length >= 4 && BitConverter.ToUInt32(d, 0) == CarWire.Magic) { if (!Drop()) CarRelay.OnClientPacket(d, d.Length, NetClock.Now); continue; }
                if (d.Length >= 4 && BitConverter.ToUInt32(d, 0) == CoopSessionLogic.ListMagic) { if (!Drop()) CoopSession.OnList(d); continue; }
                if (d.Length >= 4 && BitConverter.ToUInt32(d, 0) == CoopSessionLogic.DiagMagic) { if (!Drop()) CoopSession.OnDiag(); continue; }
                if (!CarSyncLogic.TryDecodePing(d, out var kind, out _, out var seq, out var flags)) continue;
                if (Drop()) continue;
                if (flags >= 0) Later(() => CarSync.OnHostFlags(flags, NetClock.Now));   // host's session CarSync state
                if (kind == 1) Later(() => { lock (Tracker) Tracker.OnReply(seq, NetClock.Now); });
                else { d = d[..CarSyncLogic.PingSize]; d[4] = 1; var src = from; Later(() => { HostPingsEchoed++; Out(u, d, src); }); }   // host's ping: echo to its source
            }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { if (_udp != u) return; Thread.Sleep(20); }
            catch { Thread.Sleep(20); }
        }
    }
}

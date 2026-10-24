using System;
using System.Collections.Generic;
using System.Net;

namespace OthLan;

/// <summary>
/// 0.2.0 T3.3: host OTC3 relay + per-slot StatePlayback for HostFollower / observers.
/// </summary>
internal static class CarRelay
{
    static readonly Dictionary<int, StatePlayback> _play = new();
    static readonly object _lock = new();
    public static int Accepted, Relayed, Dropped, Injected;
    public static string Describe() => $"relay accept={Accepted} relayed={Relayed} drop={Dropped} inject={Injected}";

    public static void Reset()
    {
        lock (_lock) { _play.Clear(); Accepted = Relayed = Dropped = Injected = 0; }
    }

    public static StatePlayback PlaybackOf(int slot)
    {
        lock (_lock)
        {
            if (!_play.TryGetValue(slot, out var pb)) _play[slot] = pb = new StatePlayback();
            return pb;
        }
    }

    /// <summary>Host: OTC3 from a client (network thread; use HostPings.Now for arrival).</summary>
    public static void OnPacket(byte[] d, int len, IPEndPoint from, double arrivalLocal)
    {
        if (d == null || len < CarWire.HeaderSize) { Dropped++; return; }
        if (!CarWire.TryDecode(d.AsSpan(0, len), out var pkt)) { Dropped++; return; }
        var slot = pkt.Slot;
        var ep = HostPings.EndpointOf(slot);
        var dec = RelayLogic.Filter(slot, ep != null, ep != null && EndpointsEqual(ep, from));
        if (dec != RelayLogic.Decision.Accept) { Dropped++; return; }
        Accepted++;
        PushPlayback(slot, pkt, arrivalLocal);
        HostPings.BroadcastExcept(d, len, from);
        Relayed++;
    }

    /// <summary>Host: own car stream (main thread).</summary>
    public static void InjectHost(in CarWire.CarStatePacket pkt)
    {
        Injected++;
        var slot = pkt.Slot;
        PushPlayback(slot, pkt, NetClock.Now);
        var buf = new byte[CarWire.MaxPacketBytes];
        var n = CarWire.Encode(pkt, buf);
        if (n > 0) HostPings.Broadcast(Trim(buf, n));
    }

    /// <summary>Client: OTC3 relayed from host (network thread; use ClientLink.Now).</summary>
    public static void OnClientPacket(byte[] d, int len, double arrivalLocal)
    {
        if (d == null || len < CarWire.HeaderSize) return;
        if (!CarWire.TryDecode(d.AsSpan(0, len), out var pkt)) return;
        if (OwnCar.OwnSlot >= 0 && pkt.Slot == OwnCar.OwnSlot) return;
        PushPlayback(pkt.Slot, pkt, arrivalLocal);
    }

    static void PushPlayback(int slot, in CarWire.CarStatePacket pkt, double arrivalLocal)
    {
        var pb = PlaybackOf(slot);
        for (var i = 0; i < pkt.Count; i++)
        {
            var s = pkt.StateAt(i);
            pb.Push(ToSample(s, pkt.Epoch), arrivalLocal);
        }
    }

    static StatePlayback.Sample ToSample(in CarWire.CarStateSample s, byte epoch) => new()
    {
        Seq = s.Seq, SendTime = s.SendTime, Epoch = epoch,
        Px = s.Px, Py = s.Py, Pz = s.Pz, Qx = s.Qx, Qy = s.Qy, Qz = s.Qz, Qw = s.Qw,
        Vx = s.Vx, Vy = s.Vy, Vz = s.Vz, Wx = s.Wx, Wy = s.Wy, Wz = s.Wz,
        Flags = s.Flags, SteerDeg = s.SteerDeg, WheelCount = s.WheelCount,
        W0 = s.W0, W1 = s.W1, W2 = s.W2, W3 = s.W3, W4 = s.W4, W5 = s.W5,
    };

    static bool EndpointsEqual(IPEndPoint a, IPEndPoint b)
        => a != null && b != null && a.Port == b.Port && a.Address.Equals(b.Address);

    static byte[] Trim(byte[] buf, int n)
    {
        if (n == buf.Length) return buf;
        var t = new byte[n]; Array.Copy(buf, t, n); return t;
    }
}

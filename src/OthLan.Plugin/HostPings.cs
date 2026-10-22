using System;
using System.Collections.Generic;
using System.Net;

namespace OthLan;

/// <summary>Host side: RTT to each LAN client over the same side channel. The host learns each client's endpoint
/// (Radmin IP + source port) from the client's own pings and pings it back on that endpoint, so it rides the
/// client's outbound "connection" (no inbound rule on the client).</summary>
internal static class HostPings
{
    sealed class Peer { public IPEndPoint Ep; public double LastSeen; public readonly EchoTracker T = new(); }
    static readonly Dictionary<int, Peer> _peers = new();
    static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    public static double Now => Clock.Elapsed.TotalSeconds;

    /// <summary>Network thread: a client ping (kind 0) told us where slot lives.</summary>
    public static void SawClient(int slot, IPEndPoint ep)
    {
        lock (_peers)
        {
            if (!_peers.TryGetValue(slot, out var p)) _peers[slot] = p = new Peer();
            if (p.Ep == null || !p.Ep.Equals(ep)) { p.Ep = ep; p.T.Reset(); }
            p.LastSeen = Now;
        }
    }

    /// <summary>Network thread: reply (kind 1) to one of our pings.</summary>
    public static void OnReply(int slot, uint seq)
    {
        lock (_peers) if (_peers.TryGetValue(slot, out var p)) p.T.OnReply(seq, Now);
    }

    /// <summary>Main thread, ~5/s: ping every client seen in the last 10 s.</summary>
    public static void SendAll()
    {
        var now = Now;
        lock (_peers)
            foreach (var kv in _peers)
            {
                if (kv.Value.Ep == null || now - kv.Value.LastSeen > 10) continue;
                var seq = kv.Value.T.NextSeq(now);
                HostInfo.SendTo(CarSyncLogic.EncodePing(0, (byte)kv.Key, seq, now, CarSync.HostFlags), kv.Value.Ep);
            }
    }

    /// <summary>Send a datagram to every client seen in the last 10 s (link status for the over-the-car labels).</summary>
    public static void Broadcast(byte[] b)
    {
        var now = Now;
        lock (_peers) foreach (var kv in _peers) if (kv.Value.Ep != null && now - kv.Value.LastSeen <= 10) HostInfo.SendTo(b, kv.Value.Ep);
    }

    /// <summary>(ms or NaN, no link / recovering, loss%, seconds without any echo or -1) per slot.</summary>
    public static Dictionary<int, (double ms, bool timedOut, double loss, double noLinkS)> Snapshot()
    {
        var now = Now; var r = new Dictionary<int, (double, bool, double, double)>();
        lock (_peers) foreach (var kv in _peers) r[kv.Key] = (kv.Value.T.Value(now), kv.Value.T.TimedOut(now), kv.Value.T.LossPercent, kv.Value.T.NoLinkSeconds(now));
        return r;
    }

    /// <summary>Endpoint the slot's pings come from (null if unknown). Pose packets must come from the same one.</summary>
    public static IPEndPoint EndpointOf(int slot) { lock (_peers) return _peers.TryGetValue(slot, out var p) ? p.Ep : null; }

    public static void Clear() { lock (_peers) _peers.Clear(); }
}

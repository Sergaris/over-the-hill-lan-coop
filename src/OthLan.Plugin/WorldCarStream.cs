using System;
using System.Collections.Generic;
using UnityEngine;

namespace OthLan;

/// <summary>
/// Host: dense OTW1 pose stream for awake world cars (<see cref="ParkedCars"/>). Clients smooth via
/// <see cref="WorldCarView"/>; OTC3 / player slots untouched.
/// </summary>
internal static class WorldCarStream
{
    sealed class Track
    {
        public readonly WorldCarWire.PoseSample[] Hist = new WorldCarWire.PoseSample[3];
        public int HistN;
        public uint Seq;
        public byte Epoch;
        public Vector3 LastP;
        public bool HasLastP;
    }

    static readonly Dictionary<uint, Track> _tracks = new();
    static readonly List<(uint Id, Rigidbody Rb)> _awake = new();
    static readonly byte[] _buf = new byte[WorldCarWire.MaxPacketBytes];
    static float _nextSend;
    public static int Sent, EpochBumps, CarsSent;

    public static void Reset()
    {
        _tracks.Clear();
        _nextSend = 0;
        Sent = EpochBumps = CarsSent = 0;
    }

    public static void Tick()
    {
        if (LanState.Active != LanRole.Host
            || !Plugin.CarSyncEnabled.Value
            || (Plugin.RelayCarState != null && !Plugin.RelayCarState.Value)
            || !Plugin.ParkIdleCars.Value)
        {
            if (_tracks.Count > 0) Reset();
            return;
        }

        var hz = Plugin.CarSendHz.Value;
        if (hz < 1f) hz = 1f;
        var now = Time.realtimeSinceStartup;
        if (now < _nextSend) return;
        _nextSend = now + 1f / hz;

        _awake.Clear();
        ParkedCars.CollectAwake(_awake);
        CarsSent = 0;
        var red = Plugin.CarStreamRedundancy.Value;
        if (red < 1) red = 1; if (red > 3) red = 3;

        foreach (var (id, rb) in _awake)
        {
            if (rb == null || rb.WasCollected) continue;
            try { SendOne(id, rb, red); }
            catch { }
        }
    }

    static void SendOne(uint id, Rigidbody rb, int red)
    {
        if (!_tracks.TryGetValue(id, out var tr))
        {
            tr = new Track();
            _tracks[id] = tr;
        }
        var p = rb.position;
        if (tr.HasLastP && (p - tr.LastP).sqrMagnitude > 25f)
        {
            tr.Epoch++;
            EpochBumps++;
        }
        tr.LastP = p;
        tr.HasLastP = true;

        var q = rb.rotation;
        var v = rb.linearVelocity;
        var w = rb.angularVelocity;
        // Kinematic tow: Unity vel is often 0 — finite-diff from last pose so Hermite has tangents.
        if (rb.isKinematic && tr.HistN > 0)
        {
            var prev = tr.Hist[0];
            var dt = (float)Math.Max(1e-3, NetClock.Now - prev.SendTime);
            v = (p - new Vector3(prev.Px, prev.Py, prev.Pz)) / dt;
            w = Vector3.zero;
        }

        var s = new WorldCarWire.PoseSample
        {
            Seq = ++tr.Seq,
            SendTime = NetClock.Now,
            Px = p.x, Py = p.y, Pz = p.z,
            Qx = q.x, Qy = q.y, Qz = q.z, Qw = q.w,
            Vx = v.x, Vy = v.y, Vz = v.z,
            Wx = w.x, Wy = w.y, Wz = w.z,
        };
        if (tr.HistN < 3) tr.HistN++;
        for (var i = tr.HistN - 1; i > 0; i--) tr.Hist[i] = tr.Hist[i - 1];
        tr.Hist[0] = s;

        var count = (byte)Math.Min(red, tr.HistN);
        var pkt = new WorldCarWire.Packet { NetworkId = id, Epoch = tr.Epoch, Count = count };
        for (var i = 0; i < count; i++) pkt.SetState(i, tr.Hist[i]);
        var n = WorldCarWire.Encode(pkt, _buf);
        if (n <= 0) return;
        var copy = new byte[n];
        Array.Copy(_buf, copy, n);
        HostPings.Broadcast(copy);
        Sent++;
        CarsSent++;
    }

    public static string Describe() => $"worldStream sent={Sent} cars={CarsSent} epochs={EpochBumps} tracks={_tracks.Count}";
}

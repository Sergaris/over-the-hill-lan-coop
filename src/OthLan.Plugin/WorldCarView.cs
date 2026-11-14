using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Fusion;
using Fusion.Addons.Physics;
using UnityEngine;
using VehiclePhysics;

namespace OthLan;

/// <summary>
/// Client visual smoothing for world / evacuate cars (Broken_*, abandoned) that are not <see cref="FusionPlayer"/>.
/// Prefers host <see cref="WorldCarWire"/> (OTW1) stream at CarSendHz; falls back to buffering Fusion NRB poses.
/// Skip any object with FusionPlayer so <see cref="RemoteCarView"/> owns player-car roots.
/// </summary>
internal static class WorldCarView
{
    sealed class Car
    {
        public NetworkObject O;
        public Rigidbody Rb;
        public NetworkRigidbody3D Nrb;
        public Transform Root;
        public readonly StatePlayback Play = new(new StatePlayback.Cfg
        {
            SendHz = 30,
            MinDelayS = 0.05,
            MaxDelayS = 0.25,
            MaxExtrapS = 0.25,
        });
        public uint LastLocalSeq;
        public byte Epoch;
        public Vector3 PrevP;
        public double PrevAt;
        public bool HasPrev;
        public double LastMoveAt = -1;
        public double LastStreamAt = -1;
        public bool StreamEpochArmed;
        public bool Logged;
    }

    const float HoldActiveS = 0.75f;
    const float FreshStreamS = 0.5f;
    const float ScanEveryS = 2f;
    const float SnapM = 15f;

    static readonly Dictionary<uint, Car> _cars = new();
    static readonly List<uint> _gone = new();
    static readonly ConcurrentQueue<(uint Id, byte Epoch, WorldCarWire.PoseSample Sample, double Arrival)> _incoming = new();
    static float _nextScan;
    static IntPtr _runner;
    public static int Applied, Captured, StreamPushes, ActiveCars, StreamFresh;

    /// <summary>Network thread (ClientLink): enqueue OTW1 newest state.</summary>
    public static void OnPacket(byte[] d, int len, double arrivalLocal)
    {
        if (d == null || len < WorldCarWire.HeaderSize) return;
        if (!WorldCarWire.TryDecode(d.AsSpan(0, len), out var pkt)) return;
        // Newest-first redundancy — only push S0 (same as OTC3 SyncProbe).
        _incoming.Enqueue((pkt.NetworkId, pkt.Epoch, pkt.S0, arrivalLocal));
    }

    /// <summary>NetFrame.Late, after <see cref="RemoteCarView.AfterFusionRender"/>.</summary>
    public static void Late()
    {
        if (LanState.Active != LanRole.Client)
        {
            if (_cars.Count > 0 || !_incoming.IsEmpty) Clear();
            return;
        }
        var r = LanState.Runner();
        if (r == null || !r.IsRunning)
        {
            Clear();
            return;
        }
        if (r.Pointer != _runner) { Clear(); _runner = r.Pointer; }

        DrainIncoming();

        var wall = Time.realtimeSinceStartup;
        if (wall >= _nextScan) { _nextScan = wall + ScanEveryS; Scan(r); }

        var now = NetClock.Now;
        Applied = Captured = StreamFresh = ActiveCars = 0;
        foreach (var kv in _cars)
        {
            try { TickOne(kv.Key, kv.Value, now); }
            catch { }
        }
        if (_gone.Count > 0)
        {
            foreach (var id in _gone) _cars.Remove(id);
            _gone.Clear();
        }
    }

    static void DrainIncoming()
    {
        while (_incoming.TryDequeue(out var item))
        {
            if (!_cars.TryGetValue(item.Id, out var c))
            {
                // Packet before Scan saw the object — keep a stub so the next Scan fills Unity refs.
                c = new Car();
                _cars[item.Id] = c;
            }
            if (c.StreamEpochArmed && item.Epoch != c.Epoch)
                c.Play.Reset();
            c.Epoch = item.Epoch;
            c.StreamEpochArmed = true;
            var s = item.Sample;
            c.Play.Push(new StatePlayback.Sample
            {
                Seq = s.Seq,
                SendTime = s.SendTime,
                Epoch = item.Epoch,
                Px = s.Px, Py = s.Py, Pz = s.Pz,
                Qx = s.Qx, Qy = s.Qy, Qz = s.Qz, Qw = s.Qw,
                Vx = s.Vx, Vy = s.Vy, Vz = s.Vz,
                Wx = s.Wx, Wy = s.Wy, Wz = s.Wz,
            }, item.Arrival);
            c.LastStreamAt = item.Arrival;
            var speed = MathF.Sqrt(s.Vx * s.Vx + s.Vy * s.Vy + s.Vz * s.Vz);
            var ang = MathF.Sqrt(s.Wx * s.Wx + s.Wy * s.Wy + s.Wz * s.Wz);
            if (!ParkLogic.IsStill(speed, ang)) c.LastMoveAt = item.Arrival;
            StreamPushes++;
        }
    }

    static void Scan(NetworkRunner r)
    {
        foreach (var o in r.GetAllNetworkObjects())
        {
            if (o == null || o.WasCollected) continue;
            if (o.GetComponent<FusionPlayer>() != null) continue;
            var id = o.Id.Raw;
            var vb = o.GetComponent<VehicleBase>();
            var rb = o.GetComponent<Rigidbody>();
            if (vb == null || rb == null) continue;
            if (_cars.TryGetValue(id, out var existing))
            {
                if (existing.O == null)
                {
                    existing.O = o;
                    existing.Rb = rb;
                    existing.Nrb = o.GetComponent<NetworkRigidbody3D>();
                    existing.Root = o.transform;
                }
                continue;
            }
            _cars[id] = new Car
            {
                O = o,
                Rb = rb,
                Nrb = o.GetComponent<NetworkRigidbody3D>(),
                Root = o.transform,
            };
        }
    }

    static void TickOne(uint id, Car c, double now)
    {
        if (c.O == null || c.O.WasCollected || c.Root == null)
        {
            // Stub waiting for Scan, or destroyed.
            if (c.O != null) _gone.Add(id);
            return;
        }
        if (c.Rb == null || c.Rb.WasCollected) { _gone.Add(id); return; }

        var freshStream = c.LastStreamAt >= 0 && (now - c.LastStreamAt) <= FreshStreamS;
        if (freshStream) StreamFresh++;

        if (!freshStream)
        {
            // Fallback: buffer Fusion-rendered pose (sparse) until host stream arrives.
            var p = c.Root.position;
            var q = c.Root.rotation;
            var seq = (uint)Time.frameCount;
            if (seq != c.LastLocalSeq)
            {
                float vx = 0, vy = 0, vz = 0;
                if (c.HasPrev)
                {
                    var dt = now - c.PrevAt;
                    if (dt > 1e-4)
                    {
                        var inv = 1.0 / dt;
                        vx = (float)((p.x - c.PrevP.x) * inv);
                        vy = (float)((p.y - c.PrevP.y) * inv);
                        vz = (float)((p.z - c.PrevP.z) * inv);
                    }
                    if ((p - c.PrevP).sqrMagnitude > SnapM * SnapM)
                    {
                        c.Epoch++;
                        c.Play.Reset();
                    }
                }
                var speed = MathF.Sqrt(vx * vx + vy * vy + vz * vz);
                var ang = 0f;
                try { if (!c.Rb.isKinematic) ang = c.Rb.angularVelocity.magnitude; } catch { }
                if (!ParkLogic.IsStill(speed, ang)) c.LastMoveAt = now;

                c.Play.Push(new StatePlayback.Sample
                {
                    Seq = seq,
                    SendTime = now,
                    Epoch = c.Epoch,
                    Px = p.x, Py = p.y, Pz = p.z,
                    Qx = q.x, Qy = q.y, Qz = q.z, Qw = q.w,
                    Vx = vx, Vy = vy, Vz = vz,
                }, now);
                c.LastLocalSeq = seq;
                c.PrevP = p;
                c.PrevAt = now;
                c.HasPrev = true;
                Captured++;
            }
        }

        var active = c.LastMoveAt >= 0 && (now - c.LastMoveAt) <= HoldActiveS;
        if (!active && !freshStream) return;
        if (!active) return;
        ActiveCars++;

        var sample = c.Play.SampleAt(now);
        if (!sample.Ok || sample.Stale) return;

        c.Root.SetPositionAndRotation(
            new Vector3(sample.Px, sample.Py, sample.Pz),
            new Quaternion(sample.Qx, sample.Qy, sample.Qz, sample.Qw));
        try { if (c.Nrb != null) c.Nrb._rootIsDirtyFromInterpolation = true; } catch { }
        Applied++;
        if (!c.Logged)
        {
            c.Logged = true;
            try { Plugin.L.LogInfo($"[WorldView] smoothing {c.O.gameObject.name.Replace("(Clone)", "")}#{id} (stream)"); }
            catch { Plugin.L.LogInfo($"[WorldView] smoothing #{id}"); }
        }
    }

    static void Clear()
    {
        _cars.Clear();
        while (_incoming.TryDequeue(out _)) { }
        _runner = IntPtr.Zero;
        _nextScan = 0;
        Applied = Captured = StreamPushes = ActiveCars = StreamFresh = 0;
    }

    public static string Describe() =>
        $"worldView cars={_cars.Count} active={ActiveCars} streamFresh={StreamFresh} capture={Captured} apply={Applied} streamPush={StreamPushes}";
}

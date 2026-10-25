using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;
using VehiclePhysics;

namespace OthLan;

/// <summary>
/// Host only: parks driverless cars (broken/abandoned world cars) that stand still out of every player's winch reach.
/// A standing VPP car still rewrites ~180 state words per tick (solver noise), which Fusion must replicate every
/// snapshot: 7 such cars were ~70 % of the host's state changes and pushed each snapshot into two fragments.
/// Parked = kinematic rigidbody + disabled VPP controller, so its networked state stops changing. Decisions:
/// <see cref="ParkLogic"/>. Everything is undone when the session ends or the option is turned off.
/// </summary>
internal static class ParkedCars
{
    sealed class Car
    {
        public NetworkObject O; public Rigidbody Rb; public VehicleBase Vb;
        public float StillSince = -1; public bool Parked;
    }

    const float UpdateEveryS = 0.25f, ScanEveryS = 2f;
    static readonly Dictionary<uint, Car> _cars = new();
    static readonly List<Vector3> _players = new();
    static readonly List<uint> _gone = new();
    static float _nextUpdate, _nextScan, _wakeM = ParkLogic.MinWakeM;
    static IntPtr _runner;
    public static int Parks, Wakes;

    public static bool IsParked(uint id) => _cars.TryGetValue(id, out var c) && c.Parked;

    /// <summary>The parked car nearest to <paramref name="from"/> (self-test and diagnostics).</summary>
    public static Rigidbody NearestParked(Vector3 from, out uint id)
    {
        Rigidbody best = null; id = 0; var bd = float.MaxValue;
        foreach (var kv in _cars)
        {
            var c = kv.Value;
            if (!c.Parked || c.Rb == null || c.Rb.WasCollected) continue;
            var d = Vector3.Distance(from, c.Rb.position);
            if (d < bd) { bd = d; best = c.Rb; id = kv.Key; }
        }
        return best;
    }

    public static int ParkedNow { get { var n = 0; foreach (var c in _cars.Values) if (c.Parked) n++; return n; } }
    public static string Describe() => $"parked={ParkedNow}/{_cars.Count} parks={Parks} wakes={Wakes} wakeM={_wakeM:0}";

    /// <summary>Main thread, every frame (NetFrame).</summary>
    public static void Tick()
    {
        var r = LanState.Active == LanRole.Host && Plugin.ParkIdleCars.Value ? LanState.Runner() : null;
        if (r == null || !r.IsServer) { if (_cars.Count > 0 || _runner != IntPtr.Zero) WakeAll("off"); return; }
        if (r.Pointer != _runner) { WakeAll("new runner"); _runner = r.Pointer; }
        var now = Time.realtimeSinceStartup;
        if (now >= _nextScan) { _nextScan = now + ScanEveryS; Scan(r); }
        if (now < _nextUpdate) return;
        _nextUpdate = now + UpdateEveryS;
        CollectPlayers();
        foreach (var kv in _cars) Update(kv.Key, kv.Value, now);
        foreach (var id in _gone) _cars.Remove(id);
        _gone.Clear();
    }

    static void Scan(NetworkRunner r)
    {
        var reach = 0f;
        foreach (var o in r.GetAllNetworkObjects())
        {
            if (o == null || o.WasCollected) continue;
            var fp = o.GetComponent<FusionPlayer>();
            if (fp != null)
            {
                var w = HostFollower.WinchOf(fp);
                if (w != null) reach = Math.Max(reach, Math.Max(w.MaxWinchRange, w.MaxRopeLength));
                if (o.InputAuthority.IsRealPlayer) continue;
            }
            var id = o.Id.Raw;
            if (_cars.ContainsKey(id)) continue;
            var vb = o.GetComponent<VehicleBase>(); var rb = o.GetComponent<Rigidbody>();
            if (vb == null || rb == null) continue;
            _cars[id] = new Car { O = o, Rb = rb, Vb = vb };
        }
        var wake = ParkLogic.WakeDistance(reach);
        if (wake != _wakeM) Plugin.L.LogInfo($"[Park] longest player winch reach {reach:0.0} m -> wake distance {wake:0} m");
        _wakeM = wake;
    }

    static void CollectPlayers()
    {
        _players.Clear();
        foreach (var c in CarRegistry.Cars)
            if (c.Rb != null && !c.Rb.WasCollected) _players.Add(c.Rb.position);
    }

    static void Update(uint id, Car c, float now)
    {
        if (c.O == null || c.O.WasCollected || c.Rb == null || c.Rb.WasCollected || c.Vb == null || c.Vb.WasCollected)
        { _gone.Add(id); return; }
        var pos = c.Rb.position;
        var nearest = float.PositiveInfinity;
        foreach (var p in _players) nearest = Math.Min(nearest, Vector3.Distance(p, pos));
        if (!c.Parked)
        {
            if (c.Rb.isKinematic) { c.StillSince = -1; return; }   // the game's own kinematic state: not ours to touch
            if (!ParkLogic.IsStill(c.Rb.linearVelocity.magnitude, c.Rb.angularVelocity.magnitude)) c.StillSince = -1;
            else if (c.StillSince < 0) c.StillSince = now;
        }
        var still = c.StillSince < 0 ? 0f : now - c.StillSince;
        switch (ParkLogic.Decide(c.Parked, still, nearest, _wakeM))
        {
            case ParkLogic.Act.Park: Park(c, nearest); break;
            case ParkLogic.Act.Wake: Wake(c, $"player {nearest:0} m"); break;
        }
    }

    static void Park(Car c, float nearest)
    {
        c.Parked = true; Parks++;   // first: a throw below still leaves the car to Wake, which undoes both writes
        c.Rb.isKinematic = true;
        c.Vb.enabled = false;
        Plugin.L.LogInfo($"[Park] parked {Name(c)} (nearest player {nearest:0} m, wake at {_wakeM:0} m)");
    }

    static void Wake(Car c, string why)
    {
        c.Parked = false; c.StillSince = -1; Wakes++;
        try
        {
            if (c.Vb != null && !c.Vb.WasCollected) c.Vb.enabled = true;
            if (c.Rb != null && !c.Rb.WasCollected) { c.Rb.isKinematic = false; c.Rb.WakeUp(); }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[Park] wake {why}: {e.Message}"); }
        Plugin.L.LogInfo($"[Park] woke {Name(c)} ({why})");
    }

    static void WakeAll(string why)
    {
        foreach (var c in _cars.Values) if (c.Parked) Wake(c, why);
        _cars.Clear(); _runner = IntPtr.Zero;
    }

    static string Name(Car c)
    {
        try { return $"{c.O.gameObject.name.Replace("(Clone)", "")}#{c.O.Id.Raw}"; } catch { return "car"; }
    }
}

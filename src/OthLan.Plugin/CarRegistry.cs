using System;
using System.Collections.Generic;
using Fusion;
using Fusion.Addons.Physics;
using UnityEngine;

namespace OthLan;

/// <summary>
/// 0.2.0 T2.2: single owner of the session's FusionPlayer scan (once per second).
/// </summary>
internal readonly struct CarEntry
{
    public readonly FusionPlayer Fp;
    public readonly int Slot;       // InputAuthority.AsIndex, -1 if unknown
    public readonly bool IsOwn;
    public readonly Rigidbody Rb;
    public readonly NetworkRigidbody3D Nrb;

    public CarEntry(FusionPlayer fp, int slot, bool isOwn, Rigidbody rb, NetworkRigidbody3D nrb)
    { Fp = fp; Slot = slot; IsOwn = isOwn; Rb = rb; Nrb = nrb; }
}

internal static class CarRegistry
{
    static readonly List<CarEntry> _cars = new();
    static FusionPlayer[] _players = Array.Empty<FusionPlayer>();
    static float _nextScan;
    static IntPtr _runner;

    public static IReadOnlyList<CarEntry> Cars => _cars;
    /// <summary>Raw array for callers that still iterate FusionPlayer[] (same snapshot as <see cref="Cars"/>).</summary>
    public static FusionPlayer[] Players => _players;

    public static void Reset()
    {
        _cars.Clear();
        _players = Array.Empty<FusionPlayer>();
        _nextScan = 0;
        _runner = IntPtr.Zero;
    }

    /// <summary>Main thread, every frame (before other netcode that needs the list).</summary>
    public static void Tick()
    {
        if (LanState.Active == LanRole.Off || LanState.LanRunner == IntPtr.Zero)
        {
            if (_runner != IntPtr.Zero) Reset();
            return;
        }
        var now = Time.realtimeSinceStartup;
        var force = false;
        try
        {
            var r = OverlandNetworkRunner.Instance?.NetworkRunner;
            if (r == null || r.Pointer != LanState.LanRunner || !r.IsRunning)
            {
                if (_runner != IntPtr.Zero) Reset();
                return;
            }
            if (r.Pointer != _runner) { _runner = r.Pointer; force = true; }
        }
        catch { return; }
        if (!force && now < _nextScan) return;
        _nextScan = now + 1f;
        Rescan();
    }

    /// <summary>Force a rescan now (e.g. Diagnostics once-per-session).</summary>
    public static void RefreshNow() { _nextScan = 0; Rescan(); }

    static void Rescan()
    {
        FusionPlayer[] all;
        try { all = UnityEngine.Object.FindObjectsOfType<FusionPlayer>(); }
        catch { all = Array.Empty<FusionPlayer>(); }
        _cars.Clear();
        if (all == null) all = Array.Empty<FusionPlayer>();
        var kept = new List<FusionPlayer>(all.Length);
        for (var i = 0; i < all.Length; i++)
        {
            var fp = all[i];
            if (fp == null || fp.WasCollected) continue;
            var slot = -1;
            var isOwn = false;
            try
            {
                var o = fp.Object;
                if (o != null)
                {
                    isOwn = o.HasInputAuthority;
                    try { slot = o.InputAuthority.AsIndex; } catch { slot = -1; }
                }
            }
            catch { }
            Rigidbody rb = null;
            NetworkRigidbody3D nrb = null;
            try { rb = fp.rigidbody ?? fp.vehicleBase?.cachedRigidbody; } catch { }
            try { nrb = fp.GetComponent<NetworkRigidbody3D>(); } catch { }
            _cars.Add(new CarEntry(fp, slot, isOwn, rb, nrb));
            kept.Add(fp);
        }
        _players = kept.ToArray();
    }

    public static bool TryGetOwn(out CarEntry e)
    {
        for (var i = 0; i < _cars.Count; i++)
            if (_cars[i].IsOwn) { e = _cars[i]; return true; }
        e = default;
        return false;
    }

    public static bool TryGetBySlot(int slot, out CarEntry e)
    {
        for (var i = 0; i < _cars.Count; i++)
            if (_cars[i].Slot == slot) { e = _cars[i]; return true; }
        e = default;
        return false;
    }
}

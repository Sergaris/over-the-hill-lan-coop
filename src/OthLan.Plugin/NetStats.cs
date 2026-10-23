using System;
using System.Collections.Generic;
using Fusion;
using Fusion.Addons.Physics;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace OthLan;

/// <summary>
/// 0.1.8 network statistics (docs/NETCODE-v2.md 2.7), per LAN session:
///  * own car and remote cars: on-screen smoothness (per-frame deviation from constant velocity, sampled right before
///    rendering), the number the "jerky" complaints are about;
///  * client: gaps between host snapshots (what the UDP echo ping cannot see), Fusion's "input buffer was full" drops,
///    time re-syncs, own-car correction sizes (host state vs ours at each prediction reset).
/// Written to the log every 30 s ([Netcode] StatsToLog) and shown in F7.
/// </summary>
internal static class NetStats
{
    public static readonly Smoothness Own = new(), Remote = new(), OwnWindow = new(), OwnSim = new(), RemoteWindow = new();
    static int _remoteHitchLogs;
    static float _lastRenderTime = -1; static int _hitchLogs, _lastResets, _lastRestores; static Vector3 _lastOwnPos; static bool _hasOwnPos;
    public static readonly GapStats Gaps = new();
    public static int InputDropped, TimeResets, Corrections, CorrOver1m, CorrOver5m; public static double CorrMax, CorrSum;
    static readonly Dictionary<IntPtr, Smoothness> _remote = new();
    static IntPtr _runner; static int _lastServerTick = int.MinValue, _lastTick = int.MinValue; static double _serverTickAt = -1;
    static float _nextLog, _lastSampleAt = -1; static bool _hooked;
    static UnityEngine.Events.UnityAction _cb;
    public static double LastOwnHitch;
    static FusionPlayer[] _players = Array.Empty<FusionPlayer>(); static float _nextScan;

    static void Hook()
    {
        if (_hooked) return; _hooked = true;
        try { _cb = DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction>(new Action(BeforeRender)); Application.add_onBeforeRender(_cb); }
        catch (Exception e) { Plugin.L.LogWarning($"[NetStats] before-render sampling unavailable ({e.Message}); sampling in Update"); _cb = null; }
    }

    /// <summary>Unity log line (any thread is fine: counters only).</summary>
    public static void OnUnityLog(string msg)
    {
        if (msg.Contains("could not add") && msg.Contains("input")) InputDropped++;
    }

    public static void OnOwnCorrection(double err)
    {
        Corrections++; CorrSum += err; if (err > CorrMax) CorrMax = err; if (err > 1) CorrOver1m++; if (err > 5) CorrOver5m++;
    }

    static void ResetAll()
    {
        Own.Reset(); OwnWindow.Reset(); RemoteWindow.Reset(); _remoteHitchLogs = 0; OwnSim.Reset(); _lastRenderTime = -1; _hitchLogs = 0; _nextScan = 0; _hasOwnPos = false; Remote.Reset(); Gaps.Reset(); _remote.Clear();
        InputDropped = TimeResets = Corrections = CorrOver1m = CorrOver5m = 0; CorrMax = CorrSum = 0;
        _lastServerTick = _lastTick = int.MinValue; _serverTickAt = -1; _lastSampleAt = -1;
    }

    static NetworkRunner Runner()
    {
        if (LanState.Active == LanRole.Off || LanState.LanRunner == IntPtr.Zero) return null;
        var r = OverlandNetworkRunner.Instance?.NetworkRunner;
        return r != null && r.Pointer == LanState.LanRunner && r.IsRunning ? r : null;
    }

    /// <summary>Main thread, every frame (Overlay.Update).</summary>
    public static void Tick()
    {
        Hook();
        var r = Runner();
        if (r == null) { if (_runner != IntPtr.Zero) { Log("session end"); _runner = IntPtr.Zero; } return; }
        if (r.Pointer != _runner) { if (_runner != IntPtr.Zero) Log("runner changed"); _runner = r.Pointer; ResetAll(); _nextLog = Time.realtimeSinceStartup + 30f; }
        var now = Time.realtimeSinceStartupAsDouble;
        if (r.IsClient && !r.IsServer)
        {
            try
            {
                var st = r.Simulation.LatestServerTick.Raw;
                if (st != _lastServerTick)
                {
                    if (_serverTickAt > 0) Gaps.Add(now - _serverTickAt);
                    _lastServerTick = st; _serverTickAt = now;
                }
                var tk = r.Tick.Raw;
                if (_lastTick != int.MinValue && tk < _lastTick) TimeResets++;
                _lastTick = tk;
            }
            catch { }
        }
        if (Plugin.NetStatsLog.Value && Time.realtimeSinceStartup >= _nextLog) { _nextLog = Time.realtimeSinceStartup + 30f; Log("30 s"); OwnWindow.Reset(); RemoteWindow.Reset(); NetTrace.Flush(); }
    }

    /// <summary>OnGUI Repaint: runs after the scene was rendered this frame, so the sampled transform is what was on screen.</summary>
    public static void OnRepaint() { if (_cb == null && Event.current != null && Event.current.type == EventType.Repaint) BeforeRender(); }

    static void BeforeRender()
    {
        try
        {
            if (_runner == IntPtr.Zero) return;
            OwnRender.BeforeRender();
            var t = Time.realtimeSinceStartup;
            var dt = _lastSampleAt < 0 ? 0 : t - _lastSampleAt; _lastSampleAt = t;
            if (dt <= 0 || dt > 0.25f) { Own.Break(); OwnWindow.Break(); foreach (var s in _remote.Values) s.Break(); return; }
            // Own scan (1 s): CarSync's list is empty when ping compensation is off.
            if (t >= _nextScan) { _nextScan = t + 1f; try { _players = UnityEngine.Object.FindObjectsOfType<FusionPlayer>(); } catch { _players = Array.Empty<FusionPlayer>(); } }
            foreach (var fp in _players)
            {
                if (fp == null || fp.WasCollected || fp.Object == null) continue;
                var p = Visual(fp);
                if (fp.Object.HasInputAuthority)
                {
                    LastOwnHitch = Own.Add(p.x, p.y, p.z, dt); OwnWindow.Add(p.x, p.y, p.z, dt);
                    // Same samples on Fusion's own render clock: frame pacing (CPU time between Fusion's update and the
                    // render, resimulation cost) is not counted, only motion that is not smooth in simulation time.
                    double simH = 0; float rt = -1, sdt = 0;
                    try { var r = Runner(); if (r != null) rt = r.LocalRenderTime; } catch { }
                    if (rt >= 0 && _lastRenderTime >= 0) { sdt = rt - _lastRenderTime; if (sdt > 1e-4f && sdt < 0.25f) simH = OwnSim.Add(p.x, p.y, p.z, sdt); else OwnSim.Break(); }
                    _lastRenderTime = rt;
                    if (NetTrace.On) { try { var r2 = Runner(); var rb2 = fp.GetComponent<Rigidbody>(); NetTrace.Own("D", r2 != null ? r2.Tick.Raw : 0, 0, 0, 0, rt, p, rb2 != null ? rb2.position : p, rb2 != null ? rb2.linearVelocity : Vector3.zero, LastOwnHitch, simH); } catch { } }
                    if (LastOwnHitch > 0.3 && _hitchLogs < 60 && Plugin.NetStatsLog.Value)
                    {
                        _hitchLogs++;
                        var v = _hasOwnPos ? (p - _lastOwnPos).magnitude / Math.Max(dt, 1e-3f) : 0;
                        string rbd = "-"; try { var rb = fp.GetComponent<Rigidbody>(); if (rb != null) rbd = $"{(rb.position - p).magnitude * 100:0}cm"; } catch { }
                        Plugin.L.LogInfo($"[NetStats] hitch {LastOwnHitch * 100:0}cm (sim clock {simH * 100:0}cm) dt={dt * 1000:0}ms simDt={sdt * 1000:0}ms v={v:0.0}m/s render-rb={rbd} resets+{OwnCar.Resets - _lastResets} restores+{OwnCar.Restores - _lastRestores} sinceReset={(OwnCar.LastResetAt > 0 ? (OwnCar.Now - OwnCar.LastResetAt) * 1000 : -1):0}ms");
                    }
                    _lastResets = OwnCar.Resets; _lastRestores = OwnCar.Restores; _lastOwnPos = p; _hasOwnPos = true;
                }
                else
                {
                    if (!_remote.TryGetValue(fp.Pointer, out var s)) { _remote[fp.Pointer] = s = new Smoothness(); NetTrace.DumpHierarchy(fp); }
                    var f0 = s.Frames;
                    var h = s.Add(p.x, p.y, p.z, dt);
                    if (s.Frames > f0) { Remote.Record(h); RemoteWindow.Record(h); }
                    if (h > 0.5 && _remoteHitchLogs < 80 && Plugin.NetStatsLog.Value) { _remoteHitchLogs++; RemoteHitchLog(fp, p, h, dt); }
                    if (NetTrace.On) TraceRemote(fp, p, h, dt);
                }
            }
        }
        catch { }
    }

    /// <summary>Seconds since the last new host snapshot (client), -1 on the host.</summary>
    public static double SnapshotAge => _serverTickAt > 0 && LanState.Active == LanRole.Client ? Time.realtimeSinceStartupAsDouble - _serverTickAt : -1;

    static void RemoteHitchLog(FusionPlayer fp, Vector3 p, double h, float dt)
    {
        int car = -1; string extra = "";
        try { car = fp.Object.InputAuthority.PlayerId; } catch { }
        try { var rb = fp.rigidbody; if (rb != null) extra = $" v={rb.linearVelocity.magnitude:0.0}m/s render-rb={(rb.position - p).magnitude * 100:0}cm"; } catch { }
        if (LanState.Active == LanRole.Host)
        { try { if (CarSync.LastAction.TryGetValue(car, out var a)) extra += $" carsync={a}"; if (CarSync.Metrics.TryGetValue(car, out var m)) extra += $" err={m.err:0.00}m/{m.errDeg:0}deg age={m.poseAge:0.00}s"; } catch { } }
        else extra += $" snapAge={SnapshotAge * 1000:0}ms smooth={RemoteSmooth.StateOf(fp.Pointer)}";
        Plugin.L.LogInfo($"[NetStats] remote hitch {h * 100:0}cm car={car} dt={dt * 1000:0}ms{extra}");
    }

    static void TraceRemote(FusionPlayer fp, Vector3 p, double h, float dt)
    {
        try
        {
            int car = -1; try { car = fp.Object.InputAuthority.PlayerId; } catch { }
            var rb = fp.rigidbody; var bp = rb != null ? rb.position : Vector3.zero; var sp = rb != null ? rb.linearVelocity.magnitude : 0f;
            int st = 0, tk = 0; float rrt = -1, al = -1;
            try { var r = Runner(); if (r != null) { tk = r.Tick.Raw; if (r.IsClient && !r.IsServer) { st = r.Simulation.LatestServerTick.Raw; rrt = r.RemoteRenderTime; } } } catch { }
            NetTrace.Remote(dt, car, p, bp, h, sp, RemoteSmooth.StateCode(fp.Pointer), RemoteSmooth.OffsetOf(fp.Pointer), SnapshotAge, st, tk, rrt, al);
        }
        catch { }
    }

    static Vector3 Visual(FusionPlayer fp)
    {
        try { var nrb = fp.GetComponent<NetworkRigidbody3D>(); var it = nrb != null ? nrb.InterpolationTarget : null; if (it != null) return it.position; } catch { }
        return fp.transform.position;
    }

    public static string Describe() =>
        $"own car on screen: {Own.Describe()} | own (Fusion clock): {OwnSim.Describe()} | remote cars: {Remote.Describe()}" +
        (LanState.Active == LanRole.Client ? $" | snapshot gaps {Gaps.Describe()} | inputs dropped by Fusion={InputDropped} time re-syncs={TimeResets}" +
            $" | own-car corrections={Corrections} mean={(Corrections > 0 ? CorrSum / Corrections : 0):0.000}m max={CorrMax:0.00}m >1m={CorrOver1m} >5m={CorrOver5m}" : "");

    static void Log(string why)
    {
        if (!Plugin.NetStatsLog.Value) return;
        Plugin.L.LogInfo($"[NetStats] ({why}) {Describe()} | last 30 s own: {OwnWindow.Describe()} | last 30 s remote: {RemoteWindow.Describe()} | {RemoteSmooth.Describe()} | {OwnCar.Describe()} | {OwnRender.Describe()} | {OwnLocal.Describe()}");
    }
}

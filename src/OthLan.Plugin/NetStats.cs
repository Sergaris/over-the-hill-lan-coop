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
    static readonly Dictionary<IntPtr, Vector3> _remoteVisPos = new(); // last rendered root for Δpose speed (T0.5)
    static IntPtr _runner; static int _lastServerTick = int.MinValue, _lastTick = int.MinValue; static double _serverTickAt = -1;
    static float _nextLog, _lastSampleAt = -1; static bool _hooked;
    static UnityEngine.Events.UnityAction _cb;
    public static double LastOwnHitch;
    static FusionPlayer[] _players = Array.Empty<FusionPlayer>();
    // 0.1.9 R2 / 0.2.0 T0.4: wall-clock frame time — ring+percentiles owned by FrameCostLogic
    static readonly double[] _ft = new double[FrameCostLogic.WindowCap]; static int _ftN, _ftI;
    public static int FramesOver25ms, FramesOver50ms;
    static float _lastFtAt = -1;

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
        Own.Reset(); OwnWindow.Reset(); RemoteWindow.Reset(); _remoteHitchLogs = 0; OwnSim.Reset(); _lastRenderTime = -1; _hitchLogs = 0; _hasOwnPos = false; Remote.Reset(); Gaps.Reset(); _remote.Clear(); _remoteVisPos.Clear();
        InputDropped = TimeResets = Corrections = CorrOver1m = CorrOver5m = 0; CorrMax = CorrSum = 0; RemoteBelow = 0;
        _lastServerTick = _lastTick = int.MinValue; _serverTickAt = -1; _lastSampleAt = -1;
        _ftN = _ftI = FramesOver25ms = FramesOver50ms = 0; _lastFtAt = -1;
        FrameCost.ResetAll();
    }

    static void NoteFrameTime()
    {
        var t = Time.realtimeSinceStartup;
        if (_lastFtAt > 0)
        {
            var ms = (t - _lastFtAt) * 1000.0;
            if (FrameCostLogic.TryPush(_ft, ref _ftN, ref _ftI, ms, FrameCostLogic.MaxPlausibleFrameMs))
            {
                if (ms > 25) FramesOver25ms++;
                if (ms > 50) FramesOver50ms++;
            }
        }
        _lastFtAt = t;
    }

    public static string FrameDescribe()
    {
        if (!FrameCostLogic.TryStats(_ft, _ftN, out var p50, out var p99, out var max))
            return "frameMs=-";
        return $"frameMs p50={p50:0.0} p99={p99:0.0} max={max:0.0} >25ms={FramesOver25ms} >50ms={FramesOver50ms}";
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
        NoteFrameTime();
        var r = Runner();
        if (r == null)
        {
            if (_runner != IntPtr.Zero)
            {
                Log("session end");
                _runner = IntPtr.Zero;
                try { SessionLogs.SnapshotNow("session-end"); } catch (Exception e) { Plugin.L.LogWarning($"[Logs] session-end snapshot: {e.Message}"); }
            }
            return;
        }
        if (r.Pointer != _runner)
        {
            if (_runner != IntPtr.Zero) Log("runner changed");
            _runner = r.Pointer;
            ResetAll();
            SessionLogs.NoteSessionStart();
            _nextLog = Time.realtimeSinceStartup + 30f;
        }
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
            if (dt <= 0 || dt > 0.25f) { Own.Break(); OwnWindow.Break(); foreach (var s in _remote.Values) s.Break(); _remoteVisPos.Clear(); _hasOwnPos = false; return; }
            _players = CarRegistry.Players;
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
                        var v = _hasOwnPos
                            ? PoseSpeedLogic.FromDelta(_lastOwnPos.x, _lastOwnPos.y, _lastOwnPos.z, p.x, p.y, p.z, dt)
                            : 0;
                        string rbd = "-"; try { var rb = fp.GetComponent<Rigidbody>(); if (rb != null) rbd = $"{(rb.position - p).magnitude * 100:0}cm"; } catch { }
                        Plugin.L.LogInfo($"[NetStats] hitch {LastOwnHitch * 100:0}cm (sim clock {simH * 100:0}cm) dt={dt * 1000:0}ms simDt={sdt * 1000:0}ms v={v:0.0}m/s render-rb={rbd} resets+{OwnCar.Resets - _lastResets} restores+{OwnCar.Restores - _lastRestores} sinceReset={(OwnCar.LastResetAt > 0 ? (NetClock.Now - OwnCar.LastResetAt) * 1000 : -1):0}ms");
                    }
                    _lastResets = OwnCar.Resets; _lastRestores = OwnCar.Restores; _lastOwnPos = p; _hasOwnPos = true;
                }
                else
                {
                    if (!_remote.TryGetValue(fp.Pointer, out var s)) { _remote[fp.Pointer] = s = new Smoothness(); NetTrace.DumpHierarchy(fp); }
                    var f0 = s.Frames;
                    var h = s.Add(p.x, p.y, p.z, dt);
                    if (s.Frames > f0) { Remote.Record(h); RemoteWindow.Record(h); }
                    var poseV = 0.0;
                    if (_remoteVisPos.TryGetValue(fp.Pointer, out var prevVis))
                        poseV = PoseSpeedLogic.FromDelta(prevVis.x, prevVis.y, prevVis.z, p.x, p.y, p.z, dt);
                    _remoteVisPos[fp.Pointer] = p;
                    if (h > 0.5 && _remoteHitchLogs < 80 && Plugin.NetStatsLog.Value) { _remoteHitchLogs++; RemoteHitchLog(fp, p, h, dt, poseV); }
                    if (NetTrace.On) TraceRemote(fp, p, h, dt, poseV);
                }
            }
        }
        catch { }
    }

    /// <summary>Seconds since the last new host snapshot (client), -1 on the host.</summary>
    public static double SnapshotAge => _serverTickAt > 0 && LanState.Active == LanRole.Client ? Time.realtimeSinceStartupAsDouble - _serverTickAt : -1;
    /// <summary>0.2.0 T1.2: same as <see cref="SnapshotAge"/> — starve signal for RemoteSmoother.</summary>
    public static double ServerTickAge => SnapshotAge;
    /// <summary>Frames where a remote drawn y was &gt;0.3 m below Fusion pose (underground).</summary>
    public static int RemoteBelow;

    static void RemoteHitchLog(FusionPlayer fp, Vector3 p, double h, float dt, double poseSpeedMs)
    {
        int car = -1; string extra = $" v={poseSpeedMs:0.0}m/s";
        try { car = fp.Object.InputAuthority.PlayerId; } catch { }
        try { var rb = fp.rigidbody; if (rb != null) extra += $" render-rb={(rb.position - p).magnitude * 100:0}cm"; } catch { }
        if (LanState.Active == LanRole.Host)
        { try { if (HostFollower.LastAction.TryGetValue(car, out var a)) extra += $" carsync={a}"; if (HostFollower.Metrics.TryGetValue(car, out var m)) extra += $" err={m.err:0.00}m/{m.errDeg:0}deg age={m.poseAge:0.00}s"; } catch { } }
        else extra += $" snapAge={SnapshotAge * 1000:0}ms smooth={RemoteSmooth.StateOf(fp.Pointer)}";
        Plugin.L.LogInfo($"[NetStats] remote hitch {h * 100:0}cm car={car} dt={dt * 1000:0}ms{extra}");
    }

    static void TraceRemote(FusionPlayer fp, Vector3 p, double h, float dt, double poseSpeedMs)
    {
        try
        {
            int car = -1; try { car = fp.Object.InputAuthority.PlayerId; } catch { }
            var rb = fp.rigidbody; var bp = rb != null ? rb.position : Vector3.zero;
            int st = 0, tk = 0; float rrt = -1, al = -1;
            try { var r = Runner(); if (r != null) { tk = r.Tick.Raw; if (r.IsClient && !r.IsServer) { st = r.Simulation.LatestServerTick.Raw; rrt = r.RemoteRenderTime; } } } catch { }
            NetTrace.Remote(dt, car, p, bp, h, (float)poseSpeedMs, RemoteSmooth.StateCode(fp.Pointer), RemoteSmooth.OffsetOf(fp.Pointer), SnapshotAge, st, tk, rrt, al);
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
            $" | own-car corrections={Corrections} mean={(Corrections > 0 ? CorrSum / Corrections : 0):0.000}m max={CorrMax:0.00}m >1m={CorrOver1m} >5m={CorrOver5m} | remoteBelow={RemoteBelow}" : "");

    static void Log(string why)
    {
        if (!Plugin.NetStatsLog.Value) return;
        Plugin.L.LogInfo($"[NetStats] ({why}) {Describe()} | {FrameDescribe()} | {FrameCost.Describe()} | last 30 s own: {OwnWindow.Describe()} | last 30 s remote: {RemoteWindow.Describe()} | {RemoteSmooth.Describe()} | {RemoteWheelVisuals.Describe()} | {OwnCar.Describe()} | {OwnRender.Describe()} | {OwnLocal.Describe()}");
        FramesOver25ms = FramesOver50ms = 0; RemoteBelow = 0;
        RemoteCarView.StreamFrames = RemoteCarView.FusionFrames = RemoteCarView.Snaps = RemoteCarView.StaleFrames = RemoteCarView.ExtrapolatedFrames = RemoteCarView.SourceSwitches = RemoteCarView.RemoteBelow = 0;
        FrameCostLogic.Clear(_ft, ref _ftN, ref _ftI); // 30 s window for frameMs (same owner as cost)
        FrameCost.ResetWindow();
    }
}

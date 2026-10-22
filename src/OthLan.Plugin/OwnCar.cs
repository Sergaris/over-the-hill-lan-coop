using System;
using System.Runtime.InteropServices;
using Fusion;
using Fusion.Addons.Physics;
using UnityEngine;

namespace OthLan;

/// <summary>
/// 0.1.7 client side, own car (ROADMAP 1.5, open item A).
///
/// Vanilla Fusion (ClientPhysicsSimulation=SimulateAlways): on every server snapshot the client resets its own car to the
/// host's state of an older tick (IBeforeClientPredictionReset -> state restore -> IAfterClientPredictionReset) and
/// re-simulates the stored inputs up to its current tick. Any host/client difference is a physics correction.
///
/// Phase 2 (client-authoritative, ON by default since 0.1.7): FusionPlayer.BeforeClientPredictionReset of the own car
/// captures the car's predicted state; at the first FORWARD tick after the re-simulation (FusionPlayer.BeforeTick with
/// runner.IsForward) that state is put back and the NetworkRigidbody's render correction offset is cleared. The car
/// therefore never moves because of a host correction; CarSync makes the host follow the client instead.
/// Safety: a huge or persistent difference (game teleport/rescue/jack on the host, host not converging) or a physics
/// blow-up lets Fusion's reset through (Accept window) and, for blow-ups, falls back to phase 1 for a while.
///
/// Phase 1 (fallback, or when phase 2 is off): after a gap without snapshots the car's CorrectionSmoothingTime and
/// CorrectionSnapDistance are raised for a short time so the inevitable correction is a quick slide, not a jump.
/// </summary>
internal static unsafe class OwnCar
{
    public enum Mode { Off, Measure, Suppress }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void InstFn(IntPtr self, IntPtr mi);
    static InstFn _brOrig, _brHook, _btOrig, _btHook;
    public static bool HooksOk;
    const string K_Br = "FusionPlayer::BeforeClientPredictionReset()";
    const string K_Bt = "FusionPlayer::BeforeTick()";

    // own car (main thread; Fusion ticks run on the main thread too)
    static IntPtr _ownPtr;
    static FusionPlayer _own; static Rigidbody _rb; static NetworkRigidbody3D _nrb; static NetworkRunner _runner;
    static bool _captured; static Vector3 _cp, _cv, _ca; static Quaternion _cq; static int _capTick;
    static float _origSmooth = -1, _origSnap = -1, _nextFind;
    static double _boostUntil = -1;

    // stats (self-test / F7)
    public static int Resets, Restores, Accepts, Fallbacks, Boosts;
    public static double LastErr, LastErrDeg, MaxErr, SumErr; public static int ErrSamples;
    public static double LastResetAt = -1;            // realtime of the last snapshot reset (gap detector)
    public static string State = "off";               // "client" (phase 2) | "soft" (phase 1) | "off"
    static double _acceptUntil = -1, _fallbackUntil = -1, _bigSince = -1;
    public static volatile int AcceptRequests;        // host asked us to take its pose (game teleport on the host)
    public static volatile int OwnSlot = -1;
    static int _lastAcceptCounter = -1;
    /// <summary>Client network thread: host's "OTAC" for our slot (sent twice; counter dedups).</summary>
    public static void OnAccept(byte slot, ushort counter)
    {
        if (slot != OwnSlot || counter == _lastAcceptCounter) return;
        _lastAcceptCounter = counter; AcceptRequests++;
    }
    static int _acceptSeen;
    public static int ErrOver1m, ErrOver5m;

    public static Mode Configured
    {
        get
        {
            if (Override != null) return Override == "off" ? Mode.Off : Override == "measure" ? Mode.Measure : Mode.Suppress;
            return Plugin.OwnCarAuthority.Value ? Mode.Suppress : Mode.Measure;
        }
    }
    /// <summary>Self-test: null = config, "off" (no hooks at all), "measure" (vanilla resets, measured), "suppress".</summary>
    public static string Override;

    public static void Install()
    {
        if (HooksOk) return;
#if SELFTEST
        Override ??= Autopilot.ArgValue("--othlan-own");
#endif
        if (Override == "off") { Plugin.L.LogWarning("[OwnCar] self-test: hooks not installed (--othlan-own off)"); return; }
        var pBr = OptionalHooks.Resolve(typeof(FusionPlayer), "NativeMethodInfoPtr_BeforeClientPredictionReset_Public_Virtual_Final_New_Void_0", K_Br);
        var pBt = OptionalHooks.Resolve(typeof(FusionPlayer), "NativeMethodInfoPtr_BeforeTick_Public_Virtual_Final_New_Void_0", K_Bt);
        if (pBr == IntPtr.Zero || pBt == IntPtr.Zero) { OptionalHooks.Apply<InstFn>(IntPtr.Zero, null, out _, pBr == IntPtr.Zero ? K_Br : K_Bt); return; }
        _brHook = BeforeResetDetour; _btHook = BeforeTickDetour;
        HooksOk = OptionalHooks.Apply(pBr, _brHook, out _brOrig, K_Br) && OptionalHooks.Apply(pBt, _btHook, out _btOrig, K_Bt);
        Plugin.L.LogInfo($"[OwnCar] prediction-reset hooks {(HooksOk ? "installed" : "FAILED")}; mode={Configured}");
    }

    /// <summary>Phase 2 really active right now (client in a LAN session with CarSync on, hooks ok, not in a fallback).</summary>
    public static bool Phase2Active(double now) =>
        HooksOk && Configured == Mode.Suppress && LanState.Active == LanRole.Client && CarSync.HostFlag == 1 && now >= _fallbackUntil
        && (_spawned || _foundAt > 0 && now - _foundAt > SettleSeconds);

    // The game parks a new car (y ~ 1000) and then teleports it to the spawn point. If the spawn is late, the parked
    // car falls and phase 2 would keep (and make the host follow) that fall (seen in self-test r1). So phase 2 starts
    // only after the first big host move (the spawn teleport, > AcceptMeters) or SettleSeconds after the car appeared.
    const double SettleSeconds = 30;
    static bool _spawned; static double _foundAt = -1;
    /// <summary>Client: the game moved our car to its spawn point (spawn map done).</summary>
    public static bool SpawnSeen => _spawned;

    static double Now => Time.realtimeSinceStartupAsDouble;

    /// <summary>Main thread, every frame (Overlay.Update): find the own car, phase 1 boost, state text.</summary>
    public static void Tick()
    {
        if (LanState.Active != LanRole.Client) { Forget(); State = "off"; return; }
        var r = OverlandNetworkRunner.Instance?.NetworkRunner;
        if (r == null || r.Pointer != LanState.LanRunner || !r.IsRunning) { Forget(); State = "off"; return; }
        // link-gap clock for the post-outage rule: side channel down, or our Fusion tick stalled (> 0.3 s)
        LastNoLinkAt();
        try { var tk = r.Tick.Raw; if (tk != _gapTick) { _gapTick = tk; _gapTickAt = Now; } else if (Now - _gapTickAt > 0.3) _noLinkAt = Now; } catch { }
        _runner = r;
        if ((_own == null || _own.Object == null || _ownPtr == IntPtr.Zero) && Time.realtimeSinceStartup >= _nextFind)
        {
            Forget(); _nextFind = Time.realtimeSinceStartup + 1f;
            FusionPlayer[] all; try { all = UnityEngine.Object.FindObjectsOfType<FusionPlayer>(); } catch { all = Array.Empty<FusionPlayer>(); }
            foreach (var fp in all)
                if (fp != null && fp.Object != null && fp.Object.HasInputAuthority)
                {
                    try { OwnSlot = fp.Object.InputAuthority.AsIndex; } catch { OwnSlot = -1; }
                    _own = fp; _ownPtr = fp.Pointer; _rb = fp.rigidbody ?? fp.vehicleBase?.cachedRigidbody;
                    _spawned = false; _foundAt = Now;
                    _nrb = fp.GetComponent<NetworkRigidbody3D>();
                    if (_nrb != null) { _origSmooth = _nrb.CorrectionSmoothingTime; _origSnap = _nrb.CorrectionSnapDistance; }
                    Plugin.L.LogInfo($"[OwnCar] own car found; phase 2 {(Phase2Active(Now) ? "ACTIVE (client-authoritative)" : "inactive")}, smoothing {_origSmooth} s / snap {_origSnap} m");
                    break;
                }
        }
        var now = Now;
        State = _own == null ? "off" : Phase2Active(now) ? "client" : "soft";
        // Phase 1: soften the correction that follows a gap (no snapshot resets for > GapSeconds).
        if (_nrb != null && _origSmooth >= 0)
        {
            var gap = LastResetAt > 0 && now - LastResetAt > Plugin.OwnSoftGapSeconds.Value;
            if (gap && (!Phase2Active(now) || now < _acceptUntil))
            {
                if (_boostUntil < 0) Boosts++;
                _boostUntil = now + Plugin.OwnSoftSeconds.Value;
            }
            if (_boostUntil > 0 && now < _boostUntil)
            {
                _nrb.CorrectionSmoothingTime = Mathf.Max(_origSmooth, Plugin.OwnSoftSmoothing.Value);
                _nrb.CorrectionSnapDistance = Mathf.Max(_origSnap, Plugin.OwnSoftSnapMeters.Value);
            }
            else if (_boostUntil > 0)
            {
                _boostUntil = -1; _nrb.CorrectionSmoothingTime = _origSmooth; _nrb.CorrectionSnapDistance = _origSnap;
            }
        }
    }

    static void Forget()
    {
        if (_nrb != null && _origSmooth >= 0) { try { _nrb.CorrectionSmoothingTime = _origSmooth; _nrb.CorrectionSnapDistance = _origSnap; } catch { } }
        _own = null; _ownPtr = IntPtr.Zero; _rb = null; _nrb = null; _captured = false; _boostUntil = -1; _origSmooth = -1;
    }

    static void BeforeResetDetour(IntPtr self, IntPtr mi)
    {
        try
        {
            if (self != IntPtr.Zero && self == _ownPtr && _rb != null)
            {
                Resets++; LastResetAt = Now;
                _cp = _rb.position; _cq = _rb.rotation; _cv = _rb.linearVelocity; _ca = _rb.angularVelocity;
                _capTick = _runner != null ? _runner.Tick.Raw : 0;
                _captured = true;
            }
        }
        catch { _captured = false; }
        _brOrig(self, mi);
    }

    static void BeforeTickDetour(IntPtr self, IntPtr mi)
    {
        try
        {
            if (_captured && self != IntPtr.Zero && self == _ownPtr && _rb != null && _runner != null && _runner.IsForward)
            {
                _captured = false;
                var now = Now;
                var p = _rb.position; var q = _rb.rotation;
                var err = Vector3.Distance(p, _cp); var errDeg = Quaternion.Angle(q, _cq);
                LastErr = err; LastErrDeg = errDeg; SumErr += err; ErrSamples++; if (err > MaxErr) MaxErr = err;
                if (err > 1) ErrOver1m++; if (err > 5) ErrOver5m++;
                if (!_spawned && err > Plugin.OwnAcceptMeters.Value)
                { _spawned = true; _acceptUntil = now + Plugin.OwnAcceptSeconds.Value; Plugin.L.LogInfo($"[OwnCar] spawn move {err:0} m seen: phase 2 from now on"); }
                if (Phase2Active(now)) Decide(now, err, errDeg);
            }
        }
        catch (Exception e) { _captured = false; Plugin.L.LogWarning($"[OwnCar] {e.Message}"); }
        _btOrig(self, mi);
    }

    static void Decide(double now, double err, double errDeg)
    {
        // Host asked us to take its pose (it moved the car itself: rescue, jack, teleport).
        if (AcceptRequests != _acceptSeen) { _acceptSeen = AcceptRequests; _acceptUntil = now + Plugin.OwnAcceptSeconds.Value; Accepts++; Plugin.L.LogInfo($"[OwnCar] host moved the car: accepting its pose for {Plugin.OwnAcceptSeconds.Value:0.0} s"); }
        if (now < _acceptUntil) return;
        // Physics blow-up on our side: never keep such a state.
        var bad = !float.IsFinite(_cp.x) || !float.IsFinite(_cp.y) || !float.IsFinite(_cp.z) || _cv.magnitude > 150f || _ca.magnitude > 60f;
        if (bad)
        {
            Fallbacks++; _fallbackUntil = now + Plugin.OwnFallbackSeconds.Value;
            Plugin.L.LogWarning($"[OwnCar] own physics state invalid (v {_cv.magnitude:0} m/s): host-authoritative (phase 1) for {Plugin.OwnFallbackSeconds.Value:0} s");
            return;
        }
        // The host turned the car by a lot in one go (reset upright, game teleport): take it (else we would undo the reset).
        // Not right after a link gap: then the host copy turned by itself (repeated input) and is converging back to us.
        if (errDeg > Plugin.OwnAcceptDegrees.Value && now - LastNoLinkAt() < Plugin.OwnPostOutageSeconds.Value) { PostOutageTurnsIgnored++; }
        else if (errDeg > Plugin.OwnAcceptDegrees.Value) { _acceptUntil = now + Plugin.OwnAcceptSeconds.Value; Accepts++; Plugin.L.LogInfo($"[OwnCar] host pose turned {errDeg:0} deg: taking it (reset/teleport)"); return; }
        // Game teleport on the host (huge jump) -> accept at once; host not converging for long -> accept for a moment.
        if (err > Plugin.OwnAcceptMeters.Value) { _acceptUntil = now + Plugin.OwnAcceptSeconds.Value; Accepts++; Plugin.L.LogInfo($"[OwnCar] host pose {err:0.0} m away: taking it (teleport/rescue)"); return; }
        if (err > Plugin.OwnStuckMeters.Value) { if (_bigSince < 0) _bigSince = now; else if (now - _bigSince > Plugin.OwnStuckSeconds.Value) { _bigSince = -1; _acceptUntil = now + Plugin.OwnAcceptSeconds.Value; Accepts++; Plugin.L.LogWarning($"[OwnCar] host stayed {err:0.0} m away for {Plugin.OwnStuckSeconds.Value:0} s: taking its pose"); return; } }
        else _bigSince = -1;
        // Keep our own predicted state.
        _rb.position = _cp; _rb.rotation = _cq; _rb.linearVelocity = _cv; _rb.angularVelocity = _ca;
        _own.transform.SetPositionAndRotation(_cp, _cq);
        if (_nrb != null) { try { _nrb.ResetCorrectionOffset(); } catch { } }
        Restores++;
    }

    public static int PostOutageTurnsIgnored;
    static double _noLinkAt = -1e9, _gapTickAt; static int _gapTick;
    /// <summary>Last moment (OwnCar clock) the side channel to the host was down; a real outage kills both channels.</summary>
    static double LastNoLinkAt()
    {
        try { bool to; lock (ClientLink.Tracker) to = ClientLink.Tracker.Started && ClientLink.Tracker.TimedOut(ClientLink.Now); if (to) _noLinkAt = Now; } catch { }
        return _noLinkAt;
    }

    public static string Describe() =>
        $"state={State} postOutageTurnsIgnored={PostOutageTurnsIgnored} resets={Resets} restores={Restores} accepts={Accepts} fallbacks={Fallbacks} boosts={Boosts} " +
        $"rollback mean={(ErrSamples > 0 ? SumErr / ErrSamples : 0):0.000} m max={MaxErr:0.00} m >1m={ErrOver1m} >5m={ErrOver5m}";
}

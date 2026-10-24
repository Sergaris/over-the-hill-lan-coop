using System;
using System.Runtime.InteropServices;
using Fusion;
using Fusion.Addons.Physics;
using UnityEngine;

namespace OthLan;

/// <summary>
/// 0.1.8 netcode v2 (docs/NETCODE-v2.md): the restore moved from the first forward tick (BeforeTick, too late: Fusion had
/// already copied the host-based state as "previous" for rendering = a visible jump per snapshot) to right after
/// Simulation.Client.RunClientSideResimulationLoop, and now covers Vehicle Physics Pro's own state (VehicleBase networked
/// words 0..338 + RestoreSnapshot) and the NetworkRigidbody3D words, not only the rigidbody. The host pose is taken only
/// on a host-side game move (OTAC), &gt; AcceptMeters (not right after a link gap) or an invalid local state; the 0.1.7
/// "turned &gt; 30 deg" and "stuck 8 m for 3 s" rules are gone (they were the rollbacks the reporter saw). The client's Fusion
/// input buffer is enlarged (InputBufferSeconds) so stalls never drop inputs.
///
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
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void ResimFn(IntPtr self, int ticks, IntPtr mi);
    static ResimFn _rsOrig, _rsHook;
    public static bool HooksOk, ResimHookOk;
    internal static NetworkRunner Runner => _runner;
    internal static Rigidbody Rb => _rb;
    internal static FusionPlayer Own => _own;
    internal static NetworkRigidbody3D Nrb => _nrb;

    /// <summary>A full own-car state (rigidbody, VPP words, NetworkRigidbody words, previous-tick words).</summary>
    internal sealed class Snap
    {
        public bool Ok; public Vector3 P, V, A; public Quaternion Q;
        public int[] Vb = new int[VbWords], Nrb = new int[64], VbPrev = new int[VbWords], NrbPrev = new int[64];
        public int VbN, NrbN, VbPrevN, NrbPrevN;
    }
    internal static void CaptureInto(Snap s)
    {
        s.Ok = false; if (_rb == null || _own == null) return;
        s.P = _rb.position; s.Q = _rb.rotation; s.V = _rb.linearVelocity; s.A = _rb.angularVelocity;
        s.VbN = s.NrbN = s.VbPrevN = s.NrbPrevN = 0;
        if (_vb != null && _vb.Ptr != null && _vb.WordCount >= VbWords) { var src = _vb.Ptr; for (int i = 0; i < VbWords; i++) s.Vb[i] = src[i]; s.VbN = VbWords; }
        if (_nrb != null && _nrb.Ptr != null) { var n = _nrb.WordCount; if (n > s.Nrb.Length) { s.Nrb = new int[n]; s.NrbPrev = new int[n]; } var src = _nrb.Ptr; for (int i = 0; i < n; i++) s.Nrb[i] = src[i]; s.NrbN = n; }
        var pv = s.VbN == VbWords ? PrevPtr(_vb) : null; if (pv != null) { for (int i = 0; i < VbWords; i++) s.VbPrev[i] = pv[i]; s.VbPrevN = VbWords; }
        var pn = s.NrbN > 0 ? PrevPtr(_nrb) : null; if (pn != null) { for (int i = 0; i < s.NrbN; i++) s.NrbPrev[i] = pn[i]; s.NrbPrevN = s.NrbN; }
        s.Ok = true;
    }
    internal static void RestoreFrom(Snap s, bool prevToo)
    {
        if (!s.Ok || _rb == null || _own == null) return;
        if (s.NrbN > 0 && _nrb != null && _nrb.Ptr != null && _nrb.WordCount == s.NrbN) { var d = _nrb.Ptr; for (int i = 0; i < s.NrbN; i++) d[i] = s.Nrb[i]; }
        _rb.position = s.P; _rb.rotation = s.Q; _rb.linearVelocity = s.V; _rb.angularVelocity = s.A;
        _own.transform.SetPositionAndRotation(s.P, s.Q);
        if (s.VbN == VbWords && _vb != null && _vb.Ptr != null) { var d = _vb.Ptr; for (int i = 0; i < VbWords; i++) d[i] = s.Vb[i]; try { _vb.RestoreSnapshot(); } catch { } }
        if (prevToo)
        {
            var pn = s.NrbPrevN > 0 && _nrb != null && _nrb.WordCount == s.NrbPrevN ? PrevPtr(_nrb) : null;
            var pv = s.VbPrevN == VbWords && _vb != null ? PrevPtr(_vb) : null;
            if (pn != null) for (int i = 0; i < s.NrbPrevN; i++) pn[i] = s.NrbPrev[i];
            if (pv != null) for (int i = 0; i < VbWords; i++) pv[i] = s.VbPrev[i];
        }
    }
    internal static bool AcceptingHostPose(double now) => now < _acceptUntil;
    internal static void MarkRootDirty() { try { if (_nrb != null) _nrb._rootIsDirtyFromInterpolation = true; } catch { } }
    const string K_Rs = "Fusion.Simulation+Client::RunClientSideResimulationLoop(int)";
    // VehicleBase (VPP) networked words: m_time .. vehicleSnapshot = [0, 339); words after that belong to subclasses
    // (gameplay state the host owns) and are never touched.
    const int VbWords = 339;
    static int[] _vbBuf = new int[VbWords], _nrbBuf = new int[64];
    // Fusion's render interpolation for a predicted object goes from meta.Previous (tick T-1) to the current words (T).
    // The prediction reset copies the host snapshot into BOTH; Previous is refreshed only by the last forward tick of a
    // frame. In a frame with a resimulation but no forward tick the car was drawn between the host's old pose and ours:
    // one visible hitch per snapshot. So Previous is captured and put back as well.
    static int[] _vbPrev = new int[VbWords], _nrbPrev = new int[64]; static int _vbPrevN, _nrbPrevN;
    public static int PrevRestores, PrevUnavailable;
    static unsafe int* PrevPtr(NetworkBehaviour b)
    {
        if (b == null || b.Ptr == null) return null;
        if (!b.TryGetSnapshotsBuffers(out var from, out var to, out _)) return null;
        // Only the local timeframe has to == the live words; then from is meta.Previous.
        if (to._ptr != (IntPtr)b.Ptr || from._ptr == IntPtr.Zero || from._ptr == to._ptr) return null;
        return (int*)from._ptr;
    }
    static int _vbN, _nrbN; static VehiclePhysics.VehicleBase _vb;
    public static int LateFixes, ResimRestores, WordRestores, VppRestores, InputBufferPatched, AcceptsDistance, AcceptsOtac, DistanceIgnoredAfterGap;
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
        // 0.1.8: restore right after the re-simulation (before forward ticks and the render "previous" copy).
        var pRs = OptionalHooks.Resolve(typeof(Simulation.Client), "NativeMethodInfoPtr_RunClientSideResimulationLoop_Private_Void_Int32_0", K_Rs);
        _rsHook = ResimDetour;
        ResimHookOk = HooksOk && OptionalHooks.Apply(pRs, _rsHook, out _rsOrig, K_Rs);
        Plugin.L.LogInfo($"[OwnCar] prediction-reset hooks {(HooksOk ? "installed" : "FAILED")}, after-resim restore {(ResimHookOk ? "installed" : "unavailable (restore at the first forward tick, 0.1.7 style)")}; mode={Configured}");
    }

    /// <summary>Phase 2 really active right now (client in a LAN session with CarSync on, hooks ok, not in a fallback).
    /// Suspended during contact/winch (R3): Fusion's host-authoritative result is kept so both sides share one collision.</summary>
    public static bool Phase2Active(double now) =>
        HooksOk && Configured == Mode.Suppress && LanState.Active == LanRole.Client && CarSync.HostFlag == 1 && now >= _fallbackUntil
        && !InteractionSuspended
        && (_spawned || _foundAt > 0 && now - _foundAt > SettleSeconds);

    /// <summary>0.2.0 T4.1: OwnLocal + phase 2 paused while InteractionLogic is HostShared (car winch / optional host contact).</summary>
    public static bool InteractionSuspended;
    public static int Suspends, SuspendFrames;
    static InteractionLogic.Mode _interactMode;
    static double _enteredSharedAt, _lastWantAt;
    static float _nextSuspendScan; static FusionPlayer[] _suspendPeers = Array.Empty<FusionPlayer>();
    static InteractionLogic.Cfg InteractCfg()
    {
        var c = new InteractionLogic.Cfg
        {
            ResumeSeconds = Math.Max(1.5, Plugin.SuspendResumeSeconds.Value),
            MinDwellSeconds = Plugin.SharedMinDwellSeconds.Value,
        };
        c.ContactAuthority = string.Equals(Plugin.ContactAuthority.Value, "Host", StringComparison.OrdinalIgnoreCase)
            ? InteractionLogic.Authority.Host : InteractionLogic.Authority.Owner;
        c.WorldWinchAuthority = string.Equals(Plugin.WorldWinchAuthority.Value, "Host", StringComparison.OrdinalIgnoreCase)
            ? InteractionLogic.Authority.Host : InteractionLogic.Authority.Owner;
        // Legacy SuspendOn* = false forces Owner authority for that source.
        if (!Plugin.SuspendOnContact.Value) c.ContactAuthority = InteractionLogic.Authority.Owner;
        if (!Plugin.SuspendOnWinch.Value) { /* car winch still forces shared; world uses Owner */ }
        return c;
    }

    // The game parks a new car (y ~ 1000) and then teleports it to the spawn point. If the spawn is late, the parked
    // car falls and phase 2 would keep (and make the host follow) that fall (seen in self-test r1). So phase 2 starts
    // only after the first big host move (the spawn teleport, > AcceptMeters) or SettleSeconds after the car appeared.
    const double SettleSeconds = 30;
    static bool _spawned; static double _foundAt = -1;
    /// <summary>Client: the game moved our car to its spawn point (spawn map done).</summary>
    public static bool SpawnSeen => _spawned;

    /// <summary>T5.4: alias of <see cref="NetClock.Now"/>.</summary>
    internal static double Now => NetClock.Now;

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
        try { PatchInputBuffer(r); } catch (Exception e) { if (_patchedSim == IntPtr.Zero) Plugin.L.LogWarning($"[OwnCar] input buffer: {e.Message}"); _patchedSim = (IntPtr)1; }
        if ((_own == null || _own.Object == null || _ownPtr == IntPtr.Zero) && Time.realtimeSinceStartup >= _nextFind)
        {
            Forget(); _nextFind = Time.realtimeSinceStartup + 1f;
            if (CarRegistry.TryGetOwn(out var e) && e.Fp != null && e.Fp.Object != null)
            {
                OwnSlot = e.Slot;
                _own = e.Fp; _ownPtr = e.Fp.Pointer; _rb = e.Rb ?? e.Fp.rigidbody ?? e.Fp.vehicleBase?.cachedRigidbody;
                _spawned = false; _foundAt = Now;
                _nrb = e.Nrb ?? e.Fp.GetComponent<NetworkRigidbody3D>();
                OwnRender.Forget(); OwnRender.OwnNrb = _nrb != null ? _nrb.Pointer : IntPtr.Zero;
                _vb = e.Fp.vehicleBase;
                try { Plugin.L.LogInfo($"[OwnCar] networked words: VehicleBase {(_vb != null ? _vb.WordCount : -1)} (restoring {VbWords}), NetworkRigidbody3D {(_nrb != null ? _nrb.WordCount : -1)}"); } catch { }
                if (_nrb != null) { _origSmooth = _nrb.CorrectionSmoothingTime; _origSnap = _nrb.CorrectionSnapDistance; }
                Plugin.L.LogInfo($"[OwnCar] own car found; phase 2 {(Phase2Active(Now) ? "ACTIVE (client-authoritative)" : "inactive")}, smoothing {_origSmooth} s / snap {_origSnap} m");
            }
        }
        var now = Now;
        UpdateSuspend(now);
        State = _own == null ? "off" : InteractionSuspended ? "suspend" : Phase2Active(now) ? "client" : "soft";
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
        OwnRender.Forget(); OwnLocal.Forget();
        if (_nrb != null && _origSmooth >= 0) { try { _nrb.CorrectionSmoothingTime = _origSmooth; _nrb.CorrectionSnapDistance = _origSnap; } catch { } }
        _own = null; _ownPtr = IntPtr.Zero; _rb = null; _nrb = null; _vb = null; _captured = false; _boostUntil = -1; _origSmooth = -1;
        _acceptUntil = -1; _fallbackUntil = -1; _bigSince = -1; _patchedSim = IntPtr.Zero;
        InteractionSuspended = false; _interactMode = InteractionLogic.Mode.Owner; _enteredSharedAt = _lastWantAt = 0; _nextSuspendScan = 0;
    }

    static void BeforeResetDetour(IntPtr self, IntPtr mi)
    {
        try
        {
            if (self != IntPtr.Zero && self == _ownPtr && _rb != null)
            {
                OwnLocal.RestoreSkip();
                OwnRender.BeforeSim(_own.transform, _rb);
                Resets++; LastResetAt = Now;
                _cp = _rb.position; _cq = _rb.rotation; _cv = _rb.linearVelocity; _ca = _rb.angularVelocity;
                _capTick = _runner != null ? _runner.Tick.Raw : 0;
                _vbN = _nrbN = 0;
                try
                {
                    if (_vb != null && _vb.Ptr != null && _vb.WordCount >= VbWords) { var src = _vb.Ptr; for (int i = 0; i < VbWords; i++) _vbBuf[i] = src[i]; _vbN = VbWords; }
                    if (_nrb != null && _nrb.Ptr != null) { var n = _nrb.WordCount; if (n > _nrbBuf.Length) _nrbBuf = new int[n]; var src = _nrb.Ptr; for (int i = 0; i < n; i++) _nrbBuf[i] = src[i]; _nrbN = n; }
                    _vbPrevN = _nrbPrevN = 0;
                    var pv = _vbN == VbWords ? PrevPtr(_vb) : null; if (pv != null) { for (int i = 0; i < VbWords; i++) _vbPrev[i] = pv[i]; _vbPrevN = VbWords; }
                    var pn = _nrbN > 0 ? PrevPtr(_nrb) : null; if (pn != null) { if (_nrbN > _nrbPrev.Length) _nrbPrev = new int[_nrbN]; for (int i = 0; i < _nrbN; i++) _nrbPrev[i] = pn[i]; _nrbPrevN = _nrbN; }
                }
                catch { _vbN = _nrbN = 0; }
                _captured = true;
                if (NetTrace.On) NetTrace.Own("R", _capTick, _capTick, 0, SrvTick(), RenderT(), _cp, _cp, _cv, 0, 0);
            }
        }
        catch { _captured = false; }
        _brOrig(self, mi);
    }

    static void BeforeTickDetour(IntPtr self, IntPtr mi)
    {
        try
        {
            if (self != IntPtr.Zero && self == _ownPtr && _rb != null) OwnRender.BeforeSim(_own.transform, _rb);
            if (_captured && self != IntPtr.Zero && self == _ownPtr && _rb != null && _runner != null && _runner.IsForward)
            { LateFixes++; _lateNow = true; AfterResim(); _lateNow = false; }
            // T3.7: move kinematic remotes before Fusion's Physics.Simulate on the forward tick.
            if (self != IntPtr.Zero && self == _ownPtr && _runner != null && _runner.IsForward && LanState.Active == LanRole.Client)
            {
                var dt = Time.fixedDeltaTime;
                try { if (_runner.DeltaTime > 0) dt = _runner.DeltaTime; } catch { }
                RemoteCarView.PrePhysicsStep(NetClock.Now + dt);
            }
            if (self != IntPtr.Zero && self == _ownPtr && _rb != null && _runner != null && _runner.IsForward) OwnLocal.OnForwardTick();
            if (NetTrace.On && self != IntPtr.Zero && self == _ownPtr && _rb != null && _runner != null)
                NetTrace.Own(_runner.IsForward ? "F" : "f", _runner.Tick.Raw, _capTick, 0, SrvTick(), RenderT(), _rb.position, _rb.position, _rb.linearVelocity, 0, 0);
        }
        catch (Exception e) { _captured = false; Plugin.L.LogWarning($"[OwnCar] {e.Message}"); }
        _btOrig(self, mi);
    }

    static void ResimDetour(IntPtr self, int ticks, IntPtr mi)
    {
        var t0 = _runner != null && NetTrace.On ? _runner.Tick.Raw : 0;
        using (FrameCost.Measure(FrameCost.Resim))
            _rsOrig(self, ticks, mi);
        if (NetTrace.On && _runner != null && _rb != null) NetTrace.Own("S", _runner.Tick.Raw, _capTick, ticks, SrvTick(), RenderT(), _rb.position, _rb.position, _rb.linearVelocity, t0, _captured ? 1 : 0);
        try { if (_captured && _rb != null && _runner != null) { ResimRestores++; AfterResim(); } }
        catch (Exception e) { _captured = false; Plugin.L.LogWarning($"[OwnCar] after resim: {e.Message}"); }
    }

    /// <summary>The re-simulation from the host's state is done: compare, then keep our own state (or take the host's).</summary>
    static void AfterResim()
    {
        {
            {
                _captured = false;
                var now = Now;
                var p = _rb.position; var q = _rb.rotation;
                var err = Vector3.Distance(p, _cp); var errDeg = Quaternion.Angle(q, _cq);
                LastErr = err; LastErrDeg = errDeg; SumErr += err; ErrSamples++; if (err > MaxErr) MaxErr = err;
                if (err > 1) ErrOver1m++; if (err > 5) ErrOver5m++;
                if (!_spawned && err > Plugin.OwnAcceptMeters.Value)
                { _spawned = true; _acceptUntil = now + Plugin.OwnAcceptSeconds.Value; Plugin.L.LogInfo($"[OwnCar] spawn move {err:0} m seen: phase 2 from now on"); try { CarStreamSender.BumpEpoch("spawn move"); } catch { } }
                if (Phase2Active(now)) Decide(now, err, errDeg);
                NetStats.OnOwnCorrection(err);
            }
        }
    }

    static void Decide(double now, double err, double errDeg)
    {
        // Host asked us to take its pose (the game moved the car on the host: respawn, rescue, jack, reset upright).
        if (AcceptRequests != _acceptSeen) { _acceptSeen = AcceptRequests; _acceptUntil = now + Plugin.OwnAcceptSeconds.Value; Accepts++; AcceptsOtac++; Plugin.L.LogInfo($"[OwnCar] host moved the car: accepting its pose for {Plugin.OwnAcceptSeconds.Value:0.0} s"); try { CarStreamSender.BumpEpoch("host moved"); } catch { } }
        if (now < _acceptUntil) return;
        // Physics blow-up on our side: never keep such a state.
        var bad = !float.IsFinite(_cp.x) || !float.IsFinite(_cp.y) || !float.IsFinite(_cp.z) || _cv.magnitude > 150f || _ca.magnitude > 60f;
        if (bad)
        {
            Fallbacks++; _fallbackUntil = now + Plugin.OwnFallbackSeconds.Value;
            Plugin.L.LogWarning($"[OwnCar] own physics state invalid (v {_cv.magnitude:0} m/s): host-authoritative (phase 1) for {Plugin.OwnFallbackSeconds.Value:0} s");
            return;
        }
        // Very far apart: a game teleport whose OTAC was lost. Not right after a link gap: then the host copy is simply
        // behind (it braked / extrapolated) and converges to us; taking its pose would be exactly the rollback we avoid.
        if (OwnCarLogic.TakeHostPose(err, now - LastNoLinkAt(), Plugin.OwnAcceptMeters.Value, Plugin.OwnPostOutageSeconds.Value))
        { _acceptUntil = now + Plugin.OwnAcceptSeconds.Value; Accepts++; AcceptsDistance++; Plugin.L.LogInfo($"[OwnCar] host pose {err:0.0} m away: taking it (teleport/rescue)"); return; }
        if (err > Plugin.OwnAcceptMeters.Value) DistanceIgnoredAfterGap++;
        Restore();
    }

    /// <summary>Put our own (pre-reset) state back: rigidbody + transform, NetworkRigidbody3D words, VPP state.</summary>
    static bool _lateNow;
    static int SrvTick() { try { return _runner != null ? _runner.Simulation.LatestServerTick.Raw : 0; } catch { return 0; } }
    static float RenderT() { try { return _runner != null ? _runner.LocalRenderTime : -1; } catch { return -1; } }

    static void Restore()
    {
        if (NetTrace.On && _runner != null) NetTrace.Own(_lateNow ? "L" : "P", _runner.Tick.Raw, _capTick, 0, SrvTick(), RenderT(), _cp, _rb.position, _cv, Vector3.Distance(_rb.position, _cp), 0);
        if (_nrbN > 0 && _nrb != null && _nrb.Ptr != null && _nrb.WordCount == _nrbN) { var d = _nrb.Ptr; for (int i = 0; i < _nrbN; i++) d[i] = _nrbBuf[i]; WordRestores++; }
        _rb.position = _cp; _rb.rotation = _cq; _rb.linearVelocity = _cv; _rb.angularVelocity = _ca;
        _own.transform.SetPositionAndRotation(_cp, _cq);
        if (_vbN == VbWords && _vb != null && _vb.Ptr != null)
        {
            var d = _vb.Ptr; for (int i = 0; i < VbWords; i++) d[i] = _vbBuf[i];
            try { _vb.RestoreSnapshot(); VppRestores++; } catch (Exception e) { if (VppRestores == 0) Plugin.L.LogWarning($"[OwnCar] VPP restore: {e.Message}"); }
        }
        // Previous-tick words (render "from"), see _vbPrev.
        try
        {
            var pn = _nrbPrevN > 0 && _nrb != null && _nrb.WordCount == _nrbPrevN ? PrevPtr(_nrb) : null;
            var pv = _vbPrevN == VbWords && _vb != null ? PrevPtr(_vb) : null;
            if (pn != null) { for (int i = 0; i < _nrbPrevN; i++) pn[i] = _nrbPrev[i]; PrevRestores++; } else PrevUnavailable++;
            if (pv != null) for (int i = 0; i < VbWords; i++) pv[i] = _vbPrev[i];
        }
        catch { PrevUnavailable++; }
        if (_nrb != null) { try { _nrb.ResetCorrectionOffset(); } catch { } }
        Restores++;
    }

    // ---- input buffer (0.1.8): Fusion keeps unconfirmed inputs in a buffer of TickRate entries (1 s). A host-to-client
    // stall of ~0.7 s at 200 ms ping filled it and Fusion dropped our inputs (Player.log "buffer was full").
    static IntPtr _patchedSim;
    public static int InputCapacity;
    static void PatchInputBuffer(NetworkRunner r)
    {
        var secs = Plugin.InputBufferSeconds.Value;
        if (secs <= 0) return;
        var sim = r.Simulation; if (sim == null || sim.Pointer == _patchedSim) return;
        _patchedSim = sim.Pointer;
        var cl = sim.TryCast<Simulation.Client>(); if (cl == null) return;
        var buf = cl._inputBuffer; if (buf == null) return;
        var want = OwnCarLogic.InputCapacity(secs, r.TickRate, buf._capacity);
        if (want > buf._capacity)
        {
            var arr = cl._inputArray;
            if (arr == null || arr.Length < want + 8) cl._inputArray = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<SimulationInput>(want + 8);
            var old = buf._capacity; buf._capacity = want; InputCapacity = want; InputBufferPatched++;
            Plugin.L.LogInfo($"[OwnCar] Fusion input buffer {old} -> {want} ticks ({secs:0.#} s at {r.TickRate} Hz): stalls no longer drop inputs");
        }
    }

    public static int PostOutageTurnsIgnored;
    static double _noLinkAt = -1e9, _gapTickAt; static int _gapTick;
    /// <summary>Last moment (OwnCar clock) the side channel to the host was down; a real outage kills both channels.</summary>
    static double LastNoLinkAt()
    {
        try { bool to; lock (ClientLink.Tracker) to = ClientLink.Tracker.Started && ClientLink.Tracker.TimedOut(ClientLink.Now); if (to) _noLinkAt = Now; } catch { }
        return _noLinkAt;
    }

    static void UpdateSuspend(double now)
    {
        if (_own == null || _rb == null)
        { InteractionSuspended = false; _interactMode = InteractionLogic.Mode.Owner; return; }
        if (Time.realtimeSinceStartup >= _nextSuspendScan)
        {
            _nextSuspendScan = Time.realtimeSinceStartup + 0.5f;
            _suspendPeers = CarRegistry.Players;
        }
        bool localC = false;
        var winchLocal = WinchKindLogic.Kind.None;
        var winchTag = "none";
        try
        {
            if (Plugin.SuspendOnWinch.Value)
            {
                PlayerRef pref = default;
                try { pref = _own.Object.InputAuthority; } catch { }
                winchLocal = HostFollower.WinchKindOf(_own, pref, out var wSlot);
                winchTag = WinchKindLogic.LogTag(winchLocal, wSlot);
            }
            if (Plugin.SuspendOnContact.Value && winchLocal == WinchKindLogic.Kind.None)
            {
                var lookAhead = 0.15 + (double.IsNaN(PingHud.SmoothedMs) ? 0.1 : PingHud.SmoothedMs / 2000.0);
                var inv = Quaternion.Inverse(_rb.rotation);
                var box = CarSync.BoxOf(_own); // x width, y height, z length; cfg only as BoxOf's fallback
                foreach (var o in _suspendPeers)
                {
                    if (o == null || o == _own || o.Object == null || o.Object.HasInputAuthority) continue;
                    var orb = o.rigidbody ?? o.vehicleBase?.cachedRigidbody; if (orb == null) continue;
                    var d = inv * (orb.position - _rb.position);
                    var rel = (orb.linearVelocity - _rb.linearVelocity).magnitude;
                    var m = InteractionLogic.PredictiveMargin(Plugin.CarContactMargin.Value, rel, lookAhead);
                    if (CarSyncLogic.InContact(d.x, d.y, d.z, box.x, box.z, box.y, m)) { localC = true; break; }
                }
            }
        }
        catch { }
        // Host winch flag has no Car/World split yet — treat as Car (forces HostShared).
        var winchHost = CarSync.HostSuspendWinch ? WinchKindLogic.Kind.Car : WinchKindLogic.Kind.None;
        var cfg = InteractCfg();
        cfg.ResumeSeconds = InteractionLogic.ResumeDelay(cfg, PingHud.SmoothedMs, 0.1);
        var want = InteractionLogic.WantShared(localC, CarSync.HostSuspendContact, winchLocal, winchHost, cfg);
        var was = InteractionSuspended;
        _interactMode = InteractionLogic.Step(_interactMode, now, ref _enteredSharedAt, ref _lastWantAt, want, cfg);
        InteractionSuspended = _interactMode == InteractionLogic.Mode.HostShared;
        if (InteractionSuspended) SuspendFrames++;
        if (InteractionSuspended && !was)
        {
            Suspends++;
            try { OwnLocal.RestoreSkip(); OwnLocal.Forget(); OwnRender.Forget(); } catch { }
            Plugin.L.LogInfo($"[OwnCar] HostShared (winch={winchTag} contact={localC}/{CarSync.HostSuspendContact})");
        }
        else if (!InteractionSuspended && was)
        {
            try { OwnLocal.Forget(); OwnRender.Forget(); } catch { }
            Plugin.L.LogInfo("[OwnCar] Owner mode after HostShared");
        }
    }

    public static string Describe() =>
        $"state={State} resets={Resets} restores={Restores} (afterResim={ResimRestores} late={LateFixes} words={WordRestores} vpp={VppRestores} prev={PrevRestores} prevNA={PrevUnavailable}) accepts={Accepts} (otac={AcceptsOtac} dist={AcceptsDistance} distIgnoredAfterGap={DistanceIgnoredAfterGap}) fallbacks={Fallbacks} boosts={Boosts} inputCap={InputCapacity} " +
        $"suspends={Suspends}/{SuspendFrames} " +
        $"rollback mean={(ErrSamples > 0 ? SumErr / ErrSamples : 0):0.000} m max={MaxErr:0.00} m >1m={ErrOver1m} >5m={ErrOver5m}";
}

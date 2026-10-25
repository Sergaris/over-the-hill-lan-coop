using System;
using System.Collections.Generic;
using Fusion;
using Fusion.Addons.Physics;
using UnityEngine;

namespace OthLan;

/// <summary>
/// 0.2.0 T3.5: where to draw a remote car — OTC3 StatePlayback when fresh, else Fusion + RemoteSmoother.
/// Owner of remote root pose (render + kinematic body for T3.7). Decay formula stays in RemoteSmoother.
/// </summary>
internal static class RemoteCarView
{
    const double FreshAgeS = 0.5;
    const float SourceBlendTau = 0.1f; // ~95% settle in 0.3 s

    enum Src : byte { None, Stream, Fusion }

    sealed class Slot
    {
        public Src Last = Src.None;
        public double Ox, Oy, Oz;
        public Quaternion Oq = Quaternion.identity;
        public Vector3 LastDrawnP;
        public Quaternion LastDrawnQ = Quaternion.identity;
        public bool HasDrawn;
        public float LastT = -1;
        public bool ProxyEnsured;
        public StatePlayback.PlaybackResult LastSample;
        public bool HasSample;
        /// <summary>Last pose written for physics (delay 0). Visual LateUpdate overwrites the same root transform.</summary>
        public Vector3 PhysP;
        public Quaternion PhysQ = Quaternion.identity;
        public bool HasPhys;
        public bool ServoPending; // PrePhysics set velocities; PostPhysicsStep stores pose after Simulate
        public double PullSince = -1; // proxyPull open timer (AcceptMetricsLogic)
    }

    static readonly Dictionary<int, Slot> _slots = new();
    static readonly RemoteSmoother.Cfg _blendCfg = new()
    {
        Tau = SourceBlendTau, TauLarge = SourceBlendTau, MaxCorrection = 50, SpeedFactor = 0,
        SmallMeters = 0, LargeMeters = 1
    };

    public static int StreamFrames, FusionFrames, Snaps, StaleFrames, ExtrapolatedFrames, SourceSwitches, RemoteBelow;
    public static int ProxiesTuned, Interpolated;
    /// <summary>§5.2: max extrapErr (m) and max completed proxyPull (s) in the NetStats window.</summary>
    public static double ExtrapErrMax, ProxyPullMax, ProxyPullOpenMaxAge;
    public static int ExtrapErrSamples, ProxyPullSamples, ProxyPullOpen, ProxyPullAborted;

    static readonly List<IntPtr> _interpolated = new();

    public static void Reset()
    {
        _slots.Clear();
        _interpolated.Clear();
        StreamFrames = FusionFrames = Snaps = StaleFrames = ExtrapolatedFrames = SourceSwitches = RemoteBelow = 0;
        ProxiesTuned = Interpolated = 0;
        ClearAcceptWindow();
    }

    /// <summary>NetStats 30 s window: clear accept counters + open pull timers (do not span windows).</summary>
    public static void ClearAcceptWindow()
    {
        ExtrapErrMax = ProxyPullMax = ProxyPullOpenMaxAge = 0;
        ExtrapErrSamples = ProxyPullSamples = ProxyPullOpen = ProxyPullAborted = 0;
        foreach (var sl in _slots.Values) sl.PullSince = -1;
    }

    public static string Describe()
    {
        ScanOpenPulls(NetClock.Now);
        return $"remoteView stream={StreamFrames} fusion={FusionFrames} stale={StaleFrames} extrap={ExtrapolatedFrames} snaps={Snaps} switches={SourceSwitches} remoteBelow={RemoteBelow} proxies={ProxiesTuned} interp={Interpolated} extrapErrMax={ExtrapErrMax:0.00}m n={ExtrapErrSamples} proxyPullMax={ProxyPullMax:0.00}s done={ProxyPullSamples} open={ProxyPullOpen} openAge={ProxyPullOpenMaxAge:0.00}s abort={ProxyPullAborted}";
    }

    static void ScanOpenPulls(double now)
    {
        ProxyPullOpen = 0; ProxyPullOpenMaxAge = 0;
        foreach (var sl in _slots.Values)
        {
            var age = AcceptMetricsLogic.ProxyPullOpenAge(sl.PullSince, now);
            if (double.IsNaN(age)) continue;
            ProxyPullOpen++;
            if (age > ProxyPullOpenMaxAge) ProxyPullOpenMaxAge = age;
        }
    }

    static void AbortPull(Slot sl, double now)
    {
        var age = AcceptMetricsLogic.ProxyPullAbort(ref sl.PullSince, now);
        if (!double.IsNaN(age)) ProxyPullAborted++;
    }

    /// <summary>True when this slot has a non-stale stream sample younger than FreshAgeS (RemoteSmooth must not fight it).</summary>
    public static bool HasFreshStream(int slot, double nowLocal)
    {
        // Do not call SampleAt here — it consumes pending Snap. Age + buffer is enough for "don't fight stream".
        if (slot < 0) return false;
        var pb = CarRelay.PlaybackOf(slot);
        if (pb.Count == 0) return false;
        return nowLocal - pb.LastArrivalLocal <= FreshAgeS;
    }

    /// <summary>Last SampleAt for a slot (for RemoteWheelVisuals). Valid after AfterFusionRender in the same frame.</summary>
    public static bool TryLastSample(int slot, out StatePlayback.PlaybackResult sample)
    {
        if (_slots.TryGetValue(slot, out var s) && s.HasSample && s.Last == Src.Stream)
        {
            sample = s.LastSample;
            return true;
        }
        sample = default;
        return false;
    }

    /// <summary>TuneProxy moved here (T3.5): non-simulated + kinematic when RemoteInterpolate.</summary>
    public static void EnsureProxyMode(FusionPlayer fp, NetworkRigidbody3D nrb)
    {
        if (fp == null || nrb == null) return;
        try
        {
            float s0 = nrb.CorrectionSmoothingTime, d0 = nrb.CorrectionSnapDistance, a0 = nrb.CorrectionSnapAngle;
            if (Plugin.RemoteProxySmoothing.Value > 0) nrb.CorrectionSmoothingTime = Mathf.Max(s0, Plugin.RemoteProxySmoothing.Value);
            if (Plugin.RemoteProxySnapMeters.Value > 0) nrb.CorrectionSnapDistance = Mathf.Max(d0, Plugin.RemoteProxySnapMeters.Value);
            if (Plugin.RemoteProxySnapDegrees.Value > 0) nrb.CorrectionSnapAngle = Mathf.Max(a0, Plugin.RemoteProxySnapDegrees.Value);
            ProxiesTuned++;
            ApplyInterpolate(fp, Plugin.RemoteInterpolate.Value);
            if (Plugin.RemoteInterpolate.Value) RemoteWheelVisuals.ApplyLightsNow(fp);
            Plugin.L.LogInfo($"[RemoteView] {fp.gameObject.name}: proxy correction {s0:0.##}s/{d0:0.#}m/{a0:0}deg -> {nrb.CorrectionSmoothingTime:0.##}s/{nrb.CorrectionSnapDistance:0.#}m/{nrb.CorrectionSnapAngle:0}deg");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[RemoteView] proxy tune: {e.Message}"); }
    }

    static void ApplyInterpolate(FusionPlayer fp, bool on)
    {
        try
        {
            if (fp?.Runner == null || fp.Object == null) return;
            // Host: HostFollower owns the proxy rigidbody — keep simulated + dynamic (boot023: Draw+kinematic fought follow).
            if (LanState.Active == LanRole.Host)
            {
                var okH = fp.Runner.SetIsSimulated(fp.Object, true);
                var rbH = fp.rigidbody ?? fp.vehicleBase?.cachedRigidbody;
                if (rbH != null) rbH.isKinematic = false;
                _interpolated.Remove(fp.Pointer);
                Plugin.L.LogInfo($"[RemoteView] {fp.gameObject.name}: host follow (simulated) {(okH ? "ok" : "refused")}");
                return;
            }
            var ok = fp.Runner.SetIsSimulated(fp.Object, !on);
            var rb = fp.rigidbody ?? fp.vehicleBase?.cachedRigidbody;
            // T4.0: Servo keeps a dynamic body so contacts exchange impulse; Kinematic (default) matches RemoteInterpolate.
            var servo = string.Equals(Plugin.ProxyBody?.Value, "Servo", StringComparison.OrdinalIgnoreCase);
            if (rb != null) rb.isKinematic = on && !servo;
            if (on)
            {
                if (!_interpolated.Contains(fp.Pointer)) { _interpolated.Add(fp.Pointer); Interpolated++; }
            }
            else _interpolated.Remove(fp.Pointer);
            Plugin.L.LogInfo($"[RemoteView] {fp.gameObject.name}: {(on ? "interpolated (not simulated)" : "simulated again")} {(ok ? "ok" : "refused")}");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[RemoteView] interpolate: {e.Message}"); }
    }

    /// <summary>F7 live toggle (was RemoteSmooth.SetInterpolate).</summary>
    public static void SetInterpolate(bool on, string why)
    {
        if (Plugin.RemoteInterpolate.Value == on) return;
        Plugin.RemoteInterpolate.Value = on;
        Plugin.L.LogInfo($"[RemoteView] RemoteInterpolate {(on ? "on" : "off")} ({why})");
        foreach (var fp in CarRegistry.Players)
        {
            try
            {
                if (fp == null || fp.WasCollected || fp.Object == null || fp.Object.HasInputAuthority) continue;
                ApplyInterpolate(fp, on);
                if (on) RemoteWheelVisuals.ApplyLightsNow(fp);
            }
            catch (Exception e) { Plugin.L.LogWarning($"[RemoteView] retune: {e.Message}"); }
        }
        if (!on) RemoteWheelVisuals.Reset();
    }

    /// <summary>After NetworkRigidbody.Render (NetFrame.Late). Stream pose wins when fresh; else leave Fusion pose.</summary>
    public static void AfterFusionRender()
    {
        if (LanState.Active == LanRole.Off) return;
        using var _cost = FrameCost.Measure(FrameCost.RemoteView);
        var now = NetClock.Now;
        var t = Time.realtimeSinceStartup;
        foreach (var e in CarRegistry.Cars)
        {
            try { DrawOne(e, now, t); }
            catch (Exception ex)
            {
                if (StreamFrames + FusionFrames < 4) Plugin.L.LogWarning($"[RemoteView] {ex.Message}");
            }
        }
    }

    static void DrawOne(CarEntry e, double now, float t)
    {
        if (e.IsOwn || e.Fp == null || e.Slot < 0) return;
        var root = e.Fp.transform;
        if (root == null) return;
        if (!_slots.TryGetValue(e.Slot, out var sl)) _slots[e.Slot] = sl = new Slot();
        var dt = sl.LastT < 0 ? 0 : t - sl.LastT;
        sl.LastT = t;

        if (!sl.ProxyEnsured)
        {
            var nrb = e.Fp.GetComponent<NetworkRigidbody3D>();
            if (nrb != null) EnsureProxyMode(e.Fp, nrb);
            sl.ProxyEnsured = true;
        }

        // Host: HostFollower owns root+rb. Writing OTC3 here teleported proxies to spawn (~25 m hitch in boot023).
        // Still SampleAt(now) without delayOverride so epoch Snap is consumed (physics SampleAt must not).
        if (LanState.Active == LanRole.Host)
        {
            var pbH = CarRelay.PlaybackOf(e.Slot);
            if (pbH.Count > 0) pbH.SampleAt(now);
            sl.LastDrawnP = root.position;
            sl.LastDrawnQ = root.rotation;
            sl.HasDrawn = true;
            sl.HasPhys = true;
            sl.PhysP = root.position;
            sl.PhysQ = root.rotation;
            return;
        }

        var pb = CarRelay.PlaybackOf(e.Slot);
        var fresh = pb.Count > 0 && (now - pb.LastArrivalLocal) <= FreshAgeS;
        var r = fresh ? pb.SampleAt(now) : default;
        var stream = fresh && r.Ok && !r.Stale;
        if (fresh && r.Ok && r.Stale) StaleFrames++;
        NoteExtrapErr(pb, stream, r);

        var src = stream ? Src.Stream : Src.Fusion;
        ResolveTarget(root, sl, stream, r, out var targetP, out var targetQ);
        BlendFromSource(sl, src, stream, r, targetP, targetQ, dt);

        var drawnP = new Vector3(targetP.x + (float)sl.Ox, targetP.y + (float)sl.Oy, targetP.z + (float)sl.Oz);
        var drawnQ = sl.Oq * targetQ;
        if (drawnP.y < targetP.y - 0.3f) RemoteBelow++;

        // Visual root only — kinematic body is owned by PrePhysicsStep (delay 0).
        if (stream || Math.Abs(sl.Ox) + Math.Abs(sl.Oy) + Math.Abs(sl.Oz) > 1e-4 || Quaternion.Angle(sl.Oq, Quaternion.identity) > 1e-2f)
            root.SetPositionAndRotation(drawnP, drawnQ);

        sl.LastDrawnP = drawnP;
        sl.LastDrawnQ = drawnQ;
        sl.HasDrawn = true;
    }

    static void NoteExtrapErr(StatePlayback pb, bool stream, in StatePlayback.PlaybackResult r)
    {
        if (!(stream && r.Extrapolated)) return;
        ExtrapolatedFrames++;
        if (!pb.TryNewest(out var newest)) return;
        var ee = AcceptMetricsLogic.ExtrapErrM(r.Px, r.Py, r.Pz, newest.Px, newest.Py, newest.Pz);
        ExtrapErrSamples++;
        if (ee > ExtrapErrMax) ExtrapErrMax = ee;
    }

    static void ResolveTarget(Transform root, Slot sl, bool stream, in StatePlayback.PlaybackResult r,
        out Vector3 targetP, out Quaternion targetQ)
    {
        if (stream)
        {
            targetP = new Vector3(r.Px, r.Py, r.Pz);
            targetQ = new Quaternion(r.Qx, r.Qy, r.Qz, r.Qw);
            sl.LastSample = r;
            sl.HasSample = true;
            if (r.Snap) Snaps++;
            StreamFrames++;
            return;
        }
        targetP = root.position;
        targetQ = root.rotation;
        sl.HasSample = false;
        sl.HasPhys = false;
        sl.ServoPending = false;
        FusionFrames++;
    }

    static void BlendFromSource(Slot sl, Src src, bool stream, in StatePlayback.PlaybackResult r,
        Vector3 targetP, Quaternion targetQ, float dt)
    {
        if (src != sl.Last && sl.Last != Src.None && sl.HasDrawn && !(stream && r.Snap))
        {
            sl.Ox = sl.LastDrawnP.x - targetP.x;
            sl.Oy = sl.LastDrawnP.y - targetP.y;
            sl.Oz = sl.LastDrawnP.z - targetP.z;
            sl.Oq = sl.LastDrawnQ * Quaternion.Inverse(targetQ);
            SourceSwitches++;
        }
        if (stream && r.Snap) { sl.Ox = sl.Oy = sl.Oz = 0; sl.Oq = Quaternion.identity; }
        sl.Last = src;

        if (dt > 0 && dt <= 0.25f)
            RemoteSmoother.DecayOffset(ref sl.Ox, ref sl.Oy, ref sl.Oz, dt, _blendCfg);
        var keepQ = (float)RemoteSmoother.RotKeep(Quaternion.Angle(sl.Oq, Quaternion.identity), dt > 0 ? dt : 1 / 60.0, SourceBlendTau, 180);
        sl.Oq = Quaternion.Slerp(Quaternion.identity, sl.Oq, keepQ);
    }

    /// <summary>T3.7 / T4.0: before Physics.Simulate — remote bodies at SampleAt(stepEnd, delayOverride:0).</summary>
    public static void PrePhysicsStep(double stepEndLocal)
    {
        if (LanState.Active != LanRole.Client) return;
        // Multiple Fusion forward ticks per frame: previous tick already Simulated, but Late/PostPhysics
        // has not run yet. Flush ServoPending from the live rb so Undo does not rewind that step.
        PostPhysicsStep();
        var servo = string.Equals(Plugin.ProxyBody?.Value, "Servo", StringComparison.OrdinalIgnoreCase);
        var tau = Math.Max(0.02, Plugin.ServoSeconds?.Value ?? 0.1f);
        foreach (var e in CarRegistry.Cars)
        {
            try
            {
                if (e.IsOwn || e.Rb == null || e.Slot < 0) continue;
                if (!_slots.TryGetValue(e.Slot, out var sl)) _slots[e.Slot] = sl = new Slot();
                if (!HasFreshStream(e.Slot, stepEndLocal)) { AbortPull(sl, stepEndLocal); continue; }
                var r = CarRelay.PlaybackOf(e.Slot).SampleAt(stepEndLocal, delayOverride: 0);
                if (!r.Ok) { AbortPull(sl, stepEndLocal); continue; }
                ApplyProxyBody(e, sl, in r, servo, tau, stepEndLocal);
            }
            catch { }
        }
    }

    /// <summary>T4.0: write stream pose into the remote rigidbody (Kinematic Move* / Servo vel / Stale hold).</summary>
    static void ApplyProxyBody(CarEntry e, Slot sl, in StatePlayback.PlaybackResult r,
        bool servo, double tau, double now)
    {
        var p = new Vector3(r.Px, r.Py, r.Pz);
        var q = new Quaternion(r.Qx, r.Qy, r.Qz, r.Qw);
        // OTH: rigidbody sits on FusionPlayer root — LateUpdate drew the delayed pose onto the same
        // transform. Undo that before the physics write (same idea as OwnLocal for the own car).
        UndoVisualToPhys(e.Rb, sl, p, r);
        try
        {
            var nrb = e.Nrb ?? e.Fp?.GetComponent<NetworkRigidbody3D>();
            if (nrb != null) nrb._rootIsDirtyFromInterpolation = true;
        }
        catch { }

        if (r.Stale)
        {
            AbortPull(sl, now);
            HoldProxyZeroVel(e.Rb, sl, p, q);
            return;
        }

        if (!servo || e.Rb.isKinematic)
        {
            AbortPull(sl, now); // proxyPull is Servo-only
            if (!e.Rb.isKinematic) return;
            e.Rb.MovePosition(p);
            e.Rb.MoveRotation(q);
            sl.PhysP = p; sl.PhysQ = q; sl.HasPhys = true;
            return;
        }
        // Servo: stream velocity + pull; PhysP is captured in PostPhysicsStep after Simulate.
        var err = p - e.Rb.position;
        var errLen = err.magnitude;
        var done = AcceptMetricsLogic.ProxyPullStep(ref sl.PullSince, errLen, now);
        if (!double.IsNaN(done))
        {
            ProxyPullSamples++;
            if (done > ProxyPullMax) ProxyPullMax = done;
        }
        var streamSp = new Vector3(r.Vx, r.Vy, r.Vz).magnitude;
        var corr = (float)HostFollowLogic.ServoSpeed(errLen, tau, 10.0, 0, streamSp);
        var pull = errLen > 1e-4f ? err.normalized * corr : Vector3.zero;
        e.Rb.linearVelocity = new Vector3(r.Vx, r.Vy, r.Vz) + pull;
        // T4.0: angularVelocity = stream + (target−pose)/ServoSeconds (same owner as host follow).
        var streamW = new Vector3(r.Wx, r.Wy, r.Wz);
        var qe = q * Quaternion.Inverse(e.Rb.rotation);
        qe.ToAngleAxis(out var qang, out var axis);
        if (qang > 180f) qang -= 360f;
        var wc = Vector3.zero;
        if (!float.IsNaN(axis.x) && Mathf.Abs(qang) > 0.01f)
        {
            var rad = qang * Mathf.Deg2Rad;
            var ws = (float)HostFollowLogic.ServoAngSpeed(Math.Abs(rad), tau, 3.0);
            wc = axis * (Mathf.Sign(rad) * ws);
        }
        e.Rb.angularVelocity = streamW + wc;
        sl.ServoPending = true;
    }

    /// <summary>T4.0 Stale: keep newest pose, freeze linear/angular velocity.</summary>
    static void HoldProxyZeroVel(Rigidbody rb, Slot sl, Vector3 p, Quaternion q)
    {
        if (rb.isKinematic)
        {
            rb.MovePosition(p);
            rb.MoveRotation(q);
        }
        else
        {
            rb.position = p;
            rb.rotation = q;
        }
        try { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; } catch { }
        sl.PhysP = p; sl.PhysQ = q; sl.HasPhys = true;
        sl.ServoPending = false;
    }

    /// <summary>After Physics.Simulate: remember dynamic (Servo) body pose so the next step does not rewind it.</summary>
    public static void PostPhysicsStep()
    {
        if (LanState.Active != LanRole.Client) return;
        foreach (var e in CarRegistry.Cars)
        {
            try
            {
                if (e.IsOwn || e.Rb == null || e.Slot < 0) continue;
                if (!_slots.TryGetValue(e.Slot, out var sl) || !sl.ServoPending) continue;
                sl.PhysP = e.Rb.position;
                sl.PhysQ = e.Rb.rotation;
                sl.HasPhys = true;
                sl.ServoPending = false;
            }
            catch { }
        }
    }

    static void UndoVisualToPhys(Rigidbody rb, Slot sl, Vector3 presentP, in StatePlayback.PlaybackResult r)
    {
        if (sl.HasPhys)
        {
            rb.position = sl.PhysP;
            rb.rotation = sl.PhysQ;
            return;
        }
        var dt = Time.fixedDeltaTime;
        if (dt > 1e-4f)
            rb.position = presentP - new Vector3(r.Vx, r.Vy, r.Vz) * dt;
    }
}

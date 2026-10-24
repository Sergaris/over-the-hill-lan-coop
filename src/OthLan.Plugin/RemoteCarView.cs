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
    }

    static readonly Dictionary<int, Slot> _slots = new();
    static readonly RemoteSmoother.Cfg _blendCfg = new()
    {
        Tau = SourceBlendTau, TauLarge = SourceBlendTau, MaxCorrection = 50, SpeedFactor = 0,
        SmallMeters = 0, LargeMeters = 1
    };

    public static int StreamFrames, FusionFrames, Snaps, StaleFrames, ExtrapolatedFrames, SourceSwitches, RemoteBelow;
    public static int ProxiesTuned, Interpolated;

    static readonly List<IntPtr> _interpolated = new();

    public static void Reset()
    {
        _slots.Clear();
        _interpolated.Clear();
        StreamFrames = FusionFrames = Snaps = StaleFrames = ExtrapolatedFrames = SourceSwitches = RemoteBelow = 0;
        ProxiesTuned = Interpolated = 0;
    }

    public static string Describe() =>
        $"remoteView stream={StreamFrames} fusion={FusionFrames} stale={StaleFrames} extrap={ExtrapolatedFrames} snaps={Snaps} switches={SourceSwitches} remoteBelow={RemoteBelow} proxies={ProxiesTuned} interp={Interpolated}";

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
            try
            {
                if (e.IsOwn || e.Fp == null || e.Slot < 0) continue;
                var root = e.Fp.transform;
                if (root == null) continue;
                if (!_slots.TryGetValue(e.Slot, out var sl)) _slots[e.Slot] = sl = new Slot();
                var dt = sl.LastT < 0 ? 0 : t - sl.LastT;
                sl.LastT = t;

                if (!sl.ProxyEnsured)
                {
                    var nrb = e.Fp.GetComponent<NetworkRigidbody3D>();
                    if (nrb != null) EnsureProxyMode(e.Fp, nrb);
                    sl.ProxyEnsured = true;
                }

                var pb = CarRelay.PlaybackOf(e.Slot);
                var fresh = pb.Count > 0 && (now - pb.LastArrivalLocal) <= FreshAgeS;
                var r = fresh ? pb.SampleAt(now) : default;
                var stream = fresh && r.Ok && !r.Stale;
                if (fresh && r.Ok && r.Stale) StaleFrames++;
                if (stream && r.Extrapolated) ExtrapolatedFrames++;

                var src = stream ? Src.Stream : Src.Fusion;
                Vector3 targetP;
                Quaternion targetQ;
                if (stream)
                {
                    targetP = new Vector3(r.Px, r.Py, r.Pz);
                    targetQ = new Quaternion(r.Qx, r.Qy, r.Qz, r.Qw);
                    sl.LastSample = r;
                    sl.HasSample = true;
                    if (r.Snap) Snaps++;
                    StreamFrames++;
                }
                else
                {
                    targetP = root.position;
                    targetQ = root.rotation;
                    sl.HasSample = false;
                    sl.HasPhys = false; // next stream segment re-seeds from velocity
                    sl.ServoPending = false;
                    FusionFrames++;
                }

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

                var drawnP = new Vector3(targetP.x + (float)sl.Ox, targetP.y + (float)sl.Oy, targetP.z + (float)sl.Oz);
                var drawnQ = sl.Oq * targetQ;
                if (drawnP.y < targetP.y - 0.3f) RemoteBelow++;

                // Visual root only — kinematic body is owned by PrePhysicsStep (delay 0). Writing the delayed
                // pose into rb here made the next MovePosition invent a huge contact velocity.
                if (stream || Math.Abs(sl.Ox) + Math.Abs(sl.Oy) + Math.Abs(sl.Oz) > 1e-4 || Quaternion.Angle(sl.Oq, Quaternion.identity) > 1e-2f)
                    root.SetPositionAndRotation(drawnP, drawnQ);

                sl.LastDrawnP = drawnP;
                sl.LastDrawnQ = drawnQ;
                sl.HasDrawn = true;
            }
            catch (Exception ex)
            {
                if (StreamFrames + FusionFrames < 4) Plugin.L.LogWarning($"[RemoteView] {ex.Message}");
            }
        }
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
                if (!HasFreshStream(e.Slot, stepEndLocal)) continue;
                if (!_slots.TryGetValue(e.Slot, out var sl)) _slots[e.Slot] = sl = new Slot();
                var r = CarRelay.PlaybackOf(e.Slot).SampleAt(stepEndLocal, delayOverride: 0);
                if (!r.Ok || r.Stale) continue;
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

                if (!servo || e.Rb.isKinematic)
                {
                    if (!e.Rb.isKinematic) continue;
                    e.Rb.MovePosition(p);
                    e.Rb.MoveRotation(q);
                    // Kinematic: MovePosition is the physics pose immediately.
                    sl.PhysP = p; sl.PhysQ = q; sl.HasPhys = true;
                    continue;
                }
                // Servo: stream velocity + pull; PhysP is captured in PostPhysicsStep after Simulate.
                var err = p - e.Rb.position;
                var errLen = err.magnitude;
                var streamSp = new Vector3(r.Vx, r.Vy, r.Vz).magnitude;
                var corr = (float)HostFollowLogic.ServoSpeed(errLen, tau, 10.0, 0, streamSp);
                var pull = errLen > 1e-4f ? err.normalized * corr : Vector3.zero;
                e.Rb.linearVelocity = new Vector3(r.Vx, r.Vy, r.Vz) + pull;
                e.Rb.angularVelocity = new Vector3(r.Wx, r.Wy, r.Wz);
                sl.ServoPending = true;
            }
            catch { }
        }
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

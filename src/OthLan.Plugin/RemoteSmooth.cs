using System;
using System.Collections.Generic;
using Fusion;
using Fusion.Addons.Physics;
using UnityEngine;

namespace OthLan;

/// <summary>
/// 0.1.8 netcode v3: visual smoothing of the other players' cars (docs/NETCODE-v3-RESEARCH.md, NETCODE-v2 2.4).
/// Runs in Overlay.LateUpdate, i.e. after Fusion's Render (Fusion's default player loop: Render right after the
/// MonoBehaviour Update scripts) and before the frame is drawn. It reads the pose Fusion gave the car's
/// InterpolationTarget (the visual child; the rigidbody is elsewhere) and replaces it with RemoteSmoother's output.
/// Fusion writes the InterpolationTarget again in its next Render, so nothing has to be restored; physics, collisions,
/// the host follower and the network state never see the visual offset.
/// Disabled per car when the car has no separate InterpolationTarget (then the transform is the physics body).
/// </summary>
internal static class RemoteSmooth
{
    sealed class Car
    {
        public readonly RemoteSmoother S = new();
        public Transform It; public bool Usable, Checked;
        public Vector3 LastFusionP, LastWrittenP; public Quaternion LastFusionQ = Quaternion.identity, LastWrittenQ = Quaternion.identity, LastDrawnQ = Quaternion.identity;
        public Quaternion Qo = Quaternion.identity; public Vector3 W; public bool HasQ, Wrote;
        public float HoldRotFor; // T1.2: Hold/Extrapolate rotation timeout
        public readonly Smoothness Raw = new();
    }
    static readonly Dictionary<IntPtr, Car> _cars = new();
    static readonly RemoteSmoother.Cfg _cfg = new();
    static FusionPlayer[] _players = Array.Empty<FusionPlayer>(); static IntPtr _runner;
    public static readonly Smoothness RawAll = new();
    public static int Absorbs, Snaps, ExtrapFrames, NoTarget, FusionSkipped;
    static float _lastT = -1;
    // Cars we made non-simulated for RemoteInterpolate (so a live F7 toggle can undo / redo it).
    static readonly List<IntPtr> _interpolated = new();

    public static bool Enabled => Plugin.RemoteSmoothing?.Value ?? false;

    static void ResetAll() { _cars.Clear(); _interpolated.Clear(); RawAll.Reset(); Absorbs = Snaps = ExtrapFrames = NoTarget = FusionSkipped = ProxiesTuned = Interpolated = 0; _lastT = -1; RemoteVisuals.Reset(); }

    public static string StateOf(IntPtr p) => _cars.TryGetValue(p, out var c) ? (c.Usable ? $"{c.S.Last}/off{c.S.OffsetLength * 100:0}cm" : "unusable") : "-";
    public static int StateCode(IntPtr p) => _cars.TryGetValue(p, out var c) && c.Usable ? (int)c.S.Last : -1;
    public static Vector3 OffsetOf(IntPtr p) => _cars.TryGetValue(p, out var c) ? new Vector3((float)c.S.Ox, (float)c.S.Oy, (float)c.S.Oz) : Vector3.zero;

    public static string Describe() =>
        $"remote smoothing {(Enabled ? "on" : Plugin.RemoteInterpolate?.Value == true ? "root-gap" : "off")}: raw (Fusion) {RawAll.Describe()} | absorbed={Absorbs} snaps={Snaps} extrapolatedFrames={ExtrapFrames} noTarget={NoTarget} fusionSkipped={FusionSkipped} proxiesTuned={ProxiesTuned} interpolated={Interpolated}";

    public static int ProxiesTuned;

    /// <summary>
    /// v3d (measured in v3c fc4, docs/NETCODE-v3-RESEARCH.md 3): with ClientPhysicsSimulation=SimulateAlways a client
    /// predicts the other players' cars ~0.4 s ahead of the host. When the other driver brakes, the prediction is
    /// wrong by v*0.4 s; the game's NetworkRigidbody3D smooths such corrections for 0.1 s but snaps above 2 m, which
    /// was the 2.6 m remote jump. Physics still snaps (only rendering is smoothed), so raising these per remote car
    /// changes nothing in the simulation. Own car: OwnCar handles it.
    /// </summary>
    public static int Interpolated;
    static void TuneProxy(FusionPlayer fp, NetworkRigidbody3D nrb)
    {
        try
        {
            float s0 = nrb.CorrectionSmoothingTime, d0 = nrb.CorrectionSnapDistance, a0 = nrb.CorrectionSnapAngle;
            if (Plugin.RemoteProxySmoothing.Value > 0) nrb.CorrectionSmoothingTime = Mathf.Max(s0, Plugin.RemoteProxySmoothing.Value);
            if (Plugin.RemoteProxySnapMeters.Value > 0) nrb.CorrectionSnapDistance = Mathf.Max(d0, Plugin.RemoteProxySnapMeters.Value);
            if (Plugin.RemoteProxySnapDegrees.Value > 0) nrb.CorrectionSnapAngle = Mathf.Max(a0, Plugin.RemoteProxySnapDegrees.Value);
            ProxiesTuned++;
            if (Plugin.RemoteInterpolate.Value) ApplyInterpolate(fp, true);
            else ApplyInterpolate(fp, false);
            // R1: non-simulated cars never get AfterAllTicks → apply headlights from CurrentFrameInput immediately
            // (late join + first tune). Ongoing updates: RemoteVisuals.LateTick.
            if (Plugin.RemoteInterpolate.Value) RemoteVisuals.ApplyLightsNow(fp);
            Plugin.L.LogInfo($"[RemoteSmooth] {fp.gameObject.name}: proxy correction {s0:0.##}s/{d0:0.#}m/{a0:0}deg -> {nrb.CorrectionSmoothingTime:0.##}s/{nrb.CorrectionSnapDistance:0.#}m/{nrb.CorrectionSnapAngle:0}deg");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[RemoteSmooth] proxy tune: {e.Message}"); }
    }

    static void ApplyInterpolate(FusionPlayer fp, bool on)
    {
        try
        {
            if (fp?.Runner == null || fp.Object == null) return;
            var ok = fp.Runner.SetIsSimulated(fp.Object, !on); // on = interpolate = not simulated
            var rb = fp.rigidbody ?? fp.vehicleBase?.cachedRigidbody;
            if (rb != null) rb.isKinematic = on;
            if (on)
            {
                if (!_interpolated.Contains(fp.Pointer)) { _interpolated.Add(fp.Pointer); Interpolated++; }
            }
            else _interpolated.Remove(fp.Pointer);
            Plugin.L.LogInfo($"[RemoteSmooth] {fp.gameObject.name}: {(on ? "interpolated (not simulated)" : "simulated again")} {(ok ? "ok" : "refused")}");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[RemoteSmooth] interpolate: {e.Message}"); }
    }

    /// <summary>0.1.9 U0: live F7 toggle. Retunes every remote car already seen; new cars still go through TuneProxy.</summary>
    public static void SetInterpolate(bool on, string why)
    {
        if (Plugin.RemoteInterpolate.Value == on) return;
        Plugin.RemoteInterpolate.Value = on;
        Plugin.L.LogInfo($"[RemoteSmooth] RemoteInterpolate {(on ? "on" : "off")} ({why})");
        foreach (var fp in CarRegistry.Players)
        {
            try
            {
                if (fp == null || fp.WasCollected || fp.Object == null || fp.Object.HasInputAuthority) continue;
                ApplyInterpolate(fp, on);
                if (on) RemoteVisuals.ApplyLightsNow(fp);
            }
            catch (Exception e) { Plugin.L.LogWarning($"[RemoteSmooth] retune: {e.Message}"); }
        }
        if (!on) RemoteVisuals.Reset();
    }

    /// <summary>Main thread, LateUpdate.</summary>
    public static void LateTick()
    {
        using var _cost = FrameCost.Measure(FrameCost.RemoteView);
        if (LanState.Active == LanRole.Off || LanState.LanRunner == IntPtr.Zero) { if (_runner != IntPtr.Zero) { _runner = IntPtr.Zero; ResetAll(); } return; }
        NetworkRunner r = null;
        try { r = OverlandNetworkRunner.Instance?.NetworkRunner; } catch { }
        if (r == null || r.Pointer != LanState.LanRunner || !r.IsRunning) return;
        if (r.Pointer != _runner) { _runner = r.Pointer; ResetAll(); }
        var t = Time.realtimeSinceStartup; var dt = _lastT < 0 ? 0 : t - _lastT; _lastT = t;
        _players = CarRegistry.Players;
        var client = r.IsClient && !r.IsServer;
        var starveS = Plugin.RemoteStarvedSeconds.Value;
        // 0.1.9 U5: under RemoteInterpolate OTH cars have no InterpolationTarget (Usable=false), so the old
        // RemoteSmoothing path never ran. Run RemoteSmoother on the root instead: Fusion freezes the kinematic
        // remote car across snapshot gaps → dead-reckon ≤ ExtrapolateSeconds, then hold and blend back.
        var rootGapSmooth = client && Plugin.RemoteInterpolate.Value;
        _cfg.ExtrapolateSeconds = Plugin.RemoteExtrapolateSeconds.Value; _cfg.Tau = Plugin.RemoteSmoothTau.Value; _cfg.TauLarge = Math.Min(_cfg.Tau, Plugin.RemoteSmoothTauLarge.Value);
        _cfg.MaxCorrection = Plugin.RemoteMaxCorrection.Value; _cfg.SnapMeters = Plugin.RemoteSnapMeters.Value;
        foreach (var fp in _players)
        {
            try
            {
                if (fp == null || fp.WasCollected || fp.Object == null || fp.Object.HasInputAuthority) continue;
                // 0.2.0 T3.5: OTC3 stream owns the draw when fresh — do not fight RemoteCarView.
                try
                {
                    var slot = fp.Object.InputAuthority.AsIndex;
                    if (RemoteCarView.HasFreshStream(slot, NetClock.Now)) continue;
                }
                catch { }
                if (!_cars.TryGetValue(fp.Pointer, out var c)) _cars[fp.Pointer] = c = new Car();
                if (!c.Checked)
                {
                    c.Checked = true;
                    var nrb = fp.GetComponent<NetworkRigidbody3D>(); c.It = nrb != null ? nrb.InterpolationTarget : null;
                    c.Usable = c.It != null && c.It != fp.transform && c.It.GetComponent<Rigidbody>() == null;
                    if (!c.Usable)
                    {
                        NoTarget++;
                        Plugin.L.LogInfo(rootGapSmooth
                            ? $"[RemoteSmooth] {fp.gameObject.name}: no InterpolationTarget — root gap smoothing (RemoteInterpolate)"
                            : $"[RemoteSmooth] {fp.gameObject.name}: no separate InterpolationTarget, not smoothed");
                    }
                    if (client && nrb != null) TuneProxy(fp, nrb);
                }
                Transform vis = null;
                if (c.Usable && c.It != null) vis = c.It;
                else if (rootGapSmooth) vis = fp.transform;
                if (vis == null) continue;
                var fpos = vis.position; var fq = vis.rotation;
                // Fusion did not write the target this frame (our value is still there): its pose is the last one we saw.
                if (c.Wrote && fpos == c.LastWrittenP && fq == c.LastWrittenQ) { fpos = c.LastFusionP; fq = c.LastFusionQ; FusionSkipped++; }
                // T1.2: starve = no new host snapshots (ServerTickAge), not "this car's Fusion pose frozen".
                var age = NetStats.ServerTickAge;
                var starved = client && age >= 0 && age > starveS;
                // raw = what Fusion alone would have drawn (same metric as NetStats, for the A/B in one run)
                if (dt > 0 && dt <= 0.25f) { var f0 = c.Raw.Frames; var h = c.Raw.Add(fpos.x, fpos.y, fpos.z, dt); if (c.Raw.Frames > f0) RawAll.Record(h); } else c.Raw.Break();
                // IT path still needs RemoteSmoothing=true; root path is on whenever RemoteInterpolate is.
                if (!(Enabled || (rootGapSmooth && !c.Usable))) { c.S.Reset(); c.Wrote = false; c.LastFusionP = fpos; c.LastFusionQ = fq; continue; }
                var a0 = c.S.Absorbed; var s0 = c.S.Snaps; var e0 = c.S.ExtrapolatedFrames;
                var (x, y, z) = c.S.Step(fpos.x, fpos.y, fpos.z, dt, _cfg, starved);
                Absorbs += c.S.Absorbed - a0; Snaps += c.S.Snaps - s0; ExtrapFrames += c.S.ExtrapolatedFrames - e0;
                // rotation: angular velocity estimate from Fusion's frames; same absorb / extrapolate / decay as position
                Quaternion drawnQ;
                if (!c.HasQ || dt <= 0 || dt > 0.5f || c.S.Snaps != s0) { c.Qo = Quaternion.identity; c.W = Vector3.zero; drawnQ = fq; c.HasQ = true; c.HoldRotFor = 0; }
                else
                {
                    var dq = fq * Quaternion.Inverse(c.LastFusionQ); dq.ToAngleAxis(out var ang, out var axis);
                    if (ang > 180) ang -= 360;
                    var frozen = Quaternion.Angle(fq, c.LastFusionQ) < 1e-3f;
                    if (c.S.Last == RemoteSmoother.State.Extrapolate)
                    {
                        c.HoldRotFor = 0;
                        drawnQ = Quaternion.AngleAxis(c.W.magnitude * Mathf.Rad2Deg * dt, c.W.sqrMagnitude > 1e-8f ? c.W.normalized : Vector3.up) * c.LastDrawnQ; c.Qo = drawnQ * Quaternion.Inverse(fq);
                    }
                    else if (c.S.Last == RemoteSmoother.State.Hold)
                    {
                        c.HoldRotFor += dt;
                        if (c.HoldRotFor > _cfg.HoldMaxSeconds) { c.Qo = Quaternion.identity; drawnQ = fq; c.HoldRotFor = 0; }
                        else { drawnQ = c.LastDrawnQ; c.Qo = drawnQ * Quaternion.Inverse(fq); }
                    }
                    else
                    {
                        c.HoldRotFor = 0;
                        var expectedQ = Quaternion.AngleAxis(c.W.magnitude * Mathf.Rad2Deg * dt, c.W.sqrMagnitude > 1e-8f ? c.W.normalized : Vector3.up) * c.LastDrawnQ;
                        if (c.S.LastAbsorbed || Quaternion.Angle(c.Qo * fq, expectedQ) > Plugin.RemoteAbsorbDegrees.Value)
                            c.Qo = expectedQ * Quaternion.Inverse(fq);
                        else if (!frozen && !float.IsNaN(axis.x) && Mathf.Abs(ang) < 90) c.W = Vector3.Lerp(c.W, axis * (ang * Mathf.Deg2Rad / dt), 0.5f);
                        var keep = (float)RemoteSmoother.RotKeep(Quaternion.Angle(c.Qo, Quaternion.identity), dt, _cfg.Tau, Plugin.RemoteMaxCorrectionDeg.Value);
                        c.Qo = Quaternion.Slerp(Quaternion.identity, c.Qo, keep);
                        drawnQ = c.Qo * fq;
                    }
                }
                var drawn = new Vector3((float)x, (float)y, (float)z);
                if (drawn.y < fpos.y - 0.3f) NetStats.RemoteBelow++;
                c.LastFusionP = fpos; c.LastFusionQ = fq;
                if ((drawn - fpos).sqrMagnitude > 1e-8f || Quaternion.Angle(drawnQ, fq) > 1e-3f) { vis.SetPositionAndRotation(drawn, drawnQ); c.Wrote = true; c.LastWrittenP = vis.position; c.LastWrittenQ = vis.rotation; }
                else c.Wrote = false;
                c.LastDrawnQ = drawnQ;
            }
            catch { }
        }
    }
}

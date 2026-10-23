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
        public readonly Smoothness Raw = new();
    }
    static readonly Dictionary<IntPtr, Car> _cars = new();
    static readonly RemoteSmoother.Cfg _cfg = new();
    static FusionPlayer[] _players = Array.Empty<FusionPlayer>(); static float _nextScan; static IntPtr _runner;
    public static readonly Smoothness RawAll = new();
    public static int Absorbs, Snaps, ExtrapFrames, NoTarget, FusionSkipped;
    static float _lastT = -1;

    public static bool Enabled => Plugin.RemoteSmoothing?.Value ?? false;

    static void ResetAll() { _cars.Clear(); RawAll.Reset(); Absorbs = Snaps = ExtrapFrames = NoTarget = FusionSkipped = ProxiesTuned = 0; _nextScan = 0; _lastT = -1; }

    public static string StateOf(IntPtr p) => _cars.TryGetValue(p, out var c) ? (c.Usable ? $"{c.S.Last}/off{c.S.OffsetLength * 100:0}cm" : "unusable") : "-";
    public static int StateCode(IntPtr p) => _cars.TryGetValue(p, out var c) && c.Usable ? (int)c.S.Last : -1;
    public static Vector3 OffsetOf(IntPtr p) => _cars.TryGetValue(p, out var c) ? new Vector3((float)c.S.Ox, (float)c.S.Oy, (float)c.S.Oz) : Vector3.zero;

    public static string Describe() =>
        $"remote smoothing {(Enabled ? "on" : "off")}: raw (Fusion) {RawAll.Describe()} | absorbed={Absorbs} snaps={Snaps} extrapolatedFrames={ExtrapFrames} noTarget={NoTarget} fusionSkipped={FusionSkipped} proxiesTuned={ProxiesTuned} interpolated={Interpolated}";

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
            if (Plugin.RemoteInterpolate.Value)
            {
                // v4 2.3: other players' cars are drawn from Fusion's interpolation buffer (time-shifted, jitter-buffered
                // playback of the host's snapshots) instead of being predicted: no corrections, no resimulation cost.
                try
                {
                    var ok = fp.Runner != null && fp.Runner.SetIsSimulated(fp.Object, false);
                    var rb = fp.rigidbody; if (rb != null) rb.isKinematic = true;
                    Interpolated++;
                    Plugin.L.LogInfo($"[RemoteSmooth] {fp.gameObject.name}: interpolated (not simulated) {(ok ? "ok" : "refused")}");
                }
                catch (Exception e) { Plugin.L.LogWarning($"[RemoteSmooth] interpolate: {e.Message}"); }
            }
            Plugin.L.LogInfo($"[RemoteSmooth] {fp.gameObject.name}: proxy correction {s0:0.##}s/{d0:0.#}m/{a0:0}deg -> {nrb.CorrectionSmoothingTime:0.##}s/{nrb.CorrectionSnapDistance:0.#}m/{nrb.CorrectionSnapAngle:0}deg");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[RemoteSmooth] proxy tune: {e.Message}"); }
    }

    /// <summary>Main thread, LateUpdate.</summary>
    public static void LateTick()
    {
        if (LanState.Active == LanRole.Off || LanState.LanRunner == IntPtr.Zero) { if (_runner != IntPtr.Zero) { _runner = IntPtr.Zero; ResetAll(); } return; }
        NetworkRunner r = null;
        try { r = OverlandNetworkRunner.Instance?.NetworkRunner; } catch { }
        if (r == null || r.Pointer != LanState.LanRunner || !r.IsRunning) return;
        if (r.Pointer != _runner) { _runner = r.Pointer; ResetAll(); }
        var t = Time.realtimeSinceStartup; var dt = _lastT < 0 ? 0 : t - _lastT; _lastT = t;
        if (t >= _nextScan) { _nextScan = t + 1f; try { _players = UnityEngine.Object.FindObjectsOfType<FusionPlayer>(); } catch { _players = Array.Empty<FusionPlayer>(); } }
        var client = r.IsClient && !r.IsServer;
        var starved = client && NetStats.SnapshotAge > Plugin.RemoteStarvedSeconds.Value;
        _cfg.ExtrapolateSeconds = Plugin.RemoteExtrapolateSeconds.Value; _cfg.Tau = Plugin.RemoteSmoothTau.Value; _cfg.TauLarge = Math.Min(_cfg.Tau, Plugin.RemoteSmoothTauLarge.Value);
        _cfg.MaxCorrection = Plugin.RemoteMaxCorrection.Value; _cfg.SnapMeters = Plugin.RemoteSnapMeters.Value;
        foreach (var fp in _players)
        {
            try
            {
                if (fp == null || fp.WasCollected || fp.Object == null || fp.Object.HasInputAuthority) continue;
                if (!_cars.TryGetValue(fp.Pointer, out var c)) _cars[fp.Pointer] = c = new Car();
                if (!c.Checked)
                {
                    c.Checked = true;
                    var nrb = fp.GetComponent<NetworkRigidbody3D>(); c.It = nrb != null ? nrb.InterpolationTarget : null;
                    c.Usable = c.It != null && c.It != fp.transform && c.It.GetComponent<Rigidbody>() == null;
                    if (!c.Usable) { NoTarget++; Plugin.L.LogInfo($"[RemoteSmooth] {fp.gameObject.name}: no separate InterpolationTarget, not smoothed"); }
                    if (client && nrb != null) TuneProxy(fp, nrb);
                }
                if (!c.Usable || c.It == null) continue;
                var fpos = c.It.position; var fq = c.It.rotation;
                // Fusion did not write the target this frame (our value is still there): its pose is the last one we saw.
                if (c.Wrote && fpos == c.LastWrittenP && fq == c.LastWrittenQ) { fpos = c.LastFusionP; fq = c.LastFusionQ; FusionSkipped++; }
                // raw = what Fusion alone would have drawn (same metric as NetStats, for the A/B in one run)
                if (dt > 0 && dt <= 0.25f) { var f0 = c.Raw.Frames; var h = c.Raw.Add(fpos.x, fpos.y, fpos.z, dt); if (c.Raw.Frames > f0) RawAll.Record(h); } else c.Raw.Break();
                if (!Enabled) { c.S.Reset(); c.Wrote = false; c.LastFusionP = fpos; c.LastFusionQ = fq; continue; }
                var a0 = c.S.Absorbed; var s0 = c.S.Snaps; var e0 = c.S.ExtrapolatedFrames;
                var (x, y, z) = c.S.Step(fpos.x, fpos.y, fpos.z, dt, _cfg, starved);
                Absorbs += c.S.Absorbed - a0; Snaps += c.S.Snaps - s0; ExtrapFrames += c.S.ExtrapolatedFrames - e0;
                // rotation: angular velocity estimate from Fusion's frames; same absorb / extrapolate / decay as position
                Quaternion drawnQ;
                if (!c.HasQ || dt <= 0 || dt > 0.5f || c.S.Snaps != s0) { c.Qo = Quaternion.identity; c.W = Vector3.zero; drawnQ = fq; c.HasQ = true; }
                else
                {
                    var dq = fq * Quaternion.Inverse(c.LastFusionQ); dq.ToAngleAxis(out var ang, out var axis);
                    if (ang > 180) ang -= 360;
                    var frozen = Quaternion.Angle(fq, c.LastFusionQ) < 1e-3f;
                    if (c.S.Last == RemoteSmoother.State.Extrapolate)
                    { drawnQ = Quaternion.AngleAxis(c.W.magnitude * Mathf.Rad2Deg * dt, c.W.sqrMagnitude > 1e-8f ? c.W.normalized : Vector3.up) * c.LastDrawnQ; c.Qo = drawnQ * Quaternion.Inverse(fq); }
                    else if (c.S.Last == RemoteSmoother.State.Hold) { drawnQ = c.LastDrawnQ; c.Qo = drawnQ * Quaternion.Inverse(fq); }
                    else
                    {
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
                c.LastFusionP = fpos; c.LastFusionQ = fq;
                if ((drawn - fpos).sqrMagnitude > 1e-8f || Quaternion.Angle(drawnQ, fq) > 1e-3f) { c.It.SetPositionAndRotation(drawn, drawnQ); c.Wrote = true; c.LastWrittenP = c.It.position; c.LastWrittenQ = c.It.rotation; }
                else c.Wrote = false;
                c.LastDrawnQ = drawnQ;
            }
            catch { }
        }
    }
}

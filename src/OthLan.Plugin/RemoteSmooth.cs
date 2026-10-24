using System;
using System.Collections.Generic;
using Fusion;
using Fusion.Addons.Physics;
using UnityEngine;

namespace OthLan;

/// <summary>
/// Fusion fallback visual smoothing when OTC3 stream is stale (T1.2 RemoteSmoother on InterpolationTarget).
/// 0.2.0 T3.5: root-gap path removed — RemoteCarView owns remote root pose; TuneProxy → RemoteCarView.EnsureProxyMode.
/// </summary>
internal static class RemoteSmooth
{
    sealed class Car
    {
        public readonly RemoteSmoother S = new();
        public Transform It; public bool Usable, Checked;
        public Vector3 LastFusionP, LastWrittenP; public Quaternion LastFusionQ = Quaternion.identity, LastWrittenQ = Quaternion.identity, LastDrawnQ = Quaternion.identity;
        public Quaternion Qo = Quaternion.identity; public Vector3 W; public bool HasQ, Wrote;
        public float HoldRotFor;
        public readonly Smoothness Raw = new();
    }
    static readonly Dictionary<IntPtr, Car> _cars = new();
    static readonly RemoteSmoother.Cfg _cfg = new();
    static FusionPlayer[] _players = Array.Empty<FusionPlayer>(); static IntPtr _runner;
    public static readonly Smoothness RawAll = new();
    public static int Absorbs, Snaps, ExtrapFrames, NoTarget, FusionSkipped;
    static float _lastT = -1;

    public static bool Enabled => Plugin.RemoteSmoothing?.Value ?? false;

    static void ResetAll()
    {
        _cars.Clear(); RawAll.Reset(); Absorbs = Snaps = ExtrapFrames = NoTarget = FusionSkipped = 0; _lastT = -1;
        RemoteWheelVisuals.Reset();
        RemoteCarView.Reset();
    }

    public static string StateOf(IntPtr p) => _cars.TryGetValue(p, out var c) ? (c.Usable ? $"{c.S.Last}/off{c.S.OffsetLength * 100:0}cm" : "unusable") : "-";
    public static int StateCode(IntPtr p) => _cars.TryGetValue(p, out var c) && c.Usable ? (int)c.S.Last : -1;
    public static Vector3 OffsetOf(IntPtr p) => _cars.TryGetValue(p, out var c) ? new Vector3((float)c.S.Ox, (float)c.S.Oy, (float)c.S.Oz) : Vector3.zero;

    public static string Describe() =>
        $"remote smoothing {(Enabled ? "on" : "off")}: raw (Fusion) {RawAll.Describe()} | absorbed={Absorbs} snaps={Snaps} extrapolatedFrames={ExtrapFrames} noTarget={NoTarget} fusionSkipped={FusionSkipped} | {RemoteCarView.Describe()}";

    /// <summary>F7 live toggle — forwards to RemoteCarView (owner of EnsureProxyMode).</summary>
    public static void SetInterpolate(bool on, string why) => RemoteCarView.SetInterpolate(on, why);

    /// <summary>Main thread, LateUpdate — only InterpolationTarget path when stream is not fresh.</summary>
    public static void LateTick()
    {
        if (LanState.Active == LanRole.Off || LanState.LanRunner == IntPtr.Zero) { if (_runner != IntPtr.Zero) { _runner = IntPtr.Zero; ResetAll(); } return; }
        NetworkRunner r = null;
        try { r = OverlandNetworkRunner.Instance?.NetworkRunner; } catch { }
        if (r == null || r.Pointer != LanState.LanRunner || !r.IsRunning) return;
        if (r.Pointer != _runner) { _runner = r.Pointer; ResetAll(); }
        var t = Time.realtimeSinceStartup; var dt = _lastT < 0 ? 0 : t - _lastT; _lastT = t;
        _players = CarRegistry.Players;
        var client = r.IsClient && !r.IsServer;
        var starveS = Plugin.RemoteStarvedSeconds.Value;
        _cfg.ExtrapolateSeconds = Plugin.RemoteExtrapolateSeconds.Value; _cfg.Tau = Plugin.RemoteSmoothTau.Value; _cfg.TauLarge = Math.Min(_cfg.Tau, Plugin.RemoteSmoothTauLarge.Value);
        _cfg.MaxCorrection = Plugin.RemoteMaxCorrection.Value; _cfg.SnapMeters = Plugin.RemoteSnapMeters.Value;
        foreach (var fp in _players)
        {
            try
            {
                if (fp == null || fp.WasCollected || fp.Object == null || fp.Object.HasInputAuthority) continue;
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
                        Plugin.L.LogInfo($"[RemoteSmooth] {fp.gameObject.name}: no separate InterpolationTarget, Fusion fallback without root-gap (RemoteCarView owns root)");
                    }
                    if (client && nrb != null) RemoteCarView.EnsureProxyMode(fp, nrb);
                }
                if (!c.Usable || c.It == null) continue;
                var vis = c.It;
                var fpos = vis.position; var fq = vis.rotation;
                if (c.Wrote && fpos == c.LastWrittenP && fq == c.LastWrittenQ) { fpos = c.LastFusionP; fq = c.LastFusionQ; FusionSkipped++; }
                var age = NetStats.ServerTickAge;
                var starved = client && age >= 0 && age > starveS;
                if (dt > 0 && dt <= 0.25f) { var f0 = c.Raw.Frames; var h = c.Raw.Add(fpos.x, fpos.y, fpos.z, dt); if (c.Raw.Frames > f0) RawAll.Record(h); } else c.Raw.Break();
                if (!Enabled) { c.S.Reset(); c.Wrote = false; c.LastFusionP = fpos; c.LastFusionQ = fq; continue; }
                var a0 = c.S.Absorbed; var s0 = c.S.Snaps; var e0 = c.S.ExtrapolatedFrames;
                var (x, y, z) = c.S.Step(fpos.x, fpos.y, fpos.z, dt, _cfg, starved);
                Absorbs += c.S.Absorbed - a0; Snaps += c.S.Snaps - s0; ExtrapFrames += c.S.ExtrapolatedFrames - e0;
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

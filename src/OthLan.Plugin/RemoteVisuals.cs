using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;
using VehiclePhysics;

namespace OthLan;

/// <summary>
/// 0.1.9 R1: when <see cref="Plugin.RemoteInterpolate"/> makes remote cars non-simulated, Fusion stops calling
/// AfterAllTicks / FixedUpdateNetwork on them, so headlights, brake/reverse lights, VPP wheel visuals and
/// <see cref="VehicleCustomizationController.ApplyNetworkedModLivery"/> freeze.
/// This module drives those visuals from replicated input / networked status / transform motion each LateUpdate
/// (client). Host still simulates every car and does not need this path.
/// Owner of "remote car visual sync when non-simulated"; called from Overlay.LateUpdate after RemoteSmooth.
/// </summary>
internal static class RemoteVisuals
{
    sealed class Car
    {
        public LightsController Lights;
        public WheelTransformUpdater[] Wheels;
        public NetworkedVehicleStatus Status;
        public VehicleCustomizationController Custom;
        public readonly float[] SpinDeg = new float[8];
        public float LastT = -1;
        public Vector3 LastPos;
        public bool HasPos;
        public bool Lit;
        public long AppliedLiveryId = long.MinValue;
        public int AppliedLiverySub = int.MinValue;
        public bool WtuDisabled;
    }

    static readonly Dictionary<IntPtr, Car> _cars = new();
    static FusionPlayer[] _players = Array.Empty<FusionPlayer>();
    static float _nextScan;
    static IntPtr _runner;
    public static int Cars, LightSets, WheelFrames, LiveryApplies, Errors;

    public static void Reset()
    {
        foreach (var c in _cars.Values)
            RestoreWtu(c);
        _cars.Clear();
        Cars = LightSets = WheelFrames = LiveryApplies = Errors = 0;
        _nextScan = 0;
    }

    /// <summary>Apply current lights once (late join / right after TuneProxy).</summary>
    public static void ApplyLightsNow(FusionPlayer fp)
    {
        if (fp == null) return;
        try
        {
            if (!_cars.TryGetValue(fp.Pointer, out var c)) _cars[fp.Pointer] = c = new Car();
            Ensure(fp, c);
            ApplyLights(fp, c);
            ApplyLivery(c);
        }
        catch (Exception e) { Errors++; if (Errors < 4) Plugin.L.LogWarning($"[RemoteVisuals] lights-now: {e.Message}"); }
    }

    /// <summary>Main thread, LateUpdate. Client only; only when remote cars are interpolated (non-simulated).</summary>
    public static void LateTick()
    {
        if (!Plugin.RemoteInterpolate.Value) return;
        if (LanState.Active != LanRole.Client || LanState.LanRunner == IntPtr.Zero) { if (_runner != IntPtr.Zero) { _runner = IntPtr.Zero; Reset(); } return; }
        NetworkRunner r = null;
        try { r = OverlandNetworkRunner.Instance?.NetworkRunner; } catch { }
        if (r == null || r.Pointer != LanState.LanRunner || !r.IsRunning || !r.IsClient || r.IsServer) return;
        if (r.Pointer != _runner) { _runner = r.Pointer; Reset(); }

        var t = Time.realtimeSinceStartup;
        if (t >= _nextScan) { _nextScan = t + 1f; try { _players = UnityEngine.Object.FindObjectsOfType<FusionPlayer>(); } catch { _players = Array.Empty<FusionPlayer>(); } }

        Cars = 0;
        foreach (var fp in _players)
        {
            try
            {
                if (fp == null || fp.WasCollected || fp.Object == null || fp.Object.HasInputAuthority) continue;
                if (!_cars.TryGetValue(fp.Pointer, out var c)) _cars[fp.Pointer] = c = new Car();
                Ensure(fp, c);
                ApplyLights(fp, c);
                ApplyWheels(fp, c, t);
                ApplyLivery(c);
                Cars++;
            }
            catch (Exception e) { Errors++; if (Errors < 6) Plugin.L.LogWarning($"[RemoteVisuals] {e.Message}"); }
        }
    }

    static void Ensure(FusionPlayer fp, Car c)
    {
        if (c.Lights == null || c.Lights.WasCollected)
            c.Lights = fp.GetComponentInChildren<LightsController>() ?? fp.vehicleBase?.GetComponentInChildren<LightsController>();
        if (c.Wheels == null)
        {
            try { c.Wheels = fp.GetComponentsInChildren<WheelTransformUpdater>(); }
            catch { c.Wheels = Array.Empty<WheelTransformUpdater>(); }
        }
        if (c.Status == null || c.Status.WasCollected)
            c.Status = fp.GetComponentInChildren<NetworkedVehicleStatus>() ?? fp.GetComponent<NetworkedVehicleStatus>();
        if (c.Custom == null || c.Custom.WasCollected)
            c.Custom = fp.GetComponentInChildren<VehicleCustomizationController>() ?? fp.GetComponent<VehicleCustomizationController>();
        // Own the visual wheel pose: WTU.Update would keep writing frozen VPP angles every frame.
        if (!c.WtuDisabled && c.Wheels != null)
        {
            for (int i = 0; i < c.Wheels.Length; i++)
            {
                var w = c.Wheels[i];
                if (w != null && !w.WasCollected && w.enabled) w.enabled = false;
            }
            c.WtuDisabled = true;
        }
    }

    static void RestoreWtu(Car c)
    {
        if (c?.Wheels == null) return;
        for (int i = 0; i < c.Wheels.Length; i++)
        {
            try
            {
                var w = c.Wheels[i];
                if (w != null && !w.WasCollected) w.enabled = true;
            }
            catch { }
        }
        c.WtuDisabled = false;
    }

    static void ApplyLights(FusionPlayer fp, Car c)
    {
        if (c.Lights == null || c.Lights.WasCollected) return;
        var wantHead = fp.ButtonDown(NetworkInputData.VehicleButtons.Headlights);
        if (c.Lights.AreHeadlightsOn != wantHead)
        {
            c.Lights.AreHeadlightsOn = wantHead;
            LightSets++;
            c.Lit = wantHead;
        }
        var inp = fp.CurrentFrameInput;
        var wantBrake = inp.Brake > 0.05f;
        if (c.Lights.AreBrakingLightsOn != wantBrake) c.Lights.AreBrakingLightsOn = wantBrake;
        // Reverse: prefer networked speed (kinematic rb.linearVelocity is ~0 under RemoteInterpolate).
        var speed = ForwardSpeedMs(fp, c, 0f);
        var reverse = speed < -0.8f && inp.Throttle < 0.05f;
        if (c.Lights.AreReverseLightsOn != reverse) c.Lights.AreReverseLightsOn = reverse;
    }

    static void ApplyWheels(FusionPlayer fp, Car c, float t)
    {
        if (c.Wheels == null || c.Wheels.Length == 0) return;
        var dt = c.LastT < 0 ? 0 : t - c.LastT; c.LastT = t;
        if (dt <= 0 || dt > 0.25f) { TrackPos(fp, c); return; }

        var steerIn = fp.CurrentFrameInput.Steering;
        const float maxSteer = 35f;
        var bodySpeed = ForwardSpeedMs(fp, c, dt);
        TrackPos(fp, c);

        for (int i = 0; i < c.Wheels.Length && i < c.SpinDeg.Length; i++)
        {
            var w = c.Wheels[i];
            if (w == null || w.WasCollected) continue;
            var wt = w.wheelTransform;
            if (wt == null) continue;
            float radius = 0.38f;
            try
            {
                var vb = fp.vehicleBase;
                if (vb != null && i < vb.wheelCount) { var rr = vb.GetWheelRadius(i); if (rr > 0.15f && rr < 1.2f) radius = rr; }
            }
            catch { }

            // Prefer per-wheel angular velocity from the host's NetworkedVehicleStatus (rad/s).
            var spun = false;
            try
            {
                var ang = c.Status != null ? c.Status.WheelsAngularVelocity : null;
                if (ang != null && i < ang.Count)
                {
                    c.SpinDeg[i] += ang[i] * Mathf.Rad2Deg * dt;
                    spun = true;
                }
            }
            catch { }
            if (!spun)
            {
                float wheelSpeed = bodySpeed;
                try
                {
                    var ws = c.Status != null ? c.Status.WheelsSpeed : null;
                    if (ws != null && i < ws.Count) wheelSpeed = ws[i];
                }
                catch { }
                c.SpinDeg[i] += (wheelSpeed / radius) * Mathf.Rad2Deg * dt;
            }
            if (c.SpinDeg[i] > 1e6f || c.SpinDeg[i] < -1e6f) c.SpinDeg[i] %= 360f;
            var steerDeg = w.m_isFrontWheel ? steerIn * maxSteer : 0f;
            // VPP visual convention on OTH cars: local X = roll, local Y = steer (matches WheelTransformUpdater's angles).
            wt.localRotation = Quaternion.Euler(c.SpinDeg[i], steerDeg, 0f);
        }
        WheelFrames++;
    }

    /// <summary>
    /// Forward speed (m/s) for visuals. Under RemoteInterpolate remote RBs are kinematic → linearVelocity ≈ 0
    /// while the transform still moves; use networked VehicleSpeed / VehicleBase.speed, else Δpose.
    /// </summary>
    static float ForwardSpeedMs(FusionPlayer fp, Car c, float dt)
    {
        try
        {
            if (c.Status != null && !c.Status.WasCollected)
            {
                var vs = c.Status.VehicleSpeed;
                // Networked field is signed along the car forward on OTH builds we tested; fall through if unset.
                if (Mathf.Abs(vs) > 0.01f) return vs;
            }
        }
        catch { }
        try
        {
            var vb = fp.vehicleBase;
            if (vb != null)
            {
                var sp = vb.speed;
                if (Mathf.Abs(sp) > 0.01f) return sp;
            }
        }
        catch { }
        if (dt > 1e-4f && c.HasPos)
        {
            Transform tr = null;
            try { tr = fp.transform; } catch { }
            if (tr != null)
            {
                var delta = tr.position - c.LastPos;
                return Vector3.Dot(delta, tr.forward) / dt;
            }
        }
        try
        {
            var rb = fp.rigidbody ?? fp.vehicleBase?.cachedRigidbody;
            if (rb != null) return Vector3.Dot(rb.linearVelocity, rb.transform.forward);
        }
        catch { }
        return 0f;
    }

    static void TrackPos(FusionPlayer fp, Car c)
    {
        try
        {
            var tr = fp.transform;
            if (tr == null) return;
            c.LastPos = tr.position;
            c.HasPos = true;
        }
        catch { }
    }

    /// <summary>
    /// Non-simulated proxies skip the Fusion callbacks that normally call ApplyNetworkedModLivery after
    /// EquippedModLivery* replicates. Re-apply when the networked ids change (or once on first sight).
    /// </summary>
    static void ApplyLivery(Car c)
    {
        if (c.Custom == null || c.Custom.WasCollected) return;
        long id;
        int sub;
        try
        {
            id = c.Custom.EquippedModLiveryId;
            sub = c.Custom.EquippedModLiverySubId;
        }
        catch { return; }
        if (id == c.AppliedLiveryId && sub == c.AppliedLiverySub) return;
        try
        {
            c.Custom.ApplyNetworkedModLivery();
            c.AppliedLiveryId = id;
            c.AppliedLiverySub = sub;
            LiveryApplies++;
            if (LiveryApplies <= 8)
                Plugin.L.LogInfo($"[RemoteVisuals] applied mod livery id={id} sub={sub}");
        }
        catch (Exception e)
        {
            Errors++;
            if (Errors < 8) Plugin.L.LogWarning($"[RemoteVisuals] livery id={id} sub={sub}: {e.Message}");
            // Do not stamp Applied*: retry next frames (mod download / Modio may still be loading).
        }
    }

    public static string Describe() =>
        $"remoteVisuals cars={Cars} lightSets={LightSets} wheelFrames={WheelFrames} liveryApplies={LiveryApplies} errors={Errors}";
}

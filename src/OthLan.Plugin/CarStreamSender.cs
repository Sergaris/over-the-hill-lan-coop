using System;
using UnityEngine;
using VehiclePhysics;

namespace OthLan;

/// <summary>
/// 0.2.0 T3.2: sample and send own car as OTC3 (parallel with OTP2 until T3.8).
/// RollDeg: integrated from signed body-forward spin (WheelVisualLogic). SuspM: WTU localY − restY when
/// rest position was captured near stop; else 0. SpinRadS: signed bodyV/r.
/// </summary>
internal static class CarStreamSender
{
    static readonly CarWire.CarStateSample[] _hist = new CarWire.CarStateSample[3];
    static int _histN;
    static uint _seq;
    static byte _epoch;
    static float _nextSend;
    static Vector3 _lastP;
    static bool _hasLastP;
    public static int Sent, EpochBumps;
    public static uint LastSeq { get; private set; }
    public static float LastPx { get; private set; }
    public static float LastPy { get; private set; }
    public static float LastPz { get; private set; }
    public static bool HasLastSample { get; private set; }
    static readonly byte[] _buf = new byte[CarWire.MaxPacketBytes];

    static WheelTransformUpdater[] _wheels = Array.Empty<WheelTransformUpdater>();
    static IntPtr _wheelFp;
    static readonly Vector3[] _restPos = new Vector3[CarWire.MaxWheels];
    static readonly bool[] _restPosOk = new bool[CarWire.MaxWheels];
    static readonly float[] _rollDeg = new float[CarWire.MaxWheels];
    static double _lastSampleT = -1;
    public static int SuspFromTransform, WheelsIntegrated;

    public static void Reset()
    {
        _histN = 0; _seq = 0; _epoch = 0; _nextSend = 0; _hasLastP = false; Sent = EpochBumps = 0;
        LastSeq = 0; LastPx = LastPy = LastPz = 0; HasLastSample = false;
        _wheels = Array.Empty<WheelTransformUpdater>(); _wheelFp = IntPtr.Zero; _lastSampleT = -1;
        SuspFromTransform = WheelsIntegrated = 0;
        for (var i = 0; i < CarWire.MaxWheels; i++) { _restPosOk[i] = false; _rollDeg[i] = 0; }
        SyncProbe.Reset();
    }

    public static void BumpEpoch(string why)
    {
        _epoch++; EpochBumps++;
        Plugin.L.LogInfo($"[Stream] epoch {_epoch} ({why})");
    }

    public static void Tick()
    {
        if (!Plugin.CarSyncEnabled.Value) return;
        if (Plugin.RelayCarState != null && !Plugin.RelayCarState.Value) return;
        if (LanState.Active == LanRole.Off) return;
        var hz = Plugin.CarSendHz.Value;
        if (hz < 1f) hz = 1f;
        var now = Time.realtimeSinceStartup;
        if (now < _nextSend) return;
        _nextSend = now + 1f / hz;

        if (!CarRegistry.TryGetOwn(out var car) || car.Rb == null) return;
        var rb = car.Rb;
        var p = rb.position;
        if (_hasLastP && (p - _lastP).sqrMagnitude > 25f) BumpEpoch("pose jump >5m");
        _lastP = p; _hasLastP = true;

        var s = Sample(car, rb);
        if (_histN < 3) _histN++;
        for (var i = _histN - 1; i > 0; i--) _hist[i] = _hist[i - 1];
        _hist[0] = s;

        var red = Plugin.CarStreamRedundancy.Value;
        if (red < 1) red = 1; if (red > 3) red = 3;
        var count = (byte)Math.Min(red, _histN);
        var slot = car.Slot >= 0 && car.Slot <= 254 ? (byte)car.Slot : (byte)0;
        var pkt = new CarWire.CarStatePacket { Slot = slot, Epoch = _epoch, Count = count };
        for (var i = 0; i < count; i++) pkt.SetState(i, _hist[i]);

        var n = CarWire.Encode(pkt, _buf);
        if (n <= 0) return;
        if (LanState.Active == LanRole.Host)
            CarRelay.InjectHost(pkt);
        else
        {
            var copy = new byte[n]; Array.Copy(_buf, copy, n);
            ClientLink.Send(copy);
        }
        LastSeq = s.Seq; LastPx = s.Px; LastPy = s.Py; LastPz = s.Pz; HasLastSample = true;
        if (LanState.Active != LanRole.Host)
            SyncProbe.Note(slot, s.Seq, s.Px, s.Py, s.Pz);
        Sent++;
    }

    static CarWire.CarStateSample Sample(in CarEntry car, Rigidbody rb)
    {
        var q = rb.rotation;
        var v = rb.linearVelocity;
        var w = rb.angularVelocity;
        // Signed forward speed → signed spin + reverse lights (matches reverse roll direction).
        var bodyV = Vector3.Dot(v, rb.transform.forward);
        byte flags = CarWire.FlagEngineOn;
        float steer = 0;
        float throttle = 0;
        try
        {
            var fp = car.Fp;
            if (fp != null)
            {
                var inp = fp.CurrentFrameInput;
                steer = inp.Steering * 45f;
                throttle = inp.Throttle;
                if (VehicleLightLogic.WantBrake(inp.Brake)) flags |= CarWire.FlagBrake;
                if (fp.ButtonDown(NetworkInputData.VehicleButtons.Headlights)) flags |= CarWire.FlagHeadlights;
                try
                {
                    var nvs = fp.GetComponent<NetworkedVehicleStatus>();
                    if (nvs != null && nvs.AllWheelsAirborne) flags |= CarWire.FlagAirborne;
                }
                catch { }
            }
        }
        catch { }
        if (VehicleLightLogic.WantReverse(bodyV, throttle)) flags |= CarWire.FlagReverse;
        var sample = new CarWire.CarStateSample
        {
            Seq = ++_seq,
            SendTime = NetClock.Now,
            Px = rb.position.x, Py = rb.position.y, Pz = rb.position.z,
            Qx = q.x, Qy = q.y, Qz = q.z, Qw = q.w,
            Vx = v.x, Vy = v.y, Vz = v.z,
            Wx = w.x, Wy = w.y, Wz = w.z,
            Flags = flags,
            SteerDeg = steer,
            WheelCount = 4,
        };
        FillWheels(car.Fp, ref sample, bodyV);
        return sample;
    }

    static void FillWheels(FusionPlayer fp, ref CarWire.CarStateSample sample, float bodyForwardMs)
    {
        var spin = bodyForwardMs / (float)WheelSpinLogic.DefaultRadius;
        var t = NetClock.Now;
        var dt = _lastSampleT < 0 ? 0f : (float)Math.Clamp(t - _lastSampleT, 0, 0.25);
        _lastSampleT = t;

        EnsureWheels(fp);
        var n = _wheels.Length > 0 ? Math.Min(CarWire.MaxWheels, _wheels.Length) : 4;
        sample.WheelCount = (byte)n;

        for (var i = 0; i < n; i++)
        {
            _rollDeg[i] = WheelVisualLogic.IntegrateRollDeg(_rollDeg[i], spin, dt);
            WheelsIntegrated++;
            float susp = 0;
            try
            {
                if (i < _wheels.Length)
                {
                    var w = _wheels[i];
                    if (w != null && !w.WasCollected)
                    {
                        var wt = w.wheelTransform;
                        if (wt != null)
                        {
                            MaybeCaptureRestPos(i, wt, bodyForwardMs);
                            if (_restPosOk[i])
                            {
                                susp = WheelVisualLogic.SuspDeltaM(_restPos[i].y, wt.localPosition.y);
                                SuspFromTransform++;
                            }
                        }
                    }
                }
            }
            catch { }

            sample.SetWheel(i, new CarWire.WheelSample { RollDeg = _rollDeg[i], SpinRadS = spin, SuspM = susp });
        }
    }

    static void EnsureWheels(FusionPlayer fp)
    {
        if (fp == null || fp.WasCollected) { _wheels = Array.Empty<WheelTransformUpdater>(); _wheelFp = IntPtr.Zero; return; }
        if (fp.Pointer == _wheelFp && _wheels != null) return;
        _wheelFp = fp.Pointer;
        for (var i = 0; i < CarWire.MaxWheels; i++) _restPosOk[i] = false;
        try { _wheels = fp.GetComponentsInChildren<WheelTransformUpdater>() ?? Array.Empty<WheelTransformUpdater>(); }
        catch { _wheels = Array.Empty<WheelTransformUpdater>(); }
    }

    static void MaybeCaptureRestPos(int i, Transform wt, float bodyForwardMs)
    {
        if (_restPosOk[i]) return;
        if (!WheelVisualLogic.CanCaptureRestPos(bodyForwardMs)) return;
        _restPos[i] = wt.localPosition;
        _restPosOk[i] = true;
    }
}

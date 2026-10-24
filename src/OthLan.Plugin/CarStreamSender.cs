using System;
using UnityEngine;

namespace OthLan;

/// <summary>
/// 0.2.0 T3.2: sample and send own car as OTC3 (parallel with OTP2 until T3.8).
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
    static readonly byte[] _buf = new byte[CarWire.MaxPacketBytes];

    public static void Reset()
    {
        _histN = 0; _seq = 0; _epoch = 0; _nextSend = 0; _hasLastP = false; Sent = EpochBumps = 0;
    }

    public static void BumpEpoch(string why)
    {
        _epoch++; EpochBumps++;
        Plugin.L.LogInfo($"[Stream] epoch {_epoch} ({why})");
    }

    public static void Tick()
    {
        if (!Plugin.CarSyncEnabled.Value) return;
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
        // newest first in hist
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
        Sent++;
    }

    static CarWire.CarStateSample Sample(in CarEntry car, Rigidbody rb)
    {
        var q = rb.rotation;
        var v = rb.linearVelocity;
        var w = rb.angularVelocity;
        byte flags = CarWire.FlagEngineOn;
        float steer = 0;
        try
        {
            var fp = car.Fp;
            if (fp != null)
            {
                var inp = fp.CurrentFrameInput;
                steer = inp.Steering * 45f;
                if (inp.Brake > 0.1f) flags |= CarWire.FlagBrake;
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

        var speed = v.magnitude;
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
        // T3.0 probe deferred: spin from body speed / WheelSpinLogic.DefaultRadius (single owner).
        var spin = speed / (float)WheelSpinLogic.DefaultRadius;
        for (var i = 0; i < 4; i++)
            sample.SetWheel(i, new CarWire.WheelSample { RollDeg = (spin * (float)NetClock.Now * (180f / Mathf.PI)) % 360f, SpinRadS = spin, SuspM = 0 });
        return sample;
    }
}

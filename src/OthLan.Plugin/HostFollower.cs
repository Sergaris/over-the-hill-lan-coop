using System;
using System.Collections.Generic;
using System.Net;
using Fusion;
using Fusion.Addons.Physics;
using UnityEngine;

namespace OthLan;

/// <summary>
/// 0.2.2 T5.1: physical proxy of a client car on the host.
/// Fresh OTC3 → <see cref="CarRelay.PlaybackOf"/> <c>SampleAt(now, delayOverride: 0)</c>
/// (freshness is <see cref="RemoteCarView.HasFreshStream"/>). Otherwise PosePlayback / OTP2, until T3.8.
/// Winch pause / <c>FlagSuspendWinch</c> only when <see cref="InteractionLogic.WantShared"/> (T4.2).
/// Servo length stays in <see cref="HostFollowLogic.ServoSpeed"/>.
/// </summary>
internal static class HostFollower
{
    sealed class Latest { public CarSyncLogic.Pose Pose; public double ReceivedAt, AdvancedAt; public uint LastAppliedSeq; }
    sealed class OutState
    {
        public bool InGap; public double GapMax; public OutageLogic.Phase Phase;
        public double ConvStart = -1, ConvT, WaitSince = -1, TrackSince = -1, TrackErr0;
    }
    struct Goal
    {
        public Vector3 Target, Vel, Ang;
        public Quaternion Rot;
        public CarSyncLogic.Pose Pose;
        public double Age;
        public bool FromStream, HasPose;
    }
    struct SlotWork
    {
        public int Slot;
        public Latest Latest;
        public PosePlayback.Target Play;
        public bool HasLatest;
    }
    sealed class Drive
    {
        public int Slot;
        public PlayerRef Pref;
        public FusionPlayer Car;
        public Rigidbody Rb;
        public SlotWork Work;
        public Goal Goal;
        public OutState St;
        public CarSyncLogic.Mode Mode;
        public double Rtt, Err, ErrDeg, Gap, Now;
        public float Dt;
        public bool Winch, Paused;
    }

    public static int FrozenClientPoses, PlaybackOk, PlaybackExtraps;
    public static int HostJumps, JumpsTowardClient, LowConverges, Servos, Teleports, AcceptsSent;
    public static int TestJumpSlot = -1;
    public static float CheckpointMinOverride = -1;
    public static readonly Dictionary<int, Msg> HostStats = new();
    public static Msg ClientStat = Msg.Raw("");
    public static int Snaps, Blends, ModeSwitches, PosesSent, PosesReceived, PosesRejected, TeleportsIgnored,
        SoftSnaps, CheckpointsSkipped, ContactPauses;
    public static readonly Dictionary<int, string> LastAction = new();
    public static readonly Dictionary<int, (string mode, double rtt, double err, double errDeg, double poseAge)> Metrics = new();
    public static int Gaps, Recoveries, RecoverQuiet, RecoverConverge, RecoverSnap, RecoverWait, Converges, Extrapolations, Brakes;
    public static string LastRecovery = "";
    public static readonly List<double> ConvergeTimes = new();
    public static int SnapLoopsBroken, WinchPauses;

    static readonly object _lock = new();
    static readonly Dictionary<int, Latest> _poses = new();
    static readonly Dictionary<int, PosePlayback> _playback = new();
    static readonly Dictionary<int, CarSyncLogic.Mode> _mode = new();
    static readonly Dictionary<int, double> _lastCheckpoint = new();
    static readonly Dictionary<int, double> _softUntil = new();
    static readonly Dictionary<int, double> _lastContact = new();
    static readonly Dictionary<int, double> _selfMoveUntil = new();
    const double SelfMoveHoldSeconds = 0.5;
    static readonly Dictionary<int, (Vector3 p, Quaternion q)> _lastHost = new();
    static readonly Dictionary<int, double> _jumpHoldUntil = new();
    static readonly Dictionary<int, ushort> _acceptCtr = new();
    static readonly Dictionary<int, OutState> _out = new();
    static readonly Dictionary<int, (double at, int n)> _snapStreak = new();
    static readonly List<SlotWork> _work = new();
    static readonly Drive _drv = new();
    static FusionPlayer[] _players = Array.Empty<FusionPlayer>();
    static VehicleWinchSystem[] _winches = Array.Empty<VehicleWinchSystem>();
    static float _nextScan, _nextSend;
    static uint _seq;

    /// <summary>Frame tick: legacy OTP2 send on a client, host follow on the host.</summary>
    public static void Tick()
    {
        var r = LanState.Runner();
        if (r == null) { CarSync.NoteNoRunner(); Reset(); return; }
        if (!CarSync.Enabled)
        {
            Reset();
            ClientStat = LanState.Active == LanRole.Client ? new Msg(CarSync.HostFlag < 0 ? "cs.waitHost" : "cs.offByHost") : Msg.Raw("");
            return;
        }
        var now = Time.realtimeSinceStartup;
        Scan(now);
        try
        {
            if (LanState.Active == LanRole.Client) ClientSend(r, now);
            else HostApply(r);
        }
        catch (Exception e) { Plugin.L.LogWarning($"[CarSync] {e.Message}"); _nextScan = 0; }
    }

    public static void Reset()
    {
        lock (_lock) { _poses.Clear(); _playback.Clear(); }
        PlaybackOk = PlaybackExtraps = 0;
        _out.Clear();
        _mode.Clear(); _lastCheckpoint.Clear(); _softUntil.Clear(); _lastContact.Clear(); _lastHost.Clear();
        _selfMoveUntil.Clear(); _jumpHoldUntil.Clear(); HostStats.Clear(); Metrics.Clear(); LastAction.Clear(); _winchTags.Clear();
    }
    static readonly Dictionary<int, string> _winchTags = new();

    /// <summary>Network thread (HostInfo): OTP2 pose, until T3.8.</summary>
    public static void OnPosePacket(byte[] data, IPEndPoint from)
    {
        if (LanState.LanRunnerRole != LanRole.Host || !CarSync.HostEnabled) return;
        if (!CarSyncLogic.TryDecode(data, out var p)) return;
        var ep = HostPings.EndpointOf(p.Slot);
        if (ep == null || !ep.Equals(from)) { PosesRejected++; return; }
        var now = NetClock.Now;
        lock (_lock)
        {
            if (_poses.TryGetValue(p.Slot, out var l) && !CarSyncLogic.AcceptSeq(p.Seq, l.Pose.Seq, now - l.ReceivedAt)) return;
            var adv = l == null || p.Tick == 0 || p.Tick != l.Pose.Tick ? now : l.AdvancedAt;
            if (adv != now) FrozenClientPoses++;
            _poses[p.Slot] = new Latest { Pose = p, ReceivedAt = now, AdvancedAt = adv, LastAppliedSeq = l?.LastAppliedSeq ?? 0 };
            if (Plugin.CarPosePlayback.Value) PushPlayback(p.Slot, p, now);
            PosesReceived++;
        }
    }

    public static bool WinchInvolved(FusionPlayer car, PlayerRef pref) => WinchKindOf(car, pref) != WinchKindLogic.Kind.None;

    public static WinchKindLogic.Kind WinchKindOf(FusionPlayer car, PlayerRef pref, out int otherSlot)
    {
        otherSlot = -1;
        try
        {
            var ws = _winches; var ps = _players;
            bool ownRope = false, ownToPlayer = false, towed = false;
            for (int i = 0; i < ps.Length && i < ws.Length; i++)
            {
                var w = ws[i]; if (w == null || ps[i] == null) continue;
                if (!w.WinchIsAttached) continue;
                if (ps[i] == car)
                {
                    ownRope = true;
                    try
                    {
                        var wr = w.WinchedCarPlayerRef;
                        if (wr.IsRealPlayer) { ownToPlayer = true; otherSlot = wr.PlayerId; }
                    }
                    catch { }
                }
                else if (w.WinchedCarPlayerRef == pref)
                {
                    towed = true;
                    try { otherSlot = ps[i].Object != null ? ps[i].Object.InputAuthority.PlayerId : i; } catch { otherSlot = i; }
                }
            }
            return WinchKindLogic.Classify(ownRope, ownToPlayer, towed);
        }
        catch { }
        return WinchKindLogic.Kind.None;
    }

    public static WinchKindLogic.Kind WinchKindOf(FusionPlayer car, PlayerRef pref) => WinchKindOf(car, pref, out _);

    public static VehicleWinchSystem OwnWinch(out FusionPlayer car)
    {
        car = null;
        var ws = _winches; var ps = _players;
        for (int i = 0; i < ps.Length && i < ws.Length; i++)
            if (ps[i] != null && ps[i].Object != null && ps[i].Object.HasInputAuthority) { car = ps[i]; return ws[i]; }
        return null;
    }

    static void PushPlayback(int slot, CarSyncLogic.Pose p, double now)
    {
        if (!_playback.TryGetValue(slot, out var pb)) _playback[slot] = pb = new PosePlayback();
        pb.Push(new PosePlayback.Sample
        {
            T = now, Tick = p.Tick, Seq = p.Seq,
            Px = p.Px, Py = p.Py, Pz = p.Pz, Vx = p.Vx, Vy = p.Vy, Vz = p.Vz,
            Rx = p.Rx, Ry = p.Ry, Rz = p.Rz, Rw = p.Rw, Ax = p.Ax, Ay = p.Ay, Az = p.Az,
        });
    }

    static void Scan(float now)
    {
        if (now < _nextScan) return;
        _nextScan = now + 1f;
        _players = CarRegistry.Players;
        var w = new VehicleWinchSystem[_players.Length];
        for (int i = 0; i < _players.Length; i++)
        {
            try { w[i] = WinchOf(_players[i]); }
            catch { w[i] = null; }
        }
        _winches = w;
    }

    /// <summary>The winch of a player car (it may sit on a child or a parent object), or null.</summary>
    public static VehicleWinchSystem WinchOf(FusionPlayer fp) =>
        fp == null ? null : fp.GetComponentInChildren<VehicleWinchSystem>() ?? fp.GetComponentInParent<VehicleWinchSystem>();

    static void ClientSend(NetworkRunner r, float now)
    {
        if (now < _nextSend) return;
        _nextSend = now + 1f / Mathf.Clamp(Plugin.CarSendHz.Value, 2f, 60f);
        foreach (var fp in _players)
        {
            if (fp == null || fp.Object == null || !fp.Object.HasInputAuthority) continue;
            var rb = CarSync.Body(fp); if (rb == null) return;
            var pos = rb.position; var rot = rb.rotation; var v = rb.linearVelocity; var a = rb.angularVelocity;
            var pose = new CarSyncLogic.Pose
            {
                Slot = (byte)LanState.ClientSlot, Seq = ++_seq, Tick = r.Tick.Raw,
                Px = pos.x, Py = pos.y, Pz = pos.z, Rx = rot.x, Ry = rot.y, Rz = rot.z, Rw = rot.w,
                Vx = v.x, Vy = v.y, Vz = v.z, Ax = a.x, Ay = a.y, Az = a.z,
            };
            ClientLink.Ensure(LanState.TargetIp, LanState.TargetPort + 1);
            ClientLink.Send(CarSyncLogic.Encode(pose));
            PosesSent++;
            ClientStat = new Msg("cs.onByHost", Plugin.CarSendHz.Value);
            return;
        }
        ClientStat = new Msg("cs.noCarYet");
    }

    static void HostApply(NetworkRunner r)
    {
        var cfg = Cfg();
        var ocfg = OCfg();
        var nowS = NetClock.Now;
        var dt = Time.unscaledDeltaTime;
        Collect(nowS);
        if (_work.Count == 0) return;
        var pings = HostPings.Snapshot();
        var hostTick = r.Tick.Raw; var tickDt = (double)r.DeltaTime;
        try
        {
            using (FrameCost.Measure(FrameCost.HostFollow))
                for (int i = 0; i < _work.Count; i++)
                    ApplyOne(r, _work[i], cfg, ocfg, nowS, dt, pings, hostTick, tickDt);
        }
        finally { NetTrace.FollowFlush(LastAction, dt); }
    }

    static void Collect(double nowS)
    {
        _work.Clear();
        lock (_lock)
        {
            foreach (var kv in _poses)
            {
                var play = default(PosePlayback.Target);
                if (_playback.TryGetValue(kv.Key, out var pb))
                {
                    play = pb.SampleAt(nowS);
                    if (play.Ok) PlaybackOk++;
                    if (play.Extrapolated) PlaybackExtraps++;
                }
                _work.Add(new SlotWork { Slot = kv.Key, Latest = kv.Value, Play = play, HasLatest = true });
            }
        }
        var now = NetClock.Now;
        foreach (var fp in _players)
        {
            if (fp == null || fp.Object == null) continue;
            try { if (fp.Object.HasInputAuthority) continue; } catch { continue; }
            int slot;
            try { slot = fp.Object.InputAuthority.AsIndex; } catch { continue; }
            if (slot < 0 || Tracking(slot) || !RemoteCarView.HasFreshStream(slot, now)) continue;
            _work.Add(new SlotWork { Slot = slot });
        }
    }

    static bool Tracking(int slot)
    {
        for (int i = 0; i < _work.Count; i++) if (_work[i].Slot == slot) return true;
        return false;
    }

    static void ApplyOne(NetworkRunner r, SlotWork w, CarSyncLogic.Cfg cfg, OutageLogic.Cfg ocfg, double nowS, float dt,
        Dictionary<int, (double ms, bool timedOut, double loss, double noLinkS)> pings, int hostTick, double tickDt)
    {
        var d = _drv;
        if (!Bind(r, w.Slot, d)) return;
        d.Work = w; d.Now = nowS; d.Dt = dt;
        MaybeTestJump(d);
        d.Rtt = RttOf(r, d.Pref, w.Slot, pings);
        d.Mode = UpdateMode(w.Slot, d.Rtt, cfg);
        if (!SelectTarget(w.Slot, w, hostTick, tickDt, out d.Goal)) return;
        d.Err = Vector3.Distance(d.Rb.position, d.Goal.Target);
        d.ErrDeg = Quaternion.Angle(d.Rb.rotation, d.Goal.Rot);
        d.Gap = d.Goal.FromStream ? 0 : nowS - w.Latest.AdvancedAt;
        d.St = OutOf(w.Slot);
        TakeGameMove(d, pings);
        RememberTrace(d);
        PublishMetrics(d, cfg);
        if (HoldJump(d)) return;
        // T4.2: pause follow + FlagSuspendWinch only when InteractionLogic wants HostShared (car tow,
        // or world winch with WorldWinchAuthority=Host). World+Owner keeps stream follow (D-1).
        var winchKind = WinchKindOf(d.Car, d.Pref, out var winchSlot);
        var winchTag = WinchKindLogic.LogTag(winchKind, winchSlot);
        var wasTag = _winchTags.TryGetValue(d.Slot, out var wt) ? wt : "none";
        if (wasTag != winchTag)
        {
            _winchTags[d.Slot] = winchTag;
            Plugin.L.LogInfo($"[CarSync] slot {d.Slot} winch: {wasTag} -> {winchTag}");
        }
        d.Winch = Plugin.SuspendOnWinch.Value
            && InteractionLogic.WantShared(false, false, winchKind, WinchKindLogic.Kind.None, OwnCar.InteractCfg());
        ArmContact(d);
        if (ApplyOutage(d, ocfg, hostTick, tickDt)) return;
        if (ApplyRecovery(d, ocfg)) return;
        ApplyFollow(d, cfg, ocfg);
    }

    static bool Bind(NetworkRunner r, int slot, Drive d)
    {
        d.Slot = slot;
        var pref = PlayerRef.FromIndex(slot);
        d.Pref = pref;
        if (!r.IsPlayerValid(pref)) return false;
        FusionPlayer car = null;
        foreach (var fp in _players)
            if (fp != null && fp.Object != null && fp.Object.InputAuthority == pref) { car = fp; break; }
        if (car == null) { HostStats[slot] = new Msg("cs.slotNoCar", slot); return false; }
        var rb = CarSync.Body(car); if (rb == null) return false;
        d.Car = car; d.Rb = rb;
        return true;
    }

    static void MaybeTestJump(Drive d)
    {
        if (TestJumpSlot != d.Slot) return;
        TestJumpSlot = -1;
        d.Rb.position += d.Rb.rotation * new Vector3(0, 1.5f, 6f);
        Plugin.L.LogWarning($"[CarSync] self-test: host moved slot {d.Slot} by 6 m");
    }

    static double RttOf(NetworkRunner r, PlayerRef pref, int slot, Dictionary<int, (double ms, bool timedOut, double loss, double noLinkS)> pings)
    {
        if (pings.TryGetValue(slot, out var hp)) return hp.timedOut ? double.NaN : hp.ms;
        return r.GetPlayerRtt(pref) * 1000.0;
    }

    static CarSyncLogic.Mode UpdateMode(int slot, double rtt, CarSyncLogic.Cfg cfg)
    {
        var prev = _mode.TryGetValue(slot, out var m) ? m : CarSyncLogic.Mode.Low;
        var mode = CarSyncLogic.NextMode(prev, rtt, cfg);
        if (mode != prev) { ModeSwitches++; Plugin.L.LogInfo($"[CarSync] slot {slot}: mode {prev} -> {mode} at rtt {rtt:0} ms"); }
        _mode[slot] = mode;
        return mode;
    }

    /// <summary>Stream present when fresh; else OTP2 / PosePlayback tick-age lead.</summary>
    static bool SelectTarget(int slot, SlotWork w, int hostTick, double tickDt, out Goal g)
    {
        if (TryStream(slot, out g))
        {
            if (w.HasLatest) { g.Pose = w.Latest.Pose; g.HasPose = true; }
            return true;
        }
        if (!w.HasLatest) return false;
        var p = w.Latest.Pose;
        g.Pose = p; g.HasPose = true;
        if (w.Play.Ok)
            PoseLead(w.Play.Px, w.Play.Py, w.Play.Pz, w.Play.Vx, w.Play.Vy, w.Play.Vz, w.Play.Rx, w.Play.Ry, w.Play.Rz, w.Play.Rw,
                w.Play.Ax, w.Play.Ay, w.Play.Az, w.Play.Tick > 0 ? w.Play.Tick : p.Tick, hostTick, tickDt, ref g);
        else
            PoseLead(p.Px, p.Py, p.Pz, p.Vx, p.Vy, p.Vz, p.Rx, p.Ry, p.Rz, p.Rw, p.Ax, p.Ay, p.Az, p.Tick, hostTick, tickDt, ref g);
        return true;
    }

    static bool TryStream(int slot, out Goal g)
    {
        g = default;
        var now = NetClock.Now;
        if (!RemoteCarView.HasFreshStream(slot, now)) return false;
        var pb = CarRelay.PlaybackOf(slot);
        var s = pb.SampleAt(now, delayOverride: 0);
        // Fresh arrival but past MaxExtrap → still hold OTC3 (vel already 0) instead of falling back to OTP2.
        if (!s.Ok) return false;
        g.Target = new Vector3(s.Px, s.Py, s.Pz);
        g.Rot = new Quaternion(s.Qx, s.Qy, s.Qz, s.Qw);
        g.Vel = new Vector3(s.Vx, s.Vy, s.Vz);
        g.Ang = new Vector3(s.Wx, s.Wy, s.Wz);
        g.Age = Math.Max(0, now - pb.LastArrivalLocal);
        g.FromStream = true;
        return true;
    }

    static void PoseLead(double px, double py, double pz, double vx, double vy, double vz, double rx, double ry, double rz, double rw,
        double ax, double ay, double az, int tick, int hostTick, double tickDt, ref Goal g)
    {
        g.Age = CarSyncLogic.PoseAgeFromTicks(hostTick, tick, tickDt);
        g.Target = new Vector3((float)CarSyncLogic.Extrapolate(px, vx, g.Age), (float)CarSyncLogic.Extrapolate(py, vy, g.Age), (float)CarSyncLogic.Extrapolate(pz, vz, g.Age));
        g.Vel = new Vector3((float)vx, (float)vy, (float)vz);
        g.Ang = new Vector3((float)ax, (float)ay, (float)az);
        g.Rot = new Quaternion((float)rx, (float)ry, (float)rz, (float)rw);
        var turn = g.Ang.magnitude * (float)g.Age;
        if (turn > 1e-5f) g.Rot = Quaternion.AngleAxis(turn * Mathf.Rad2Deg, g.Ang.normalized) * g.Rot;
    }

    static void TakeGameMove(Drive d, Dictionary<int, (double ms, bool timedOut, double loss, double noLinkS)> pings)
    {
        var slot = d.Slot; var rb = d.Rb; var nowS = d.Now;
        if (!_lastHost.TryGetValue(slot, out var lh)) return;
        if (_selfMoveUntil.TryGetValue(slot, out var smu) && nowS < smu) return;
        var moved = Vector3.Distance(rb.position, lh.p); var turned = Quaternion.Angle(rb.rotation, lh.q);
        var speed = rb.linearVelocity.magnitude;
        if (!CarSyncLogic.HostJumped(moved, turned, speed, d.Dt)) return;
        var before = Vector3.Distance(lh.p, d.Goal.Target); var beforeDeg = Quaternion.Angle(lh.q, d.Goal.Rot);
        if (!HostFollowLogic.GameMovedCar(moved, turned, speed, d.Dt, before, d.Err, beforeDeg, d.ErrDeg))
        {
            JumpsTowardClient++;
            if (JumpsTowardClient <= 5) Plugin.L.LogInfo($"[CarSync] slot {slot}: host copy jumped {moved:0.0} m / {turned:0} deg toward the client's pose (physics, not the game): no OTAC");
            return;
        }
        HostJumps++;
        if (Plugin.CarTeleportOnGameMove.Value) FlagTeleport(d.Car, rb);
        var hold = CarSyncLogic.JumpHoldSeconds(pings.TryGetValue(slot, out var hj) && !hj.timedOut ? hj.ms : double.NaN);
        _jumpHoldUntil[slot] = nowS + hold;
        Plugin.L.LogInfo($"[CarSync] slot {slot}: moved by the game on the host ({moved:0.0} m, {turned:0} deg, now {d.Err:0.0} m from the client's pose); hands off for {hold:0.00} s");
        SendAccept(slot);
    }

    static void FlagTeleport(FusionPlayer car, Rigidbody rb)
    {
        try
        {
            var nrb = car.GetComponent<NetworkRigidbody3D>();
            if (nrb != null) { nrb.Teleport(new Il2CppSystem.Nullable<Vector3>(rb.position), new Il2CppSystem.Nullable<Quaternion>(rb.rotation)); Teleports++; }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[CarSync] teleport flag: {e.Message}"); }
    }

    static void RememberTrace(Drive d)
    {
        if (!NetTrace.On) return;
        NetTrace.Pending[d.Slot] = new NetTrace.FollowIn
        {
            Rb = d.Rb, Pre = d.Rb.position, Target = d.Goal.Target, ClientVel = d.Goal.Vel,
            Err = d.Err, ErrDeg = d.ErrDeg, Age = d.Goal.Age, Gap = d.Gap,
        };
    }

    static void PublishMetrics(Drive d, CarSyncLogic.Cfg cfg)
    {
        Metrics[d.Slot] = (d.Mode.ToString(), d.Rtt, d.Err, d.ErrDeg, d.Goal.Age);
        var l = d.Work.Latest;
        if (d.Work.HasLatest && d.Err > cfg.TeleportMeters && d.Now - l.ReceivedAt <= cfg.MaxPoseAgeSeconds && l.LastAppliedSeq != l.Pose.Seq)
            TeleportsIgnored++;
    }

    static bool HoldJump(Drive d)
    {
        if (!(_jumpHoldUntil.TryGetValue(d.Slot, out var jh) && d.Now < jh)) return false;
        _lastHost[d.Slot] = (d.Rb.position, d.Rb.rotation);
        _lastCheckpoint[d.Slot] = d.Now;
        LastAction[d.Slot] = "jump-hold";
        d.St.ConvStart = -1; d.St.WaitSince = -1;
        HostStats[d.Slot] = new Msg("cs.slotGameMoved", d.Slot);
        return true;
    }

    static void ArmContact(Drive d)
    {
        var slot = d.Slot; var nowS = d.Now;
        var contactAuthHost = string.Equals(Plugin.ContactAuthority?.Value, "Host", StringComparison.OrdinalIgnoreCase);
        var touching = Plugin.CarContactPause.Value && contactAuthHost && TouchesOtherCar(d.Car, d.Rb);
        var contactNow = d.Winch || touching;
        byte sus = 0;
        if (d.Winch) sus |= CarSyncLogic.FlagSuspendWinch;
        else if (touching) sus |= CarSyncLogic.FlagSuspendContact;
        CarSync.SetSlotSuspend(slot, sus);
        if (d.Winch && !(LastAction.TryGetValue(slot, out var la) && la == "winch")) WinchPauses++;
        if (contactNow && !CarSyncLogic.ContactPaused(nowS, _lastContact.TryGetValue(slot, out var lc0) ? lc0 : 0, Plugin.CarContactHoldSeconds.Value))
            ContactPauses++;
        if (contactNow) _lastContact[slot] = nowS;
        d.Paused = CarSyncLogic.ContactPaused(nowS, _lastContact.TryGetValue(slot, out var lc) ? lc : 0, Plugin.CarContactHoldSeconds.Value);
        if (contactNow && NetTrace.Pending.TryGetValue(slot, out var tp)) { tp.Contact = true; NetTrace.Pending[slot] = tp; }
    }

    static bool ApplyOutage(Drive d, OutageLogic.Cfg ocfg, int hostTick, double tickDt)
    {
        var phase = OutageLogic.PhaseOf(d.Gap, ocfg);
        if (phase == OutageLogic.Phase.Normal) return false;
        var slot = d.Slot; var st = d.St; var g = d.Gap;
        if (!st.InGap) { st.InGap = true; Gaps++; Plugin.L.LogInfo($"[CarSync] slot {slot}: no poses for {g:0.00} s -> {phase}"); }
        if (st.Phase != phase) { if (st.Phase != OutageLogic.Phase.Normal) Plugin.L.LogInfo($"[CarSync] slot {slot}: gap {g:0.0} s -> {phase}"); st.Phase = phase; }
        st.GapMax = g; st.ConvStart = -1; st.WaitSince = -1; st.TrackSince = -1;
        if (CarSync.ApplyEnabled && !d.Winch && !d.Paused) ChaseOutage(d, phase, ocfg, hostTick, tickDt);
        LastAction[slot] = d.Winch ? "winch" : d.Paused ? "contact" : PhaseName(phase);
        _lastHost[slot] = (d.Rb.position, d.Rb.rotation);
        HostStats[slot] = new Msg("cs.slotNoData", slot, g, PhaseMsg(phase));
        return true;
    }

    static void ChaseOutage(Drive d, OutageLogic.Phase phase, OutageLogic.Cfg ocfg, int hostTick, double tickDt)
    {
        var g = d.Goal;
        if (phase == OutageLogic.Phase.Extrapolate && Plugin.CarFollowVelocity.Value)
        { ApplyVelocityFollow(d.Rb, g.Target, g.Rot, g.Vel, g.Ang, d.Dt); Extrapolations++; return; }
        if (d.Work.HasLatest) HostOutage(d.Rb, phase, d.Gap, d.Work.Latest.Pose, hostTick, tickDt, d.Dt, ocfg, d.Car);
    }

    static bool ApplyRecovery(Drive d, OutageLogic.Cfg ocfg)
    {
        var st = d.St;
        if (st.InGap) BeginRecovery(d, ocfg);
        if (ContinueRecovery(d, ocfg)) return true;
        TrackConvergence(d.Slot, st, d.Err, d.Now);
        return false;
    }

    static void BeginRecovery(Drive d, OutageLogic.Cfg ocfg)
    {
        var st = d.St; var rb = d.Rb; var slot = d.Slot;
        st.InGap = false; st.Phase = OutageLogic.Phase.Normal;
        var hostUp = (rb.rotation * Vector3.up).y; var cliUp = (d.Goal.Rot * Vector3.up).y;
        var rec = HostFollowLogic.Recover(d.Err, d.ErrDeg, d.Paused, rb.linearVelocity.magnitude, ocfg, out var T);
        if (rec != OutageLogic.Recovery.Teleport && OutageLogic.KeepUpright(d.Err, hostUp, cliUp)) rec = OutageLogic.Recovery.None;
        Recoveries++; LastRecovery = $"{rec} gap {st.GapMax:0.00}s err {d.Err:0.00}m {d.ErrDeg:0}°";
        Plugin.L.LogInfo($"[CarSync] slot {slot}: link back after {st.GapMax:0.00} s, error {d.Err:0.00} m / {d.ErrDeg:0} deg -> {rec}{(T > 0 ? $" over {T:0.00} s" : "")}");
        st.TrackSince = rec == OutageLogic.Recovery.Teleport ? -1 : d.Now; st.TrackErr0 = d.Err;
        switch (rec)
        {
            case OutageLogic.Recovery.Converge: st.ConvStart = d.Now; st.ConvT = T; RecoverConverge++; break;
            case OutageLogic.Recovery.WaitContact: st.WaitSince = d.Now; RecoverWait++; break;
            case OutageLogic.Recovery.Snap:
                RecoverSnap++;
                if (CarSync.ApplyEnabled) DoSnap(slot, d.Car, rb, d.Goal, d.Now, $"recovery snap {d.Err:0.0} m / {d.ErrDeg:0} deg");
                break;
            case OutageLogic.Recovery.None: RecoverQuiet++; break;
        }
    }

    static bool ContinueRecovery(Drive d, OutageLogic.Cfg ocfg)
    {
        var st = d.St; var slot = d.Slot; var rb = d.Rb; var nowS = d.Now;
        if (st.WaitSince >= 0 && WaitContact(d, ocfg)) return true;
        if (st.ConvStart < 0) return false;
        var e = nowS - st.ConvStart;
        if (e >= st.ConvT) { st.ConvStart = -1; _lastCheckpoint[slot] = nowS; _softUntil.Remove(slot); return false; }
        if (CarSync.ApplyEnabled && !d.Winch && !d.Paused) ConvergeStep(d, e);
        LastAction[slot] = d.Winch ? "winch" : d.Paused ? "contact" : "converge";
        TrackConvergence(slot, st, d.Err, nowS);
        _lastHost[slot] = (rb.position, rb.rotation);
        HostStats[slot] = new Msg("cs.slotLinkBack", slot, d.Err);
        return true;
    }

    static bool WaitContact(Drive d, OutageLogic.Cfg ocfg)
    {
        var st = d.St; var rb = d.Rb;
        var rec = OutageLogic.Decide(d.Err, d.ErrDeg, d.Paused, d.Now - st.WaitSince, rb.linearVelocity.magnitude, ocfg, out var T);
        if (rec == OutageLogic.Recovery.WaitContact)
        {
            LastAction[d.Slot] = "wait-contact";
            _lastHost[d.Slot] = (rb.position, rb.rotation);
            HostStats[d.Slot] = new Msg("cs.slotWaitContact", d.Slot);
            return true;
        }
        st.WaitSince = -1;
        if (rec == OutageLogic.Recovery.Snap && CarSync.ApplyEnabled)
            DoSnap(d.Slot, d.Car, rb, d.Goal, d.Now, $"recovery snap after contact {d.Err:0.0} m");
        else if (rec == OutageLogic.Recovery.Converge) { st.ConvStart = d.Now; st.ConvT = T; }
        return false;
    }

    static void ConvergeStep(Drive d, double e)
    {
        var (kvr, kp) = OutageLogic.ConvergeFactors(e, d.St.ConvT, d.Dt);
        var rb = d.Rb; var g = d.Goal;
        rb.linearVelocity = Vector3.Lerp(rb.linearVelocity, g.Vel, (float)kvr);
        rb.angularVelocity = Vector3.Lerp(rb.angularVelocity, g.Ang, (float)kvr);
        rb.rotation = Quaternion.Slerp(rb.rotation, g.Rot, (float)kvr);
        rb.position = Vector3.Lerp(rb.position, g.Target, (float)kp);
        Converges++;
        _selfMoveUntil[d.Slot] = d.Now + SelfMoveHoldSeconds;
    }

    static void ApplyFollow(Drive d, CarSyncLogic.Cfg cfg, OutageLogic.Cfg ocfg)
    {
        var slot = d.Slot; var nowS = d.Now;
        var since = nowS - (_lastCheckpoint.TryGetValue(slot, out var c) ? c : 0);
        var soft = _softUntil.TryGetValue(slot, out var su) && nowS < su;
        var act = CarSync.ApplyEnabled
            ? CarSyncLogic.Decide(d.Mode, d.Err, d.ErrDeg, since, d.Gap, cfg, d.Paused, soft)
            : CarSyncLogic.Action.None;
        act = AdjustSnap(d, act, ocfg);
        if (d.Paused && CarSync.ApplyEnabled) { _lastCheckpoint[slot] = nowS; _softUntil.Remove(slot); }
        LastAction[slot] = d.Winch ? "winch" : d.Paused ? "contact" : act.ToString();
        if (act == CarSyncLogic.Action.CheckpointOk) { _lastCheckpoint[slot] = nowS; CheckpointsSkipped++; }
        if (act == CarSyncLogic.Action.SoftSnap) { _softUntil[slot] = nowS + cfg.CheckpointSoftSeconds; _lastCheckpoint[slot] = nowS; SoftSnaps++; }
        if (act == CarSyncLogic.Action.SoftSnap || act == CarSyncLogic.Action.SoftBlend) Blend(d, (float)CarSyncLogic.BlendFactor(d.Dt, cfg.CheckpointSoftSeconds / 5.0), false);
        else if (act == CarSyncLogic.Action.Blend && Plugin.CarFollowVelocity.Value) ApplyVelocityFollow(d.Rb, d.Goal.Target, d.Goal.Rot, d.Goal.Vel, d.Goal.Ang, d.Dt);
        else if (act == CarSyncLogic.Action.Blend) Blend(d, (float)CarSyncLogic.BlendFactor(d.Dt, Plugin.CarBlendTau.Value), true);
        else if (act == CarSyncLogic.Action.Snap) ApplySnap(d, cfg);
        FinishLive(d);
    }

    static CarSyncLogic.Action AdjustSnap(Drive d, CarSyncLogic.Action act, OutageLogic.Cfg ocfg)
    {
        var rb = d.Rb; var st = d.St;
        if (act == CarSyncLogic.Action.Snap && d.ErrDeg > ocfg.FlippedDegrees && rb.linearVelocity.magnitude >= 2 && d.Err <= ocfg.SnapMeters)
        { st.ConvStart = d.Now; st.ConvT = 0.5; return CarSyncLogic.Action.None; }
        if (act == CarSyncLogic.Action.Snap && OutageLogic.KeepUpright(d.Err, (rb.rotation * Vector3.up).y, (d.Goal.Rot * Vector3.up).y))
            return CarSyncLogic.Action.None;
        if (act == CarSyncLogic.Action.Snap && d.Err <= ocfg.SnapMeters)
        { st.ConvStart = d.Now; st.ConvT = OutageLogic.ConvergeSeconds(d.Err); st.TrackSince = d.Now; st.TrackErr0 = d.Err; LowConverges++; return CarSyncLogic.Action.None; }
        return act;
    }

    static void ApplySnap(Drive d, CarSyncLogic.Cfg cfg)
    {
        if (SnapLoop(d.Slot, d.Now))
        {
            SendAccept(d.Slot); SnapLoopsBroken++;
            _jumpHoldUntil[d.Slot] = d.Now + 1.0;
            Plugin.L.LogWarning($"[CarSync] slot {d.Slot}: snaps do not stick ({d.Err:0.0} m); client takes the host pose");
            return;
        }
        var loud = d.Err > cfg.DesyncSnapMeters || d.ErrDeg > cfg.DesyncSnapDegrees || d.Mode == CarSyncLogic.Mode.Low;
        DoSnap(d.Slot, d.Car, d.Rb, d.Goal, d.Now, loud ? $"{d.Mode} snap {d.Err:0.0} m / {d.ErrDeg:0} deg" : null);
    }

    static void Blend(Drive d, float k, bool count)
    {
        var rb = d.Rb; var g = d.Goal;
        rb.position = Vector3.Lerp(rb.position, g.Target, k);
        rb.rotation = Quaternion.Slerp(rb.rotation, g.Rot, k);
        rb.linearVelocity = Vector3.Lerp(rb.linearVelocity, g.Vel, k);
        rb.angularVelocity = Vector3.Lerp(rb.angularVelocity, g.Ang, k);
        if (count) Blends++;
    }

    static void FinishLive(Drive d)
    {
        var how = new Msg(!CarSync.ApplyEnabled ? "cs.howMeasure" : d.Winch ? "cs.howWinch" : d.Paused ? "cs.howContact"
            : d.Mode == CarSyncLogic.Mode.Low ? "cs.howSmooth" : "cs.howCheckpoints");
        var slot = d.Slot;
        if (d.Work.HasLatest)
        {
            var seq = d.Work.Latest.Pose.Seq;
            lock (_lock) if (_poses.TryGetValue(slot, out var cur) && cur.Pose.Seq == seq) cur.LastAppliedSeq = seq;
        }
        _lastHost[slot] = (d.Rb.position, d.Rb.rotation);
        HostStats[slot] = new Msg("cs.slotLive", slot, how, double.IsNaN(d.Rtt) ? "--" : d.Rtt.ToString("0"), d.Err);
    }

    static void ApplyVelocityFollow(Rigidbody rb, Vector3 target, Quaternion trot, Vector3 vel, Vector3 ang, float dt)
    {
        var e = target - rb.position; var el = e.magnitude;
        var sv = (float)HostFollowLogic.ServoSpeed(el, Plugin.CarServoTau.Value, Plugin.CarServoMaxCorrection.Value, 0.5, vel.magnitude);
        var want = vel + (el > 1e-4f ? e * (sv / el) : Vector3.zero);
        var kv = (float)CarSyncLogic.BlendFactor(dt, Plugin.CarServoVelTau.Value);
        rb.linearVelocity = Vector3.Lerp(rb.linearVelocity, want, kv);
        var qe = trot * Quaternion.Inverse(rb.rotation); qe.ToAngleAxis(out var qang, out var axis);
        if (qang > 180) qang -= 360;
        var wc = Vector3.zero;
        if (!float.IsNaN(axis.x) && Mathf.Abs(qang) > 0.01f)
        {
            var rad = qang * Mathf.Deg2Rad;
            var ws = (float)HostFollowLogic.ServoAngSpeed(Math.Abs(rad), Plugin.CarServoTau.Value, Plugin.CarServoMaxTurn.Value);
            wc = axis * (Mathf.Sign(rad) * ws);
        }
        rb.angularVelocity = Vector3.Lerp(rb.angularVelocity, ang + wc, kv);
        var kr = (float)CarSyncLogic.BlendFactor(dt, Plugin.CarServoRotLerpTau.Value);
        rb.rotation = Quaternion.Slerp(rb.rotation, trot, kr);
        if (el < 0.5f) rb.position = Vector3.Lerp(rb.position, target, (float)CarSyncLogic.BlendFactor(dt, Plugin.CarServoPosLerpTau.Value));
        Blends++; Servos++;
    }

    static void HostOutage(Rigidbody rb, OutageLogic.Phase phase, double g, CarSyncLogic.Pose p, int hostTick, double tickDt, float dt, OutageLogic.Cfg c, FusionPlayer car)
    {
        if (phase == OutageLogic.Phase.Extrapolate)
        {
            var tau = OutageLogic.ExtrapolateTau(g, c);
            if (double.IsInfinity(tau)) return;
            var k = (float)CarSyncLogic.BlendFactor(dt, tau);
            var v = rb.linearVelocity;
            rb.linearVelocity = new Vector3(Mathf.Lerp(v.x, p.Vx, k), v.y, Mathf.Lerp(v.z, p.Vz, k));
            Extrapolations++;
            if (Airborne(rb, car)) return;
            var age = tickDt > 0 && p.Tick > 0 ? Math.Max(0, (hostTick - p.Tick) * tickDt) : g;
            var ang = new Vector3(p.Ax, p.Ay, p.Az);
            var trot = new Quaternion(p.Rx, p.Ry, p.Rz, p.Rw);
            var turn = ang.magnitude * (float)Math.Min(age, c.RotHorizonS);
            if (turn > 1e-5f) trot = Quaternion.AngleAxis(turn * Mathf.Rad2Deg, ang.normalized) * trot;
            var fh = Vector3.ProjectOnPlane(rb.rotation * Vector3.forward, Vector3.up); var ft = Vector3.ProjectOnPlane(trot * Vector3.forward, Vector3.up);
            if (fh.sqrMagnitude > 1e-4f && ft.sqrMagnitude > 1e-4f)
            {
                var yaw = Vector3.SignedAngle(fh, ft, Vector3.up);
                rb.rotation = Quaternion.AngleAxis(yaw * k, Vector3.up) * rb.rotation;
            }
            var av = rb.angularVelocity;
            rb.angularVelocity = new Vector3(av.x, Mathf.Lerp(av.y, p.Ay, k), av.z);
        }
        else if (phase == OutageLogic.Phase.Brake)
        {
            if (Airborne(rb, car)) return;
            var f = (float)OutageLogic.BrakeFactor(dt, c);
            var v = rb.linearVelocity;
            rb.linearVelocity = new Vector3(v.x * f, v.y, v.z * f);
            rb.angularVelocity *= f;
            Brakes++;
        }
    }

    static bool Airborne(Rigidbody rb, FusionPlayer car)
    {
        try
        {
            var hh = CarSync.BoxOf(car).y;
            var hits = Physics.RaycastAll(rb.worldCenterOfMass, Vector3.down, hh + 1.0f, ~0, QueryTriggerInteraction.Ignore);
            foreach (var h in hits) if (h.rigidbody != rb) return false;
            return true;
        }
        catch { return false; }
    }

    static bool TouchesOtherCar(FusionPlayer car, Rigidbody rb)
    {
        var ba = CarSync.BoxOf(car);
        var inv = Quaternion.Inverse(rb.rotation);
        foreach (var o in _players)
        {
            if (o == null || o == car || o.Object == null) continue;
            var orb = CarSync.Body(o); if (orb == null) continue;
            var bb = CarSync.BoxOf(o);
            var d = inv * (orb.position - rb.position);
            if (CarSyncLogic.InContact(d.x, d.y, d.z, (ba.x + bb.x) / 2, (ba.z + bb.z) / 2, (ba.y + bb.y) / 2, Plugin.CarContactMargin.Value)) return true;
        }
        return false;
    }

    static void DoSnap(int slot, FusionPlayer car, Rigidbody rb, Goal g, double nowS, string log)
    {
        _selfMoveUntil[slot] = nowS + SelfMoveHoldSeconds;
        rb.position = g.Target; rb.rotation = g.Rot; rb.linearVelocity = g.Vel; rb.angularVelocity = g.Ang;
        car.transform.SetPositionAndRotation(g.Target, g.Rot);
        _lastCheckpoint[slot] = nowS; _softUntil.Remove(slot);
        Snaps++;
        if (log != null) Plugin.L.LogInfo($"[CarSync] slot {slot}: {log} -> client pose");
    }

    static bool SnapLoop(int slot, double nowS)
    {
        var (at, n) = _snapStreak.TryGetValue(slot, out var v) ? v : (-1e9, 0);
        n = CarSyncLogic.SnapStreak(nowS - at, n);
        if (n >= 6) { _snapStreak[slot] = (nowS, 0); return true; }
        _snapStreak[slot] = (nowS, n); return false;
    }

    static void TrackConvergence(int slot, OutState st, double err, double nowS)
    {
        if (st.TrackSince < 0) return;
        if (err < 0.5) { var t = nowS - st.TrackSince; ConvergeTimes.Add(t); Plugin.L.LogInfo($"[CarSync] slot {slot}: converged (< 0.5 m) {t:0.00} s after the link came back (error then {st.TrackErr0:0.00} m)"); st.TrackSince = -1; }
        else if (nowS - st.TrackSince > 10) { Plugin.L.LogWarning($"[CarSync] slot {slot}: still {err:0.0} m apart 10 s after the link came back"); st.TrackSince = -1; }
    }

    static void SendAccept(int slot)
    {
        try
        {
            var c = (ushort)((_acceptCtr.TryGetValue(slot, out var v) ? v : 0) + 1); _acceptCtr[slot] = c;
            var b = OutageLogic.EncodeAccept((byte)slot, c);
            HostPings.Broadcast(b); HostPings.Broadcast(b);
            AcceptsSent++;
        }
        catch (Exception e) { Plugin.L.LogWarning($"[CarSync] accept packet: {e.Message}"); }
    }

    static OutState OutOf(int slot) { if (!_out.TryGetValue(slot, out var s)) _out[slot] = s = new OutState(); return s; }

    static CarSyncLogic.Cfg Cfg() => new()
    {
        CheckpointSeconds = Plugin.CarCheckpointSeconds.Value,
        DesyncSnapMeters = Plugin.CarDesyncMeters.Value,
        DesyncSnapDegrees = Plugin.CarDesyncDegrees.Value,
        HighEnterMs = double.MaxValue, HighExitMs = double.MaxValue,
        CheckpointMinMeters = CheckpointMinOverride >= 0 ? CheckpointMinOverride : Plugin.CarCheckpointMinMeters.Value,
        CheckpointMinDegrees = CheckpointMinOverride >= 0 ? CheckpointMinOverride : Plugin.CarCheckpointMinDegrees.Value,
        CheckpointSoftSeconds = Plugin.CarCheckpointSoftSeconds.Value,
    };

    static OutageLogic.Cfg OCfg() => new()
    {
        NormalS = Plugin.OutExtrapolateFrom.Value, ExtrapolateS = Plugin.OutExtrapolateSeconds.Value, PhysicsS = Plugin.OutBrakeFrom.Value,
        BrakeS = Plugin.OutBrakeSeconds.Value, SnapMeters = Plugin.OutSnapMeters.Value, ContactWaitS = Plugin.OutContactWait.Value,
        TeleportMeters = 50,
    };

    static string PhaseName(OutageLogic.Phase p) => p switch
    {
        OutageLogic.Phase.Extrapolate => "extrapolate", OutageLogic.Phase.PhysicsOnly => "physics", OutageLogic.Phase.Brake => "brake", _ => "normal",
    };
    static Msg PhaseMsg(OutageLogic.Phase p) => new(p switch
    {
        OutageLogic.Phase.Extrapolate => "phase.extrapolate", OutageLogic.Phase.PhysicsOnly => "phase.physics", OutageLogic.Phase.Brake => "phase.brake", _ => "phase.normal",
    });
}

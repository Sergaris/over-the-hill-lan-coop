using System;
using System.Collections.Generic;
using System.Net;
using Fusion;
using Fusion.Addons.Physics;
using UnityEngine;

namespace OthLan;

/// <summary>
/// Client-authoritative car sync over the side channel (UDP Port+1). ON by default since 0.1.5 final. The HOST's
/// setting decides for the whole session: the host attaches it as a flag to every ping reply / host ping (and to
/// the info query), clients follow it live and cannot be "half on" (their F7 toggle is greyed out: "host decides").
///
/// Vanilla (see docs/CAR-SYNC.md): the HOST has State Authority over every car and simulates it from the owning
/// client's NetworkInputData; the client predicts its own car and is reset to the host snapshot on every
/// received tick, with only the visual offset smoothed (NetworkRigidbodyBase.CorrectionSmoothingTime).
///
/// Side channel: the client sends its own car pose (pos/rot/vel/angVel + the Fusion tick it belongs to) at SendHz;
/// the host treats it as the truth for that car:
///   * echo RTT &lt; 150 ms (Low mode, hysteresis 135/165): every pose is applied with light interpolation (BlendTau).
///   * echo RTT &gt; 150 ms (High mode): the host's simulation runs freely between checkpoints; every CheckpointSeconds,
///     or immediately on desync (&gt; DesyncMeters / DesyncDegrees), the host snaps the car to the client's pose.
///   * gaps &gt; 50 m are game teleports (spawn, cabin travel) and are left to the game.
/// The pose is moved to the host's current tick by velocity x (hostTick - poseTick) x DeltaTime: no clock sync
/// needed, and it accounts for both the one-way delay and the client's prediction lead.
/// Whatever the host applies replicates to every other client through Fusion (their interpolation = the smoothing
/// "for others"); the owning client's prediction resets then land on (nearly) its own pose.
/// NOT implemented: suppressing Fusion's client-side reset itself (see docs).
/// </summary>
internal static class CarSync
{
    sealed class Latest { public CarSyncLogic.Pose Pose; public double ReceivedAt, AdvancedAt; public uint LastAppliedSeq; }
    public static int FrozenClientPoses;
    public static int PlaybackOk, PlaybackExtraps;   // 0.1.9 PosePlayback counters (host)

    static readonly object _lock = new();
    static readonly Dictionary<int, Latest> _poses = new();
    static readonly Dictionary<int, PosePlayback> _playback = new();   // 0.1.9 jitter buffer per slot
    static readonly Dictionary<int, CarSyncLogic.Mode> _mode = new();
    static readonly Dictionary<int, double> _lastCheckpoint = new();
    static readonly Dictionary<int, double> _softUntil = new();      // slot -> end of the current soft checkpoint blend
    static readonly Dictionary<int, double> _lastContact = new();
    // Our own snap/converge moves the car; the next frames must not read that as "the game moved the car" (which sends
    // OTAC and makes the client take the half-converged host pose: seen in o1m after the 3/4.9/6 s outages).
    static readonly Dictionary<int, double> _selfMoveUntil = new();
    const double SelfMoveHoldSeconds = 0.5;
    static readonly Dictionary<int, (Vector3 p, Quaternion q)> _lastHost = new();   // slot -> host pose after our step
    static readonly Dictionary<int, double> _jumpHoldUntil = new();
    public static int HostJumps, JumpsTowardClient, LowConverges, Servos, Teleports;
    static readonly Dictionary<int, ushort> _acceptCtr = new();
    public static int AcceptsSent;
    /// <summary>Host: tell that player's client to take the host pose (phase 2 would otherwise undo the game's move).</summary>
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

    /// <summary>Self-test (--othlan-hostjump): move this slot's car on the host once, like a game teleport would.</summary>
    public static int TestJumpSlot = -1;    // slot -> last time its car touched another car (host)
    /// <summary>Self-test only (--othlan-contactbox): replaces the car half-extents to force the contact pause.</summary>
    public static float ContactBoxOverride;
    /// <summary>Self-test only (--othlan-cpmin): replaces CheckpointMinMeters/Degrees when &gt;= 0 (0 = every checkpoint corrects).</summary>
    public static float CheckpointMinOverride = -1;
    public static readonly Dictionary<int, Msg> HostStats = new();
    static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    static FusionPlayer[] _players = Array.Empty<FusionPlayer>();
    public static FusionPlayer[] Players => _players;
    static float _nextScan, _nextSend;
    static uint _seq;
    static readonly List<(int slot, Latest l, PosePlayback.Target play)> _work = new();
    public static Msg ClientStat = Msg.Raw("");
    public static int Snaps, Blends, ModeSwitches, PosesSent, PosesReceived, PosesRejected, TeleportsIgnored,
        SoftSnaps, CheckpointsSkipped, ContactPauses;
    public static readonly Dictionary<int, string> LastAction = new();   // slot -> last decision (self-test log)
    /// <summary>Self-test override (command line --othlan-carsync): null = config, "off", "measure" (send poses + measure the
    /// host-vs-client gap, never touch the car), "on". The F7 toggle clears "on"/"off".</summary>
    public static string Override;
    /// <summary>This PC's own setting (used when it hosts).</summary>
    public static bool LocalSetting => Override switch { "off" => false, "measure" => true, "on" => true, _ => Plugin.CarSyncEnabled.Value };
    /// <summary>Host: session state = the host's own setting.</summary>
    public static bool HostEnabled => LocalSetting;
    public static int HostFlags => HostEnabled ? CarSyncLogic.FlagCarSync : 0;
    static readonly Dictionary<int, byte> _slotSuspend = new();
    /// <summary>0.1.9: per-slot host ping flags (CarSync bit + optional contact/winch suspend for that client).</summary>
    public static int HostFlagsFor(int slot)
    {
        var f = HostFlags;
        lock (_slotSuspend) if (_slotSuspend.TryGetValue(slot, out var s)) f |= s;
        return f;
    }

    // client: what the host said (-1 unknown) and when
    static volatile int _hostFlag = -1;
    static double _hostFlagAt = -1;
    public static int HostFlag => _hostFlag;
    /// <summary>0.1.9 R3: host says this client's car is in a contact/winch pause (authoritative backup for OwnCar.Suspend).</summary>
    public static bool HostSuspendContact, HostSuspendWinch;
    /// <summary>0.1.7: the last flag the host sent in this session (it only changes when the host toggles; a link outage
    /// must not switch the client to vanilla in the middle of a gap). Reset when the session ends.</summary>
    public static bool ClientEnabled => _hostFlag == 1;

    /// <summary>Effective state for this PC in the current LAN session.</summary>
    public static bool Enabled => LanState.Active switch
    {
        LanRole.Client => ClientEnabled,
        LanRole.Host => HostEnabled,
        _ => LocalSetting,
    };
    public static bool ApplyEnabled => LanState.Active == LanRole.Host && HostEnabled && Override != "measure";

    /// <summary>Network thread (ClientLink) or main thread (info query): the host's session flag.</summary>
    public static void OnHostFlags(int flags, double now)
    {
        var v = (flags & CarSyncLogic.FlagCarSync) != 0 ? 1 : 0;
        var old = _hostFlag;
        _hostFlag = v; _hostFlagAt = now;
        HostSuspendContact = (flags & CarSyncLogic.FlagSuspendContact) != 0;
        HostSuspendWinch = (flags & CarSyncLogic.FlagSuspendWinch) != 0;
        if (old != v) Plugin.L.LogInfo($"[CarSync] host says CarSync {(v == 1 ? "ON" : "OFF")} for this session");
    }

    /// <summary>The F7 button (and the self-test autopilot, through the same call). Only the host / an idle PC may
    /// change it; takes effect live and is saved to the cfg.</summary>
    public static bool UserToggle(string source)
    {
        if (LanState.Active == LanRole.Client) { Plugin.L.LogInfo($"[CarSync] toggle ignored ({source}): the host decides"); return false; }
        var v = !LocalSetting;
        if (Override == "on" || Override == "off") Override = null;
        Plugin.CarSyncEnabled.Value = v;   // BepInEx saves the cfg on set
        Plugin.L.LogWarning($"[CarSync] {(v ? "ENABLED" : "DISABLED")} via {source}{(LanState.Active == LanRole.Host ? " (clients follow within ~0.2 s)" : "")}");
        return true;
    }

    /// <summary>Host: latest metrics per slot (for the self-test log).</summary>
    public static readonly Dictionary<int, (string mode, double rtt, double err, double errDeg, double poseAge)> Metrics = new();

    static CarSyncLogic.Cfg Cfg() => new()
    {
        CheckpointSeconds = Plugin.CarCheckpointSeconds.Value,
        DesyncSnapMeters = Plugin.CarDesyncMeters.Value,
        DesyncSnapDegrees = Plugin.CarDesyncDegrees.Value,
        // 0.1.8 netcode v2: one continuous follower for every ping (the 0.1.7 "High" checkpoint mode let the host copy
        // drift and then snapped it; at Witness's 200 ms that meant frequent big corrections). HighPingMs is ignored.
        HighEnterMs = double.MaxValue, HighExitMs = double.MaxValue,
        CheckpointMinMeters = CheckpointMinOverride >= 0 ? CheckpointMinOverride : Plugin.CarCheckpointMinMeters.Value,
        CheckpointMinDegrees = CheckpointMinOverride >= 0 ? CheckpointMinOverride : Plugin.CarCheckpointMinDegrees.Value,
        CheckpointSoftSeconds = Plugin.CarCheckpointSoftSeconds.Value,
    };

    // ---- 0.1.7 outage handling (host) ----
    sealed class OutState
    {
        public bool InGap; public double GapMax; public OutageLogic.Phase Phase;
        public double ConvStart = -1, ConvT, WaitSince = -1, TrackSince = -1, TrackErr0;
    }
    static readonly Dictionary<int, OutState> _out = new();
    static OutState OutOf(int slot) { if (!_out.TryGetValue(slot, out var s)) _out[slot] = s = new OutState(); return s; }
    public static int Gaps, Recoveries, RecoverQuiet, RecoverConverge, RecoverSnap, RecoverWait, Converges, Extrapolations, Brakes;
    public static string LastRecovery = "";
    public static readonly List<double> ConvergeTimes = new();   // seconds from link back to error < 0.5 m

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

    static void TrackConvergence(int slot, OutState st, double err, double nowS)
    {
        if (st.TrackSince < 0) return;
        if (err < 0.5) { var t = nowS - st.TrackSince; ConvergeTimes.Add(t); Plugin.L.LogInfo($"[CarSync] slot {slot}: converged (< 0.5 m) {t:0.00} s after the link came back (error then {st.TrackErr0:0.00} m)"); st.TrackSince = -1; }
        else if (nowS - st.TrackSince > 10) { Plugin.L.LogWarning($"[CarSync] slot {slot}: still {err:0.0} m apart 10 s after the link came back"); st.TrackSince = -1; }
    }

    public static int SnapLoopsBroken;
    static readonly Dictionary<int, (double at, int n)> _snapStreak = new();
    /// <summary>True when this would be the 6th snap of the slot with &lt; 0.25 s between them (a snap that does not stick).</summary>
    static bool SnapLoop(int slot, double nowS, double err)
    {
        var (at, n) = _snapStreak.TryGetValue(slot, out var v) ? v : (-1e9, 0);
        n = CarSyncLogic.SnapStreak(nowS - at, n);
        if (n >= 6) { _snapStreak[slot] = (nowS, 0); return true; }
        _snapStreak[slot] = (nowS, n); return false;
    }

    static void DoSnap(int slot, FusionPlayer car, Rigidbody rb, Vector3 target, Quaternion trot, Vector3 vel, Vector3 ang, double nowS, string log)
    {
        _selfMoveUntil[slot] = nowS + SelfMoveHoldSeconds;
        rb.position = target; rb.rotation = trot; rb.linearVelocity = vel; rb.angularVelocity = ang;
        car.transform.SetPositionAndRotation(target, trot);
        _lastCheckpoint[slot] = nowS; _softUntil.Remove(slot);
        Snaps++;
        if (log != null) Plugin.L.LogInfo($"[CarSync] slot {slot}: {log} -> client pose");
    }

    /// <summary>Nothing under the car within ~1 m of its bottom (jump, fall): no rotation extrapolation, no braking.</summary>
    static bool Airborne(Rigidbody rb, FusionPlayer car)
    {
        try
        {
            var hh = BoxOf(car).y;
            var hits = Physics.RaycastAll(rb.worldCenterOfMass, Vector3.down, hh + 1.0f, ~0, QueryTriggerInteraction.Ignore);
            foreach (var h in hits) if (h.rigidbody != rb) return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>v3 velocity servo (also used in outage Extrapolate so the host proxy keeps chasing the client's
    /// last extrapolated pose instead of only blending linear velocity — NETCODE-v4 §2.3 / 0.1.9 U5 follow-up).</summary>
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
            var rad = qang * Mathf.Deg2Rad; var ws = Mathf.Min(Mathf.Abs(rad) / Plugin.CarServoTau.Value, Plugin.CarServoMaxTurn.Value);
            wc = axis * (Mathf.Sign(rad) * ws);
        }
        rb.angularVelocity = Vector3.Lerp(rb.angularVelocity, ang + wc, kv);
        var kr = (float)CarSyncLogic.BlendFactor(dt, Plugin.CarServoRotLerpTau.Value);
        rb.rotation = Quaternion.Slerp(rb.rotation, trot, kr);
        if (el < 0.5f) rb.position = Vector3.Lerp(rb.position, target, (float)CarSyncLogic.BlendFactor(dt, Plugin.CarServoPosLerpTau.Value));
        Blends++; Servos++;
    }

    /// <summary>Host, no fresh poses: extrapolation (pull velocity and heading toward the last pose's, never the position
    /// toward a frozen point), physics only, or gentle braking. Extrapolate with FollowByVelocity uses
    /// <see cref="ApplyVelocityFollow"/> instead (caller).</summary>
    static void HostOutage(Rigidbody rb, OutageLogic.Phase phase, double g, CarSyncLogic.Pose p, int hostTick, double tickDt, float dt, OutageLogic.Cfg c, FusionPlayer car)
    {
        if (phase == OutageLogic.Phase.Extrapolate)
        {
            var tau = OutageLogic.ExtrapolateTau(g, c);
            if (double.IsInfinity(tau)) return;
            var k = (float)CarSyncLogic.BlendFactor(dt, tau);
            var v = rb.linearVelocity;
            rb.linearVelocity = new Vector3(Mathf.Lerp(v.x, p.Vx, k), v.y, Mathf.Lerp(v.z, p.Vz, k));   // vertical: host physics
            Extrapolations++;
            if (Airborne(rb, car)) return;                       // in the air: rotation is the host physics' business
            var age = tickDt > 0 && p.Tick > 0 ? Math.Max(0, (hostTick - p.Tick) * tickDt) : g;
            var ang = new Vector3(p.Ax, p.Ay, p.Az);
            var trot = new Quaternion(p.Rx, p.Ry, p.Rz, p.Rw);
            var turn = ang.magnitude * (float)Math.Min(age, c.RotHorizonS);
            if (turn > 1e-5f) trot = Quaternion.AngleAxis(turn * Mathf.Rad2Deg, ang.normalized) * trot;
            // heading only (pitch/roll follow the terrain on the host)
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

    // ---- contact box: measured from the car's own colliders (0.1.7, ROADMAP open item), cfg values as the fallback ----
    static readonly Dictionary<IntPtr, Vector3> _boxes = new();
    public static readonly Dictionary<string, string> MeasuredBoxes = new();   // car name -> "w x l x h (n colliders)" (log/F7)

    /// <summary>Half extents (x right, y up, z forward) around the rigidbody position.</summary>
    static Vector3 BoxOf(FusionPlayer car)
    {
        if (ContactBoxOverride > 0) return Vector3.one * ContactBoxOverride;
        var fallback = new Vector3(Plugin.CarHalfWidth.Value, Plugin.CarHalfHeight.Value, Plugin.CarHalfLength.Value);
        if (!Plugin.ContactBoxFromColliders.Value || car == null) return fallback;
        if (_boxes.TryGetValue(car.Pointer, out var b)) return b;
        b = fallback;
        try
        {
            var rb = Body(car);
            if (rb != null)
            {
                var inv = Quaternion.Inverse(rb.rotation); var o = rb.position;
                float mx = 0, my = 0, mz = 0; int n = 0;
                void Corner(Transform t, Vector3 lp) { var d = inv * (t.TransformPoint(lp) - o); mx = Mathf.Max(mx, Mathf.Abs(d.x)); my = Mathf.Max(my, Mathf.Abs(d.y)); mz = Mathf.Max(mz, Mathf.Abs(d.z)); }
                void Box(Transform t, Vector3 c, Vector3 size)
                {
                    var h = size * 0.5f;
                    for (int i = 0; i < 8; i++) Corner(t, c + new Vector3((i & 1) != 0 ? h.x : -h.x, (i & 2) != 0 ? h.y : -h.y, (i & 4) != 0 ? h.z : -h.z));
                }
                foreach (var col in car.GetComponentsInChildren<Collider>())
                {
                    if (col == null || col.isTrigger || !col.enabled || col.attachedRigidbody != rb) continue;
                    var bc = col.TryCast<BoxCollider>(); if (bc != null) { Box(col.transform, bc.center, bc.size); n++; continue; }
                    var mc = col.TryCast<MeshCollider>(); if (mc != null && mc.sharedMesh != null) { var mb = mc.sharedMesh.bounds; Box(col.transform, mb.center, mb.size); n++; continue; }
                    var sc = col.TryCast<SphereCollider>(); if (sc != null) { Box(col.transform, sc.center, Vector3.one * sc.radius * 2); n++; continue; }
                    var cc = col.TryCast<CapsuleCollider>();
                    if (cc != null)
                    {
                        var size = Vector3.one * cc.radius * 2; size[Mathf.Clamp(cc.direction, 0, 2)] = Mathf.Max(cc.height, cc.radius * 2);
                        Box(col.transform, cc.center, size); n++;
                    }
                }
                if (n > 0 && mx is > 0.3f and < 5f && my is > 0.2f and < 5f && mz is > 0.5f and < 8f)
                {
                    b = new Vector3(mx, my, mz);
                    MeasuredBoxes[car.name] = $"{mx:0.00} x {mz:0.00} x {my:0.00} ({n})";
                    Plugin.L.LogInfo($"[CarSync] contact box of {car.name}: half width {mx:0.00} m, half length {mz:0.00} m, half height {my:0.00} m from {n} colliders (cfg fallback {fallback.x:0.0}/{fallback.z:0.0}/{fallback.y:0.0})");
                }
                else Plugin.L.LogInfo($"[CarSync] contact box of {car.name}: colliders unusable ({n}, {mx:0.00}/{mz:0.00}/{my:0.00}); cfg values used");
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[CarSync] contact box measurement failed: {e.Message}"); }
        _boxes[car.Pointer] = b;
        return b;
    }

    /// <summary>Host: does <paramref name="car"/> touch any other car right now? (box test in its local frame; the box is
    /// the sum of both cars' half extents, measured from their colliders)</summary>
    static bool TouchesOtherCar(FusionPlayer car, Rigidbody rb)
    {
        var ba = BoxOf(car);
        var inv = Quaternion.Inverse(rb.rotation);
        foreach (var o in _players)
        {
            if (o == null || o == car || o.Object == null) continue;
            var orb = Body(o); if (orb == null) continue;
            var bb = BoxOf(o);
            var d = inv * (orb.position - rb.position);
            if (CarSyncLogic.InContact(d.x, d.y, d.z, (ba.x + bb.x) / 2, (ba.z + bb.z) / 2, (ba.y + bb.y) / 2, Plugin.CarContactMargin.Value)) return true;
        }
        return false;
    }

    /// <summary>Network thread (HostInfo listener).</summary>
    public static void OnPosePacket(byte[] data, IPEndPoint from)
    {
        if (LanState.LanRunnerRole != LanRole.Host || !HostEnabled) return;
        if (!CarSyncLogic.TryDecode(data, out var p)) return;
        // Only the endpoint that pings as this slot may move this slot's car (no spoofing, no stale process).
        var ep = HostPings.EndpointOf(p.Slot);
        if (ep == null || !ep.Equals(from)) { PosesRejected++; return; }
        var now = Clock.Elapsed.TotalSeconds;
        lock (_lock)
        {
            if (_poses.TryGetValue(p.Slot, out var l) && !CarSyncLogic.AcceptSeq(p.Seq, l.Pose.Seq, now - l.ReceivedAt)) return; // reordered/old
            // 0.1.8: a client whose Fusion tick stopped (freeze after heavy loss) keeps sending the same frozen pose over
            // the side channel. That is not a live pose: treat it like no poses (extrapolate, then brake) instead of
            // snapping to it every frame and asking the client to take our pose (an1: 80 snap loops, 84 OTAC).
            var adv = l == null || p.Tick == 0 || p.Tick != l.Pose.Tick ? now : l.AdvancedAt;
            if (adv != now) FrozenClientPoses++;
            _poses[p.Slot] = new Latest { Pose = p, ReceivedAt = now, AdvancedAt = adv, LastAppliedSeq = l?.LastAppliedSeq ?? 0 };
            if (Plugin.CarPosePlayback.Value)
            {
                if (!_playback.TryGetValue(p.Slot, out var pb)) _playback[p.Slot] = pb = new PosePlayback();
                pb.Push(new PosePlayback.Sample
                {
                    T = now, Tick = p.Tick, Seq = p.Seq,
                    Px = p.Px, Py = p.Py, Pz = p.Pz, Vx = p.Vx, Vy = p.Vy, Vz = p.Vz,
                    Rx = p.Rx, Ry = p.Ry, Rz = p.Rz, Rw = p.Rw, Ax = p.Ax, Ay = p.Ay, Az = p.Az,
                });
            }
            PosesReceived++;
        }
    }

    static NetworkRunner LanRunner()
    {
        if (LanState.Active == LanRole.Off || LanState.LanRunner == IntPtr.Zero) return null;
        var r = OverlandNetworkRunner.Instance?.NetworkRunner;
        return r != null && r.Pointer == LanState.LanRunner && r.IsRunning ? r : null;
    }

    static void Scan(float now)
    {
        if (now < _nextScan) return;
        _nextScan = now + 1f;
        try { _players = UnityEngine.Object.FindObjectsOfType<FusionPlayer>(); } catch { _players = Array.Empty<FusionPlayer>(); }
        var w = new VehicleWinchSystem[_players.Length];
        for (int i = 0; i < _players.Length; i++)
        {
            try { var fp = _players[i]; if (fp != null) w[i] = fp.GetComponentInChildren<VehicleWinchSystem>() ?? fp.GetComponentInParent<VehicleWinchSystem>(); }
            catch { w[i] = null; }
        }
        _winches = w;
    }

    static VehicleWinchSystem[] _winches = Array.Empty<VehicleWinchSystem>();
    public static int WinchPauses;

    /// <summary>The car's own winch is attached (to a tree, an object or another car), or another car's winch pulls this
    /// car. The rope force is applied by the host's simulation; overriding the pose would cancel it, so CarSync leaves
    /// such a car to vanilla Fusion (as without the mod) while the rope is attached.</summary>
    public static bool WinchInvolved(FusionPlayer car, PlayerRef pref)
    {
        try
        {
            var ws = _winches; var ps = _players;
            for (int i = 0; i < ps.Length && i < ws.Length; i++)
            {
                var w = ws[i]; if (w == null || ps[i] == null) continue;
                if (!w.WinchIsAttached) continue;
                if (ps[i] == car) return true;                     // own rope
                if (w.WinchedCarPlayerRef == pref) return true;    // someone tows this car
            }
        }
        catch { }
        return false;
    }

    /// <summary>Own car's winch (self-test).</summary>
    public static VehicleWinchSystem OwnWinch(out FusionPlayer car)
    {
        car = null;
        var ws = _winches; var ps = _players;
        for (int i = 0; i < ps.Length && i < ws.Length; i++)
            if (ps[i] != null && ps[i].Object != null && ps[i].Object.HasInputAuthority) { car = ps[i]; return ws[i]; }
        return null;
    }

    static Rigidbody Body(FusionPlayer fp)
    {
        var rb = fp.rigidbody;
        if (rb == null && fp.vehicleBase != null) rb = fp.vehicleBase.cachedRigidbody;
        return rb;
    }

    public static void Tick()
    {
        var r = LanRunner();
        if (r == null) { _hostFlag = -1; Reset(); return; }
        if (!Enabled) { Reset(); ClientStat = LanState.Active == LanRole.Client ? new Msg(_hostFlag < 0 ? "cs.waitHost" : "cs.offByHost") : Msg.Raw(""); return; }
        var now = Time.realtimeSinceStartup;
        Scan(now);
        try
        {
            if (LanState.Active == LanRole.Client) ClientSend(r, now);
            else HostApply(r);
        }
        catch (Exception e) { Plugin.L.LogWarning($"[CarSync] {e.Message}"); _nextScan = 0; }
    }

    static void Reset()
    {
        lock (_lock) { _poses.Clear(); _playback.Clear(); }
        PlaybackOk = PlaybackExtraps = 0;
        _out.Clear();
        _mode.Clear(); _lastCheckpoint.Clear(); _softUntil.Clear(); _lastContact.Clear(); _lastHost.Clear(); _selfMoveUntil.Clear(); _jumpHoldUntil.Clear(); HostStats.Clear(); Metrics.Clear(); LastAction.Clear();
    }

    static void ClientSend(NetworkRunner r, float now)
    {
        if (now < _nextSend) return;
        _nextSend = now + 1f / Mathf.Clamp(Plugin.CarSendHz.Value, 2f, 60f);
        foreach (var fp in _players)
        {
            if (fp == null || fp.Object == null || !fp.Object.HasInputAuthority) continue;
            var rb = Body(fp); if (rb == null) return;
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
        var nowS = Clock.Elapsed.TotalSeconds;
        var dt = Time.unscaledDeltaTime;
        _work.Clear();
        lock (_lock)
        {
            foreach (var kv in _poses)
            {
                PosePlayback.Target play = default;
                if (_playback.TryGetValue(kv.Key, out var pb))
                {
                    play = pb.SampleAt(nowS);
                    if (play.Ok) PlaybackOk++;
                    if (play.Extrapolated) PlaybackExtraps++;
                }
                _work.Add((kv.Key, kv.Value, play));
            }
        }
        if (_work.Count == 0) return;
        var pings = HostPings.Snapshot();   // once per frame, not per car
        var hostTick = r.Tick.Raw; var tickDt = (double)r.DeltaTime;
        var work = _work;
        try { HostApplyCars(r, work, cfg, ocfg, nowS, dt, pings, hostTick, tickDt); }
        finally { NetTrace.FollowFlush(LastAction, dt); }
    }

    static void HostApplyCars(NetworkRunner r, List<(int slot, Latest l, PosePlayback.Target play)> work, CarSyncLogic.Cfg cfg, OutageLogic.Cfg ocfg, double nowS, float dt,
                              Dictionary<int, (double ms, bool timedOut, double loss, double noLinkS)> pings, int hostTick, double tickDt)
    {
        foreach (var (slot, l, play) in work)
        {
            var pref = PlayerRef.FromIndex(slot);
            if (!r.IsPlayerValid(pref)) continue;
            FusionPlayer car = null;
            foreach (var fp in _players)
                if (fp != null && fp.Object != null && fp.Object.InputAuthority == pref) { car = fp; break; }
            if (car == null) { HostStats[slot] = new Msg("cs.slotNoCar", slot); continue; }
            var rb = Body(car); if (rb == null) continue;
            if (TestJumpSlot == slot) { TestJumpSlot = -1; rb.position += rb.rotation * new Vector3(0, 1.5f, 6f); Plugin.L.LogWarning($"[CarSync] self-test: host moved slot {slot} by 6 m"); }

            // RTT = our UDP echo over the real link (same source as the ping label); Fusion RTT only if no echo yet.
            // Echo timed out => NaN => NextMode holds the current mode (no flapping to Low on a Fusion figure).
            double rtt;
            if (pings.TryGetValue(slot, out var hp)) rtt = hp.timedOut ? double.NaN : hp.ms;
            else rtt = r.GetPlayerRtt(pref) * 1000.0;
            var prevMode = _mode.TryGetValue(slot, out var m) ? m : CarSyncLogic.Mode.Low;
            var mode = CarSyncLogic.NextMode(prevMode, rtt, cfg);
            if (mode != prevMode) { ModeSwitches++; Plugin.L.LogInfo($"[CarSync] slot {slot}: mode {prevMode} -> {mode} at rtt {rtt:0} ms"); }
            _mode[slot] = mode;
            var p = l.Pose;
            // 0.1.9: prefer jitter-buffered Hermite playback; fall back to tick-age extrapolate from the latest pose.
            double age;
            Vector3 target, vel, ang;
            Quaternion trot;
            if (play.Ok)
            {
                // Hermite-smoothed pose from the jitter buffer, then the same tick-age lead as before (one-way /
                // prediction). Delay inside PosePlayback is jitter-only (≤ ~80 ms), not a second latency.
                age = CarSyncLogic.PoseAgeFromTicks(hostTick, play.Tick > 0 ? play.Tick : p.Tick, tickDt);
                target = new Vector3((float)CarSyncLogic.Extrapolate(play.Px, play.Vx, age),
                                     (float)CarSyncLogic.Extrapolate(play.Py, play.Vy, age),
                                     (float)CarSyncLogic.Extrapolate(play.Pz, play.Vz, age));
                vel = new Vector3((float)play.Vx, (float)play.Vy, (float)play.Vz);
                ang = new Vector3((float)play.Ax, (float)play.Ay, (float)play.Az);
                trot = new Quaternion((float)play.Rx, (float)play.Ry, (float)play.Rz, (float)play.Rw);
                var turn = ang.magnitude * (float)age;
                if (turn > 1e-5f) trot = Quaternion.AngleAxis(turn * Mathf.Rad2Deg, ang.normalized) * trot;
            }
            else
            {
                age = CarSyncLogic.PoseAgeFromTicks(hostTick, p.Tick, tickDt);
                target = new Vector3((float)CarSyncLogic.Extrapolate(p.Px, p.Vx, age), (float)CarSyncLogic.Extrapolate(p.Py, p.Vy, age),
                                     (float)CarSyncLogic.Extrapolate(p.Pz, p.Vz, age));
                vel = new Vector3(p.Vx, p.Vy, p.Vz); ang = new Vector3(p.Ax, p.Ay, p.Az);
                trot = new Quaternion(p.Rx, p.Ry, p.Rz, p.Rw);
                var turn = ang.magnitude * (float)age;
                if (turn > 1e-5f) trot = Quaternion.AngleAxis(turn * Mathf.Rad2Deg, ang.normalized) * trot;
            }
            var err = Vector3.Distance(rb.position, target);
            var errDeg = Quaternion.Angle(rb.rotation, trot);
            if (_lastHost.TryGetValue(slot, out var lh)
                && !(_selfMoveUntil.TryGetValue(slot, out var smu) && nowS < smu))
            {
                var moved = Vector3.Distance(rb.position, lh.p); var turned = Quaternion.Angle(rb.rotation, lh.q);
                if (CarSyncLogic.HostJumped(moved, turned, rb.linearVelocity.magnitude, dt))
                {
                    if (HostFollowLogic.GameMovedCar(moved, turned, rb.linearVelocity.magnitude, dt,
                            Vector3.Distance(lh.p, target), err, Quaternion.Angle(lh.q, trot), errDeg))
                    {
                        HostJumps++;
                        if (Plugin.CarTeleportOnGameMove.Value)
                        {   // v3e: Fusion's teleport flag, so observers do not smooth the jump (RemoteProxySnapMeters is 10 m)
                            try { var nrb = car.GetComponent<NetworkRigidbody3D>(); if (nrb != null) { nrb.Teleport(new Il2CppSystem.Nullable<Vector3>(rb.position), new Il2CppSystem.Nullable<Quaternion>(rb.rotation)); Teleports++; } }
                            catch (Exception e) { Plugin.L.LogWarning($"[CarSync] teleport flag: {e.Message}"); }
                        }
                        var hold = CarSyncLogic.JumpHoldSeconds(pings.TryGetValue(slot, out var hj) && !hj.timedOut ? hj.ms : double.NaN);
                        _jumpHoldUntil[slot] = nowS + hold;
                        Plugin.L.LogInfo($"[CarSync] slot {slot}: moved by the game on the host ({moved:0.0} m, {turned:0} deg, now {err:0.0} m from the client's pose); hands off for {hold:0.00} s");
                        SendAccept(slot);
                    }
                    else { JumpsTowardClient++; if (JumpsTowardClient <= 5) Plugin.L.LogInfo($"[CarSync] slot {slot}: host copy jumped {moved:0.0} m / {turned:0} deg toward the client's pose (physics, not the game): no OTAC"); }
                }
            }
            if (NetTrace.On) NetTrace.Pending[slot] = new NetTrace.FollowIn { Rb = rb, Pre = rb.position, Target = target, ClientVel = vel, Err = err, ErrDeg = errDeg, Age = age, Gap = nowS - l.AdvancedAt };
            var jumpHold = _jumpHoldUntil.TryGetValue(slot, out var jh) && nowS < jh;
            Metrics[slot] = (mode.ToString(), rtt, err, errDeg, age);
            if (err > cfg.TeleportMeters && nowS - l.ReceivedAt <= cfg.MaxPoseAgeSeconds && l.LastAppliedSeq != p.Seq) TeleportsIgnored++;
            var winch = WinchInvolved(car, pref);
            var g = nowS - l.AdvancedAt;                         // seconds without a fresh (tick advancing) pose from this client
            var st = OutOf(slot);
            var phase = OutageLogic.PhaseOf(g, ocfg);
            if (jumpHold) { _lastHost[slot] = (rb.position, rb.rotation); _lastCheckpoint[slot] = nowS; LastAction[slot] = "jump-hold";
                            st.ConvStart = -1; st.WaitSince = -1;
                            HostStats[slot] = new Msg("cs.slotGameMoved", slot); continue; }
            var touching = Plugin.CarContactPause.Value && TouchesOtherCar(car, rb);
            var contactNow = winch || touching;
            // 0.1.9 R3: tell that client to suspend OwnLocal/phase2 (per-slot ping flags).
            byte sus = 0;
            if (winch) sus |= CarSyncLogic.FlagSuspendWinch;
            else if (touching) sus |= CarSyncLogic.FlagSuspendContact;
            lock (_slotSuspend) { if (sus != 0) _slotSuspend[slot] = sus; else _slotSuspend.Remove(slot); }
            if (winch && !(LastAction.TryGetValue(slot, out var la) && la == "winch")) WinchPauses++;
            if (contactNow) { if (!CarSyncLogic.ContactPaused(nowS, _lastContact.TryGetValue(slot, out var lc0) ? lc0 : 0, Plugin.CarContactHoldSeconds.Value)) ContactPauses++; _lastContact[slot] = nowS; }
            var paused = CarSyncLogic.ContactPaused(nowS, _lastContact.TryGetValue(slot, out var lc) ? lc : 0, Plugin.CarContactHoldSeconds.Value);
            if (contactNow && NetTrace.Pending.TryGetValue(slot, out var tp)) { tp.Contact = true; NetTrace.Pending[slot] = tp; }

            // ---- 0.1.7: link outage (no fresh poses) -> extrapolate / physics only / brake ----
            if (phase != OutageLogic.Phase.Normal)
            {
                if (!st.InGap) { st.InGap = true; Gaps++; Plugin.L.LogInfo($"[CarSync] slot {slot}: no poses for {g:0.00} s -> {phase}"); }
                if (st.Phase != phase) { if (st.Phase != OutageLogic.Phase.Normal) Plugin.L.LogInfo($"[CarSync] slot {slot}: gap {g:0.0} s -> {phase}"); st.Phase = phase; }
                st.GapMax = g; st.ConvStart = -1; st.WaitSince = -1; st.TrackSince = -1;
                if (ApplyEnabled && !winch && !paused)
                {
                    // 0.1.9: during Extrapolate chase the already age-extrapolated client target with the same servo as
                    // Normal/Blend (keeps host proxy closer → fewer recovery snaps). PhysicsOnly/Brake unchanged.
                    if (phase == OutageLogic.Phase.Extrapolate && Plugin.CarFollowVelocity.Value)
                    { ApplyVelocityFollow(rb, target, trot, vel, ang, dt); Extrapolations++; }
                    else HostOutage(rb, phase, g, p, hostTick, tickDt, dt, ocfg, car);
                }
                LastAction[slot] = winch ? "winch" : paused ? "contact" : PhaseName(phase);
                _lastHost[slot] = (rb.position, rb.rotation);
                HostStats[slot] = new Msg("cs.slotNoData", slot, g, PhaseMsg(phase));
                continue;
            }
            if (st.InGap)
            {   // first fresh pose after a gap
                st.InGap = false; st.Phase = OutageLogic.Phase.Normal;
                var hostUp = (rb.rotation * Vector3.up).y; var cliUp = (trot * Vector3.up).y;
                var rec = HostFollowLogic.Recover(err, errDeg, paused, rb.linearVelocity.magnitude, ocfg, out var T);
                if (rec != OutageLogic.Recovery.Teleport && OutageLogic.KeepUpright(err, hostUp, cliUp)) rec = OutageLogic.Recovery.None;
                Recoveries++; LastRecovery = $"{rec} gap {st.GapMax:0.00}s err {err:0.00}m {errDeg:0}°";
                Plugin.L.LogInfo($"[CarSync] slot {slot}: link back after {st.GapMax:0.00} s, error {err:0.00} m / {errDeg:0} deg -> {rec}{(T > 0 ? $" over {T:0.00} s" : "")}");
                st.TrackSince = rec == OutageLogic.Recovery.Teleport ? -1 : nowS; st.TrackErr0 = err;
                switch (rec)
                {
                    case OutageLogic.Recovery.Converge: st.ConvStart = nowS; st.ConvT = T; RecoverConverge++; break;
                    case OutageLogic.Recovery.WaitContact: st.WaitSince = nowS; RecoverWait++; break;
                    case OutageLogic.Recovery.Snap: RecoverSnap++; if (ApplyEnabled) DoSnap(slot, car, rb, target, trot, vel, ang, nowS, $"recovery snap {err:0.0} m / {errDeg:0} deg"); break;
                    case OutageLogic.Recovery.None: RecoverQuiet++; break;
                }
            }
            if (st.WaitSince >= 0)
            {
                var rec = OutageLogic.Decide(err, errDeg, paused, nowS - st.WaitSince, rb.linearVelocity.magnitude, ocfg, out var T);
                if (rec == OutageLogic.Recovery.WaitContact) { LastAction[slot] = "wait-contact"; _lastHost[slot] = (rb.position, rb.rotation); HostStats[slot] = new Msg("cs.slotWaitContact", slot); continue; }
                st.WaitSince = -1;
                if (rec == OutageLogic.Recovery.Snap && ApplyEnabled) DoSnap(slot, car, rb, target, trot, vel, ang, nowS, $"recovery snap after contact {err:0.0} m");
                else if (rec == OutageLogic.Recovery.Converge) { st.ConvStart = nowS; st.ConvT = T; }
            }
            if (st.ConvStart >= 0)
            {
                var e = nowS - st.ConvStart;
                if (e >= st.ConvT) { st.ConvStart = -1; _lastCheckpoint[slot] = nowS; _softUntil.Remove(slot); }
                else
                {
                    if (ApplyEnabled && !winch && !paused)
                    {
                        var (kvr, kp) = OutageLogic.ConvergeFactors(e, st.ConvT, dt);
                        rb.linearVelocity = Vector3.Lerp(rb.linearVelocity, vel, (float)kvr);
                        rb.angularVelocity = Vector3.Lerp(rb.angularVelocity, ang, (float)kvr);
                        rb.rotation = Quaternion.Slerp(rb.rotation, trot, (float)kvr);
                        rb.position = Vector3.Lerp(rb.position, target, (float)kp);
                        Converges++;
                        _selfMoveUntil[slot] = nowS + SelfMoveHoldSeconds;
                    }
                    LastAction[slot] = winch ? "winch" : paused ? "contact" : "converge";
                    TrackConvergence(slot, st, err, nowS);
                    _lastHost[slot] = (rb.position, rb.rotation);
                    HostStats[slot] = new Msg("cs.slotLinkBack", slot, err);
                    continue;
                }
            }
            TrackConvergence(slot, st, err, nowS);

            var since = nowS - (_lastCheckpoint.TryGetValue(slot, out var c) ? c : 0);
            var soft = _softUntil.TryGetValue(slot, out var su) && nowS < su;
            var act = ApplyEnabled ? CarSyncLogic.Decide(mode, err, errDeg, since, g, cfg, paused, soft) : CarSyncLogic.Action.None;
            if (act == CarSyncLogic.Action.Snap && errDeg > ocfg.FlippedDegrees && rb.linearVelocity.magnitude >= 2 && err <= ocfg.SnapMeters)
            {   // flipped / rolled: no hard flip while moving, converge over 0.5 s instead
                st.ConvStart = nowS; st.ConvT = 0.5; act = CarSyncLogic.Action.None;
            }
            if (act == CarSyncLogic.Action.Snap && OutageLogic.KeepUpright(err, (rb.rotation * Vector3.up).y, (trot * Vector3.up).y)) act = CarSyncLogic.Action.None;
            if (act == CarSyncLogic.Action.Snap && err <= ocfg.SnapMeters)
            {   // v2: a big but plausible error (after a stall) glides over 0.3..1.5 s instead of a jump others would see
                st.ConvStart = nowS; st.ConvT = OutageLogic.ConvergeSeconds(err); st.TrackSince = nowS; st.TrackErr0 = err; act = CarSyncLogic.Action.None; LowConverges++;
            }
            if (paused && ApplyEnabled) { _lastCheckpoint[slot] = nowS; _softUntil.Remove(slot); }   // no checkpoint burst right after
            LastAction[slot] = winch ? "winch" : paused ? "contact" : act.ToString();
            if (act == CarSyncLogic.Action.CheckpointOk) { _lastCheckpoint[slot] = nowS; CheckpointsSkipped++; }
            if (act == CarSyncLogic.Action.SoftSnap) { _softUntil[slot] = nowS + cfg.CheckpointSoftSeconds; _lastCheckpoint[slot] = nowS; SoftSnaps++; }
            if (act == CarSyncLogic.Action.SoftSnap || act == CarSyncLogic.Action.SoftBlend)
            {
                // Fast blend (tau = window/5: ~99% aligned at the end of the window) instead of a one-frame jump.
                var k = (float)CarSyncLogic.BlendFactor(dt, cfg.CheckpointSoftSeconds / 5.0);
                rb.position = Vector3.Lerp(rb.position, target, k);
                rb.rotation = Quaternion.Slerp(rb.rotation, trot, k);
                rb.linearVelocity = Vector3.Lerp(rb.linearVelocity, vel, k);
                rb.angularVelocity = Vector3.Lerp(rb.angularVelocity, ang, k);
            }
            else if (act == CarSyncLogic.Action.Blend && Plugin.CarFollowVelocity.Value)
            {
                // v3: drive the host copy through velocity (never a hard body write into collisions). See ApplyVelocityFollow.
                ApplyVelocityFollow(rb, target, trot, vel, ang, dt);
            }
            else if (act == CarSyncLogic.Action.Blend)
            {
                var k = (float)CarSyncLogic.BlendFactor(dt, Plugin.CarBlendTau.Value);
                rb.position = Vector3.Lerp(rb.position, target, k);
                rb.rotation = Quaternion.Slerp(rb.rotation, trot, k);
                rb.linearVelocity = Vector3.Lerp(rb.linearVelocity, vel, k);
                rb.angularVelocity = Vector3.Lerp(rb.angularVelocity, ang, k);
                Blends++;
            }
            else if (act == CarSyncLogic.Action.Snap && SnapLoop(slot, nowS, err))
            {   // the snap does not stick (seen in fo1 after a 6 s outage: 128 snaps at 8.5 m in 2.6 s, every frame):
                // ask the client to take OUR pose instead, hands off meanwhile
                SendAccept(slot); SnapLoopsBroken++;
                _jumpHoldUntil[slot] = nowS + 1.0;
                Plugin.L.LogWarning($"[CarSync] slot {slot}: snaps do not stick ({err:0.0} m); client takes the host pose");
            }
            else if (act == CarSyncLogic.Action.Snap)
                DoSnap(slot, car, rb, target, trot, vel, ang, nowS, (err > cfg.DesyncSnapMeters || errDeg > cfg.DesyncSnapDegrees || mode == CarSyncLogic.Mode.Low) ? $"{mode} snap {err:0.0} m / {errDeg:0} deg" : null);
            lock (_lock) if (_poses.TryGetValue(slot, out var cur) && cur.Pose.Seq == p.Seq) cur.LastAppliedSeq = p.Seq;
            var how = new Msg(!ApplyEnabled ? "cs.howMeasure" : winch ? "cs.howWinch" : paused ? "cs.howContact"
                    : mode == CarSyncLogic.Mode.Low ? "cs.howSmooth" : "cs.howCheckpoints");
            _lastHost[slot] = (rb.position, rb.rotation);
            HostStats[slot] = new Msg("cs.slotLive", slot, how, double.IsNaN(rtt) ? "--" : rtt.ToString("0"), err);
        }
    }
}

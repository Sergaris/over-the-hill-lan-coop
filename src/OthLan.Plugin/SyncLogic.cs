using System;

namespace OthLan;

/// <summary>Pure (Unity-free) decision logic for the LAN ping indicator and car sync. Unit-tested on the box
/// (tests/LogicTests); the Unity side lives in PingHud.cs / CarSync.cs.</summary>
public static class PingLogic
{
    public enum Level { Green, Yellow, Red, Waiting }

    /// <summary>0.1.7 (user spec 2026-10-21): green &lt;= 200 ms, yellow 200..300 (strictly above 200, up to 300 inclusive),
    /// red &gt; 300.</summary>
    public static Level Classify(double ms, double yellowAboveMs, double redAboveMs)
    {
        if (double.IsNaN(ms)) return Level.Green;
        if (ms > redAboveMs) return Level.Red;
        if (ms > yellowAboveMs) return Level.Yellow;
        return Level.Green;
    }

    /// <summary>Label colour: no link (or still recovering) =&gt; Red; no value yet =&gt; Waiting (grey).</summary>
    public static Level ClassifyEcho(double ms, bool noLink, double yellowAboveMs, double redAboveMs)
    {
        if (noLink) return Level.Red;
        if (double.IsNaN(ms) || ms <= 0) return Level.Waiting;
        return Classify(ms, yellowAboveMs, redAboveMs);
    }

    /// <summary>Light exponential smoothing; first sample (prev &lt;= 0 or NaN) passes through; big jumps (&gt;2x)
    /// converge faster so a real spike shows within ~0.5 s.</summary>
    public static double Smooth(double prev, double sample, double alpha)
    {
        if (double.IsNaN(sample) || sample < 0) return prev;
        if (double.IsNaN(prev) || prev <= 0) return sample;
        alpha = Math.Clamp(alpha, 0.01, 1.0);
        if (sample > prev * 2 || sample < prev / 2) alpha = Math.Max(alpha, 0.6);
        return prev + (sample - prev) * alpha;
    }

    public static string Format(double ms) => double.IsNaN(ms) || ms <= 0 ? Loc.T("ping.none") : Loc.F("ping.local", Math.Round(ms));

    /// <summary>"no link N s": whole seconds since the last echo, at least 1 (the label appears after 0.6 s).</summary>
    public static string NoLinkText(double seconds) => Loc.F("ping.noLink", Math.Max(1, (int)Math.Ceiling(seconds - 1e-9)));

    /// <summary>HUD label text: the ping, plus "loss NN%" only when loss is noticeable (&gt;= minLossPct), so a single
    /// lost echo does not make the label flicker. No link =&gt; "no link N s" (noLinkSeconds &gt;= 0).</summary>
    public static string LabelText(double ms, double lossPct, double minLossPct, double noLinkSeconds = -1)
    {
        if (noLinkSeconds >= 0) return NoLinkText(noLinkSeconds);
        var t = Format(ms);
        if (double.IsNaN(ms) || ms <= 0 || double.IsNaN(lossPct) || minLossPct <= 0 || lossPct < minLossPct) return t;
        return t + "  " + Loc.F("ping.loss", Math.Round(lossPct));
    }

    /// <summary>cfg migration v3 (0.1.6 -&gt; 0.1.7): the old defaults OrangeFromMs=150 / RedAboveMs=250 are written into
    /// every cfg, so they would never change. Old defaults -&gt; new 200/300; values a user edited by hand are kept.</summary>
    public static (double yellowAbove, double redAbove) MigrateV3(double? oldOrangeFromMs, double redAboveMs)
    {
        var y = oldOrangeFromMs is { } o && Math.Abs(o - 150) > 0.01 ? o : 200;
        var r = Math.Abs(redAboveMs - 250) < 0.01 ? 300 : redAboveMs;
        if (r < y) r = y;
        return (y, r);
    }
}

/// <summary>
/// RTT over OUR UDP echo on the real Radmin path (client -&gt; host:Port+1, reply to the source port). Pure and
/// time-injected so it is unit-tested.
/// 0.1.7: "no link" after <see cref="NoLinkS"/> (0.6 s, ~3 missed pings at 5 Hz) without any reply; it ends only after
/// <see cref="RecoverReplies"/> (3) replies in a row (the label stays red meanwhile). Loss = unanswered share of the last
/// 20 pings older than TimeoutS (2 s, also the latest a reply still counts as an RTT sample).
/// </summary>
public sealed class EchoTracker
{
    public double TimeoutS = 2.0, Alpha = 0.3, NoLinkS = 0.6, StartGraceS = 10;
    public int RecoverReplies = 3;
    double _firstSent = -1, _lastReply = -1, _smoothed = double.NaN;
    bool _lost; int _streak; double _lostFrom = -1;
    uint _seq;
    readonly System.Collections.Generic.Dictionary<uint, double> _pending = new();
    readonly System.Collections.Generic.Queue<bool> _history = new();
    public double LastSampleMs = double.NaN;
    readonly System.Collections.Generic.List<uint> _dead = new();
    public int LinkLosses;   // how many times "no link" started

    public uint NextSeq(double now)
    {
        if (_firstSent < 0) _firstSent = now;
        var s = ++_seq;
        _pending[s] = now;
        Expire(now);
        Update(now);
        return s;
    }

    /// <summary>Reply for <paramref name="seq"/>; returns the RTT sample in ms or NaN (unknown/duplicate/late).</summary>
    public double OnReply(uint seq, double now)
    {
        if (!_pending.Remove(seq, out var sent)) return double.NaN;
        var ms = (now - sent) * 1000.0;
        if (ms < 0 || now - sent > TimeoutS) { Push(false); return double.NaN; }
        Update(now);
        Push(true);
        _lastReply = now;
        LastSampleMs = ms;
        _smoothed = PingLogic.Smooth(_smoothed, ms, Alpha);
        if (_lost && ++_streak >= RecoverReplies) { _lost = false; _streak = 0; }
        return ms;
    }

    double Last => _lastReply >= 0 ? _lastReply : _firstSent;

    void Update(double now)
    {
        if (!Started) return;
        // no reply yet: "waiting" (local --) for StartGraceS, not "no link" (test g12b2: "no link 3 s" while the host was loading)
        if (_lastReply < 0 && now - _firstSent < StartGraceS) return;
        if (now - Last > NoLinkS)
        {
            if (!_lost) { _lost = true; _lostFrom = Last; LinkLosses++; }
            else if (_streak > 0 && _lastReply >= 0) _lostFrom = _lastReply;   // recovery interrupted: count from there
            _streak = 0;
        }
    }

    void Expire(double now)
    {
        _dead.Clear();   // reused: no allocation per ping
        foreach (var kv in _pending) if (now - kv.Value > TimeoutS) _dead.Add(kv.Key);
        foreach (var k in _dead) { _pending.Remove(k); Push(false); }
    }

    void Push(bool ok) { _history.Enqueue(ok); while (_history.Count > 20) _history.Dequeue(); }

    public bool Started => _firstSent >= 0;
    public bool HasReply => _lastReply >= 0;
    /// <summary>No link right now, or not yet recovered (3 replies in a row).</summary>
    public bool TimedOut(double now) { Update(now); return _lost; }
    /// <summary>Seconds without any reply while the link is down; -1 when replies are flowing (also while recovering).</summary>
    public double NoLinkSeconds(double now) { Update(now); return _lost && now - Last > NoLinkS ? now - _lostFrom : -1; }
    /// <summary>Smoothed ms, or NaN while waiting for the first reply or while no reply is coming.</summary>
    public double Value(double now) => _lastReply < 0 || NoLinkSeconds(now) >= 0 ? double.NaN : _smoothed;
    public double LossPercent { get { if (_history.Count == 0) return 0; int bad = 0; foreach (var h in _history) if (!h) bad++; return 100.0 * bad / _history.Count; } }
    public void Reset() { _firstSent = -1; _lastReply = -1; _smoothed = double.NaN; _pending.Clear(); _history.Clear(); LastSampleMs = double.NaN; _lost = false; _streak = 0; }
}

public static class CarSyncLogic
{
    /// <summary>Consecutive host snaps of one car: count up when the previous one was &lt; 0.25 s ago, else restart at 1.</summary>
    public static int SnapStreak(double sincePrevS, int streak) => sincePrevS < 0.25 ? streak + 1 : 1;

    public enum Mode { Low, High }
    /// <summary>None: leave the host's simulation alone. Blend: low-ping interpolation. Snap: hard alignment.
    /// SoftSnap: start a short fast blend (a checkpoint correction others see as a slide, not a jump).
    /// SoftBlend: a frame inside that window. CheckpointOk: checkpoint due but error already tiny (no correction).</summary>
    public enum Action { None, Blend, Snap, SoftSnap, SoftBlend, CheckpointOk }

    public sealed class Cfg
    {
        public double TeleportMeters = 50;
        public double HighEnterMs = 165, HighExitMs = 135;     // hysteresis around the user's 150 ms
        public double CheckpointSeconds = 1.5;
        public double DesyncSnapMeters = 3.0, DesyncSnapDegrees = 25.0;
        public double LowSnapMeters = 8.0;                     // even in low-ping mode, very large errors snap
        public double MaxPoseAgeSeconds = 1.0;                 // older client poses are ignored (client stalled)
        public double CheckpointMinMeters = 0.25, CheckpointMinDegrees = 3.0;   // user 2026-10-21: skip tiny checkpoint snaps
        public double CheckpointSoftSeconds = 0.2;             // 0 = hard checkpoint snap (old behaviour)
    }

    public static Mode NextMode(Mode current, double rttMs, Cfg c)
    {
        if (double.IsNaN(rttMs) || rttMs <= 0) return current;
        if (current == Mode.Low && rttMs > c.HighEnterMs) return Mode.High;
        if (current == Mode.High && rttMs < c.HighExitMs) return Mode.Low;
        return current;
    }

    /// <summary>What the HOST does with the latest client pose (client is authoritative for its own car).</summary>
    public static Action Decide(Mode mode, double errMeters, double errDegrees, double sinceCheckpointS, double poseAgeS, Cfg c,
                                bool contactPaused = false, bool softActive = false)
    {
        if (poseAgeS > c.MaxPoseAgeSeconds) return Action.None;
        // Larger than any plausible drift: a game-driven teleport (spawn from the lobby, cabin travel, rescue) that
        // Fusion is already replicating. Fighting it made the host snap every frame (self-test run7: 21 x 1284 m).
        if (errMeters > c.TeleportMeters) return Action.None;
        // Car-to-car contact on the host: let the host's physics resolve it (pushing a stuck friend, bumps), resume after.
        if (contactPaused) return Action.None;
        if (mode == Mode.Low)
            return errMeters > c.LowSnapMeters ? Action.Snap : Action.Blend;
        if (errMeters > c.DesyncSnapMeters || errDegrees > c.DesyncSnapDegrees) return Action.Snap;   // desync (unchanged)
        if (softActive) return Action.SoftBlend;
        if (sinceCheckpointS >= c.CheckpointSeconds)                                                  // checkpoint
        {
            if (errMeters < c.CheckpointMinMeters && errDegrees < c.CheckpointMinDegrees) return Action.CheckpointOk;
            return c.CheckpointSoftSeconds > 0 ? Action.SoftSnap : Action.Snap;
        }
        return Action.None;   // between checkpoints the host's own simulation (from the client's inputs) runs freely
    }

    /// <summary>Car-to-car contact test in the car's local frame (x right, y up, z forward): the other car's centre
    /// <paramref name="lx"/>,<paramref name="ly"/>,<paramref name="lz"/> lies within two car half-extents plus a
    /// margin. Conservative for diagonal cars (pauses slightly early), which is the safe side.</summary>
    public static bool InContact(double lx, double ly, double lz, double halfW, double halfL, double halfH, double margin)
        => Math.Abs(lx) < 2 * halfW + margin && Math.Abs(lz) < 2 * halfL + margin && Math.Abs(ly) < 2 * halfH + margin;

    /// <summary>Host-side jump of a car between two frames that CarSync did not cause: the game teleported it
    /// (portable jack / respawn / teleport RPCs all run on the host). Moved further than speed allows, or turned a lot.</summary>
    public static bool HostJumped(double movedM, double turnedDeg, double speedMs, double dt)
        => movedM > speedMs * Math.Max(dt, 0.02) * 2.0 + 1.0 || turnedDeg > 30.0;

    /// <summary>How long to leave the car alone after a host-side jump: until the client's poses can reflect it.</summary>
    public static double JumpHoldSeconds(double rttMs) => Math.Max(0.5, (double.IsNaN(rttMs) ? 300 : rttMs) * 1.5 / 1000.0 + 0.3);

    /// <summary>Contact pause: active while in contact and for holdS seconds after the last contact.</summary>
    public static bool ContactPaused(double now, double lastContactAt, double holdS)
        => lastContactAt > 0 && now - lastContactAt <= holdS;

    /// <summary>Blend factor for a frame of dt seconds with time constant tau (frame-rate independent).</summary>
    public static double BlendFactor(double dt, double tau) => tau <= 0 ? 1.0 : 1.0 - Math.Exp(-Math.Max(0, dt) / tau);

    /// <summary>Extrapolate a 1-D component by velocity over the pose age (one-way latency + time since receipt).</summary>
    public static double Extrapolate(double p, double v, double ageS) => p + v * Math.Clamp(ageS, -0.5, 0.5);

    // ---- wire format (little endian) ----
    public const uint PoseMagic = 0x3250544F;  // "OTP2" (0.1.5 final: client tick instead of client clock; "OTP1" is ignored)
    public const uint PingMagic = 0x4750544F;  // "OTPG": magic, kind(0=request,1=reply), slot, seq(u32), time(f64) = 18 B
    public const int PingSize = 18;            // + optional flags byte from the host (bit0 = CarSync on for the session)
    public const byte HostSlot = 255;
    public const byte FlagCarSync = 1;

    public static byte[] EncodePing(byte kind, byte slot, uint seq, double t, int flags = -1)
    {
        var b = new byte[flags >= 0 ? PingSize + 1 : PingSize];
        BitConverter.TryWriteBytes(b.AsSpan(0), PingMagic); b[4] = kind; b[5] = slot;
        BitConverter.TryWriteBytes(b.AsSpan(6), seq); BitConverter.TryWriteBytes(b.AsSpan(10), t);
        if (flags >= 0) b[PingSize] = (byte)flags;
        return b;
    }

    public static bool TryDecodePing(byte[] b, out byte kind, out byte slot, out uint seq) => TryDecodePing(b, out kind, out slot, out seq, out _);

    /// <summary>flags = -1 when the sender attached none (client packets, or a host without the flags byte).</summary>
    public static bool TryDecodePing(byte[] b, out byte kind, out byte slot, out uint seq, out int flags)
    {
        kind = 0; slot = 0; seq = 0; flags = -1;
        if (b == null || b.Length < PingSize || b.Length > PingSize + 1 || BitConverter.ToUInt32(b, 0) != PingMagic) return false;
        kind = b[4]; slot = b[5]; seq = BitConverter.ToUInt32(b, 6);
        if (b.Length > PingSize) flags = b[PingSize];
        return kind <= 1;
    }

    /// <summary>Seconds between the tick a client pose was sampled at and the host's current tick. Both peers run the
    /// same Fusion tick timeline, so this needs no clock sync and already contains the real one-way delay AND the
    /// client's prediction lead (a client predicts ahead of the host, so the value is ~0 or even negative; "RTT/2"
    /// overshot by v*RTT/2). Clamped to [-0.5, 0.5] s.</summary>
    public static double PoseAgeFromTicks(int hostTick, int poseTick, double tickDt)
    {
        if (tickDt <= 0 || poseTick <= 0 || hostTick <= 0) return 0;
        return Math.Clamp((hostTick - poseTick) * tickDt, -0.5, 0.5);
    }

    /// <summary>Accept a pose unless it is older/duplicate than the last one; a sequence restart (client restarted)
    /// is accepted once the previous stream has been silent for 2 s.</summary>
    public static bool AcceptSeq(uint newSeq, uint lastSeq, double sinceLastS) =>
        unchecked((int)(newSeq - lastSeq)) > 0 || sinceLastS > 2.0;

    public const int PoseSize = 4 + 1 + 4 + 8 + 13 * 4;

    public struct Pose
    {
        public byte Slot; public uint Seq; public int Tick; public int Reserved;
        public float Px, Py, Pz, Rx, Ry, Rz, Rw, Vx, Vy, Vz, Ax, Ay, Az;
    }

    public static byte[] Encode(in Pose p)
    {
        var b = new byte[PoseSize]; int o = 0;
        void U(uint v) { BitConverter.TryWriteBytes(b.AsSpan(o), v); o += 4; }
        void F(float v) { BitConverter.TryWriteBytes(b.AsSpan(o), v); o += 4; }
        U(PoseMagic); b[o++] = p.Slot; U(p.Seq); U((uint)p.Tick); U((uint)p.Reserved);
        F(p.Px); F(p.Py); F(p.Pz); F(p.Rx); F(p.Ry); F(p.Rz); F(p.Rw); F(p.Vx); F(p.Vy); F(p.Vz); F(p.Ax); F(p.Ay); F(p.Az);
        return b;
    }

    public static bool TryDecode(byte[] b, out Pose p)
    {
        p = default;
        if (b == null || b.Length != PoseSize || BitConverter.ToUInt32(b, 0) != PoseMagic) return false;
        int o = 4; p.Slot = b[o++]; p.Seq = BitConverter.ToUInt32(b, o); o += 4; p.Tick = BitConverter.ToInt32(b, o); o += 4; p.Reserved = BitConverter.ToInt32(b, o); o += 4;
        float F() { var v = BitConverter.ToSingle(b, o); o += 4; return v; }
        p.Px = F(); p.Py = F(); p.Pz = F(); p.Rx = F(); p.Ry = F(); p.Rz = F(); p.Rw = F();
        p.Vx = F(); p.Vy = F(); p.Vz = F(); p.Ax = F(); p.Ay = F(); p.Az = F();
        return float.IsFinite(p.Px) && float.IsFinite(p.Py) && float.IsFinite(p.Pz) && float.IsFinite(p.Rx) && float.IsFinite(p.Ry) &&
               float.IsFinite(p.Rz) && float.IsFinite(p.Rw) && float.IsFinite(p.Vx) && float.IsFinite(p.Vy) && float.IsFinite(p.Vz) &&
               float.IsFinite(p.Ax) && float.IsFinite(p.Ay) && float.IsFinite(p.Az);   // no per-packet array allocation
    }
}

/// <summary>
/// 0.1.7 host side: what to do with a client's car while its poses are not arriving (link outage) and when they come
/// back (ROADMAP 1.3/1.4). g = seconds since the last pose was received (poses come at 20 Hz). Pure, unit-tested.
/// </summary>
public static class OutageLogic
{
    public enum Phase { Normal, Extrapolate, PhysicsOnly, Brake }
    public enum Recovery { None, Converge, Snap, WaitContact, Teleport }

    public sealed class Cfg
    {
        public double NormalS = 0.15;          // below: normal (also covers partial loss up to ~30 %)
        public double ExtrapolateS = 1.5;      // 0.15..1.5: velocity extrapolation with decaying confidence
        public double PhysicsS = 1.5;          // ExtrapolateS..PhysicsS: the host's physics alone (empty by default); above: gentle braking
        public double BrakeS = 1.0;            // braking to 0 takes about this long
        public double PosHorizonS = 1.0;       // target position by velocity at most this far ahead (informative only)
        public double RotHorizonS = 0.25;      // rotation by angular velocity at most this far ahead
        public double QuietMeters = 0.5, QuietDegrees = 5;   // recovery below both: nothing special
        public double SnapMeters = 15;         // 0.5..15: soft convergence; 15..50: snap (not in contact)
        public double TeleportMeters = 50;     // above: the game moved the car; leave it to Fusion
        public double ContactWaitS = 2;        // a snap waits this long for a contact to end, then converges softly
        public double FlippedDegrees = 60;     // recovery with a bigger angle: 0.5 s convergence, snap only when slow
    }

    public static Phase PhaseOf(double gapS, Cfg c) =>
        gapS < c.NormalS ? Phase.Normal : gapS < c.ExtrapolateS ? Phase.Extrapolate : gapS < c.PhysicsS ? Phase.PhysicsOnly : Phase.Brake;

    /// <summary>Confidence in the last pose: 1 at g = 0, linearly to 0 at g = ExtrapolateS.</summary>
    public static double Confidence(double gapS, Cfg c) => Math.Clamp(1.0 - gapS / c.ExtrapolateS, 0, 1);

    /// <summary>Time constant of the velocity/heading pull during extrapolation: tau = 0.1 / confidence (∞ at 0).</summary>
    public static double ExtrapolateTau(double gapS, Cfg c)
    {
        var w = Confidence(gapS, c);
        return w <= 1e-3 ? double.PositiveInfinity : 0.1 / w;
    }

    /// <summary>Horizontal speed factor for one frame of the braking phase: exponential with tau = BrakeS/4 (≈98 % gone
    /// after BrakeS).</summary>
    public static double BrakeFactor(double dt, Cfg c) => Math.Exp(-Math.Max(0, dt) / Math.Max(0.05, c.BrakeS / 4));

    public static double ConvergeSeconds(double errMeters) => Math.Clamp(0.3 + 0.08 * errMeters, 0.3, 1.5);

    /// <summary>Decision on the first fresh pose after a gap (g &gt; NormalS).</summary>
    public static Recovery Decide(double errMeters, double errDegrees, bool contact, double waitedS, double hostSpeed, Cfg c, out double convergeS)
    {
        convergeS = 0;
        if (errMeters > c.TeleportMeters) return Recovery.Teleport;
        if (errDegrees > c.FlippedDegrees && errMeters <= c.SnapMeters)
        {   // flipped/rolled on one side: never a hard flip while moving
            if (hostSpeed < 2 && !contact) return Recovery.Snap;
            convergeS = 0.5; return Recovery.Converge;
        }
        if (errMeters < c.QuietMeters && errDegrees < c.QuietDegrees) return Recovery.None;
        if (errMeters <= c.SnapMeters) { convergeS = ConvergeSeconds(errMeters); return Recovery.Converge; }
        if (!contact) return Recovery.Snap;
        if (waitedS < c.ContactWaitS) return Recovery.WaitContact;
        convergeS = 1.5; return Recovery.Converge;   // contact did not end in time: no snap into another car
    }

    /// <summary>Per-frame weights during a convergence of length T, elapsed e: velocity/rotation first (tau = T/6 from
    /// the start), position joins gradually (weight 0 -&gt; 1 over the first 30 % of T, tau = T/5); ~97 % of the error is
    /// gone at T. Returns (velocity/rotation factor, position factor) for a frame of dt.</summary>
    public static (double velRot, double pos) ConvergeFactors(double elapsed, double T, double dt)
    {
        if (T <= 0) return (1, 1);
        var vr = 1.0 - Math.Exp(-Math.Max(0, dt) / (T / 6));
        var ramp = Math.Clamp(elapsed / (0.3 * T), 0, 1);
        var p = ramp * (1.0 - Math.Exp(-Math.Max(0, dt) / (T / 5)));
        return (vr, p);
    }

    /// <summary>Do not roll the host's car onto its roof because of a client pose that is upside down while the car on
    /// the host stands on its wheels at nearly the same place (a client-side glitch is likelier than a host one).</summary>
    public static bool KeepUpright(double errMeters, double hostUpY, double clientUpY) => errMeters < 1.0 && hostUpY > 0.5 && clientUpY < 0;

    /// <summary>Over-the-car label for other players: from the physics-only phase on (g &gt;= ExtrapolateS).</summary>
    public static bool ShowCarLabel(double noLinkS, Cfg c) => noLinkS >= c.ExtrapolateS;

    // ---- host -> clients link status ("OTST"): which players the host has no link to, so everyone can show it ----
    public const uint StatusMagic = 0x5453544F;
    public static byte[] EncodeStatus(System.Collections.Generic.IReadOnlyList<(byte slot, double noLinkS)> items)
    {
        var n = Math.Min(items.Count, 16);
        var b = new byte[5 + 3 * n];
        BitConverter.TryWriteBytes(b.AsSpan(0), StatusMagic); b[4] = (byte)n;
        for (int i = 0; i < n; i++)
        {
            b[5 + 3 * i] = items[i].slot;
            BitConverter.TryWriteBytes(b.AsSpan(6 + 3 * i), (ushort)Math.Clamp(Math.Round(items[i].noLinkS * 10), 0, 65535));
        }
        return b;
    }

    public static bool TryDecodeStatus(byte[] b, System.Collections.Generic.List<(byte slot, double noLinkS)> into)
    {
        into.Clear();
        if (b == null || b.Length < 5 || BitConverter.ToUInt32(b, 0) != StatusMagic) return false;
        int n = b[4]; if (n > 16 || b.Length != 5 + 3 * n) return false;
        for (int i = 0; i < n; i++) into.Add((b[5 + 3 * i], BitConverter.ToUInt16(b, 6 + 3 * i) / 10.0));
        return true;
    }

    // ---- host -> client "take my pose" ("OTAC"): the game moved this player's car on the host (rescue, reset, teleport) ----
    public const uint AcceptMagic = 0x4341544F;
    public static byte[] EncodeAccept(byte slot, ushort counter)
    {
        var b = new byte[7];
        BitConverter.TryWriteBytes(b.AsSpan(0), AcceptMagic); b[4] = slot; BitConverter.TryWriteBytes(b.AsSpan(5), counter);
        return b;
    }
    public static bool TryDecodeAccept(byte[] b, out byte slot, out ushort counter)
    {
        slot = 0; counter = 0;
        if (b == null || b.Length != 7 || BitConverter.ToUInt32(b, 0) != AcceptMagic) return false;
        slot = b[4]; counter = BitConverter.ToUInt16(b, 5);
        return true;
    }
}

/// <summary>
/// 0.1.7: the client's Fusion simulation can stay frozen for good after heavy loss (self-test n1/n2: 50 % loss per
/// direction for 30 s; the tick never moves again although packets flow and the session stays "running"; happens with
/// and without the own-car hooks). Detect it and rejoin (the host keeps the slot 45 s).
/// Frozen = the runner tick has not advanced; healthy = the OTH-LAN side channel to the same host answers with low loss
/// (so it is not an outage, where freezing is normal and recovers by itself).
/// </summary>
public static class FreezeLogic
{
    public sealed class Cfg
    {
        public double FrozenS = 12;      // tick unchanged this long ...
        public double HealthyS = 5;      // ... while the side channel has been healthy this long
        public double HealthyLossPct = 15;
        public double RetryS = 60;       // at most one rejoin per minute
        public int MaxAttempts = 3;      // per session target
    }

    /// <summary>Side channel healthy now: replies flowing and loss below the threshold.</summary>
    public static bool Healthy(bool timedOut, double lossPct, Cfg c) => !timedOut && lossPct <= c.HealthyLossPct;

    public static bool ShouldRejoin(double frozenS, double healthyS, int attempts, double sinceLastAttemptS, Cfg c) =>
        frozenS >= c.FrozenS && healthyS >= c.HealthyS && attempts < c.MaxAttempts && sinceLastAttemptS >= c.RetryS;
}

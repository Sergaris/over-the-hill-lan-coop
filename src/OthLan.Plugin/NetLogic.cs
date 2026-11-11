using System;
using System.Collections.Generic;

namespace OthLan;

/// <summary>0.1.8 netcode v2: pure decisions for the client's own car (unit-tested, no Unity).</summary>
public static class OwnCarLogic
{
    /// <summary>Take the host's pose because it is very far away (a game teleport whose OTAC was lost)? Never within
    /// <paramref name="postGapS"/> after a link gap: then the host copy is merely behind and converges to us.</summary>
    public static bool TakeHostPose(double errMeters, double sinceLinkGapS, double acceptMeters, double postGapS)
        => errMeters > acceptMeters && sinceLinkGapS >= postGapS;

    /// <summary>
    /// Whether a resim gap belongs in rollback / NetStats own-correction means.
    /// Skip: pre-phase-2, the spawn-transition sample itself (AcceptMeters teleport), and ticks where
    /// <c>Restore()</c> put the client's pose back (invisible on screen). Keep accept-window samples —
    /// the car really rides the host pose there.
    /// </summary>
    public static bool CountOwnCorrection(bool phase2Active, bool restoredOwn, bool spawnTransition)
        => phase2Active && !restoredOwn && !spawnTransition;

    /// <summary>
    /// Phase-2 late/resim restore: skip the expensive VPP <c>RestoreSnapshot</c> when Fusion already left us
    /// within noise (0.2.5.1 live: friend LateFixes≈every tick → thousands of word+VPP restores, frameMs≈30).
    /// </summary>
    public static bool SkipCheapRestore(double errMeters, double errDeg, double maxMeters, double maxDeg)
        => errMeters <= maxMeters && errDeg <= maxDeg;

    /// <summary>What phase 2 does with the own car after a prediction reset. Listed by priority.</summary>
    public enum ResetAction
    {
        /// <summary>The host moved the car (OTAC: respawn, rescue, jack): open the accept window, keep the host pose.</summary>
        AcceptOtac,
        /// <summary>Accept window open: keep the host pose.</summary>
        KeepHost,
        /// <summary>Our own physics state blew up: host-authoritative (phase 1) for a while.</summary>
        Fallback,
        /// <summary>Host pose very far away (a teleport whose OTAC was lost): open the accept window.</summary>
        TakeHostFar,
        /// <summary>The resimulation landed within noise: keep it, put back only the render Previous (no VPP restore).</summary>
        CheapPrevious,
        /// <summary>Put our own pre-reset state back.</summary>
        RestoreOwn,
    }

    public const double CheapMeters = 0.05, CheapDegrees = 1.0;
    public const double InvalidSpeed = 150, InvalidAngularSpeed = 60;

    /// <summary>Our captured pre-reset state is not something to keep (NaN position, physics blow-up).</summary>
    public static bool OwnStateInvalid(double x, double y, double z, double speed, double angularSpeed)
        => !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z) || speed > InvalidSpeed || angularSpeed > InvalidAngularSpeed;

    /// <summary>Decision known before the resimulation runs (never <see cref="ResetAction.CheapPrevious"/>).
    /// <paramref name="errMeters"/>: host pose vs ours at the same tick.</summary>
    public static ResetAction AfterReset(bool otacPending, bool acceptWindow, bool ownInvalid, double errMeters,
                                         double sinceLinkGapS, double acceptMeters, double postGapS)
    {
        if (otacPending) return ResetAction.AcceptOtac;
        if (acceptWindow) return ResetAction.KeepHost;
        if (ownInvalid) return ResetAction.Fallback;
        if (TakeHostPose(errMeters, sinceLinkGapS, acceptMeters, postGapS)) return ResetAction.TakeHostFar;
        return ResetAction.RestoreOwn;
    }

    /// <summary>Refine <paramref name="plan"/> with a real resimulation result (err = resim pose vs ours).</summary>
    public static ResetAction AfterResim(ResetAction plan, double errMeters, double errDeg)
        => plan == ResetAction.RestoreOwn && SkipCheapRestore(errMeters, errDeg, CheapMeters, CheapDegrees) ? ResetAction.CheapPrevious : plan;

    /// <summary>Inputs of <see cref="CanRunEmptyResim"/>.</summary>
    public struct EmptyResimCheck
    {
        public bool Enabled, HookReady, Phase2Active, Spawned, RemotesInterpolated, OwnStateCaptured;
        public int Ticks;
        public ResetAction Plan;
    }

    /// <summary>
    /// 0.2.7 OwnResim: the resimulation may run without scene physics and without our car's vehicle step only when phase 2
    /// will put our own state back whatever it yields (<see cref="ResetAction.RestoreOwn"/>, known before the loop).
    /// Remote cars must be interpolated (not simulated): a predicted remote needs real resim physics.
    /// </summary>
    public static bool CanRunEmptyResim(in EmptyResimCheck c)
        => c.Enabled && c.HookReady && c.Phase2Active && c.Spawned && c.RemotesInterpolated && c.OwnStateCaptured
           && c.Ticks > 0 && c.Plan == ResetAction.RestoreOwn;

    /// <summary>OwnLocal: do not pile Physics.Simulate when this frame is already over budget (death spiral).</summary>
    public static bool AllowLocalPhysicsStep(double frameDt, double budgetS)
        => frameDt > 0 && frameDt <= budgetS;

    /// <summary>Fusion client input buffer size in ticks for <paramref name="seconds"/> (never smaller than the game's).</summary>
    public static int InputCapacity(double seconds, int tickRate, int current)
    {
        if (seconds <= 0 || tickRate <= 0) return current;
        var want = (int)Math.Ceiling(Math.Min(seconds, 20) * tickRate);
        return Math.Max(current, want);
    }
}

/// <summary>Host-side v2 decisions (unit-tested).</summary>
public static class HostFollowLogic
{
    /// <summary>0.1.8 v3 velocity servo (Unreal's physics replication: dv = k (x_target - x), PositionLerp ~0): the
    /// correction velocity for a position error <paramref name="err"/> (m) with time constant <paramref name="tau"/> (s),
    /// capped at <paramref name="maxCorr"/> + <paramref name="speedFactor"/> x the client's speed (m/s). Returns the
    /// length of the correction velocity (direction = error direction).</summary>
    public static double ServoSpeed(double err, double tau, double maxCorr, double speedFactor, double clientSpeed)
    {
        if (err <= 0 || tau <= 0) return 0;
        return Math.Min(err / tau, maxCorr + speedFactor * Math.Max(0, clientSpeed));
    }

    /// <summary>
    /// Angular twin of <see cref="ServoSpeed"/>: correction rate (rad/s) for |orientation error| with time
    /// constant <paramref name="tau"/>, capped at <paramref name="maxRadS"/>. Direction is caller's axis×sign.
    /// </summary>
    public static double ServoAngSpeed(double errAbsRad, double tau, double maxRadS)
    {
        if (errAbsRad <= 0 || tau <= 0 || maxRadS <= 0) return 0;
        return Math.Min(errAbsRad / tau, maxRadS);
    }

    /// <summary>A jump of the host copy that the game made (respawn, rescue, jack, reset upright) and not the network:
    /// the car jumped (<see cref="CarSyncLogic.HostJumped"/>) AND moved away from where the client drives it (or turned
    /// away from the client's heading). 0.1.7 sent OTAC on the jump alone; at 200 ms that also fired on the host's own
    /// corrections and made the client roll back.</summary>
    public static bool GameMovedCar(double movedM, double turnedDeg, double speedMs, double dt,
                                    double distToClientBefore, double distToClientAfter, double degToClientBefore, double degToClientAfter)
    {
        if (!CarSyncLogic.HostJumped(movedM, turnedDeg, speedMs, dt)) return false;
        return distToClientAfter > distToClientBefore + 3.0 || (turnedDeg > 30 && degToClientAfter > degToClientBefore + 30);
    }

    /// <summary>Link back after a pose gap: 0 = nothing, converge over T seconds, or snap. Unlike 0.1.7, errors above the
    /// teleport threshold also snap (the client kept driving during the gap; a game teleport sends OTAC instead).</summary>
    public static OutageLogic.Recovery Recover(double errMeters, double errDeg, bool contact, double hostSpeed, OutageLogic.Cfg c, out double convergeS)
    {
        var r = OutageLogic.Decide(errMeters, errDeg, contact, 0, hostSpeed, c, out convergeS);
        if (r == OutageLogic.Recovery.Teleport) { convergeS = 0; return OutageLogic.Recovery.Snap; }
        return r;
    }
}

/// <summary>Smoothness of a moving object as seen on screen: per frame, how far the drawn position is from where it
/// would be if it kept last frame's velocity ("hitch", metres). Steady driving gives ~0 (a = 10 m/s² at 60 fps:
/// 3 mm); a pop/rubber-band shows up as a spike.</summary>
public sealed class Smoothness
{
    readonly List<double> _hitch = new();
    bool _has1, _has2; double _px, _py, _pz, _vx, _vy, _vz;
    public int Frames, Over10cm, Over50cm, Over2m; public double Max;
    public void Reset() { _hitch.Clear(); _has1 = _has2 = false; Frames = Over10cm = Over50cm = Over2m = 0; Max = 0; }
    /// <summary>Start over the velocity history (teleport, respawn) without losing the statistics.</summary>
    public void Break() { _has1 = _has2 = false; }
    public double Add(double x, double y, double z, double dt)
    {
        double h = 0;
        if (dt <= 1e-4) return 0;
        if (_has2)
        {
            double ex = _px + _vx * dt, ey = _py + _vy * dt, ez = _pz + _vz * dt;
            h = Math.Sqrt((x - ex) * (x - ex) + (y - ey) * (y - ey) + (z - ez) * (z - ez));
            if (h > 50) { Break(); _px = x; _py = y; _pz = z; _has1 = true; return 0; }   // teleport: not a hitch
            _hitch.Add(h); Frames++;
            if (h > 0.1) Over10cm++; if (h > 0.5) Over50cm++; if (h > 2) Over2m++; if (h > Max) Max = h;
        }
        if (_has1) { _vx = (x - _px) / dt; _vy = (y - _py) / dt; _vz = (z - _pz) / dt; _has2 = true; }
        _px = x; _py = y; _pz = z; _has1 = true;
        return h;
    }
    /// <summary>Count a hitch measured elsewhere (aggregate over several cars).</summary>
    public void Record(double h)
    {
        _hitch.Add(h); Frames++;
        if (h > 0.1) Over10cm++; if (h > 0.5) Over50cm++; if (h > 2) Over2m++; if (h > Max) Max = h;
    }
    public double Percentile(double p)
    {
        if (_hitch.Count == 0) return 0;
        var a = _hitch.ToArray(); Array.Sort(a);
        var i = (int)Math.Clamp(Math.Ceiling(p / 100.0 * a.Length) - 1, 0, a.Length - 1);
        return a[i];
    }
    public string Describe() => Frames == 0 ? "no frames" :
        $"frames={Frames} p50={Percentile(50) * 100:0.0}cm p95={Percentile(95) * 100:0.0}cm p99={Percentile(99) * 100:0.0}cm max={Max * 100:0}cm >10cm={Over10cm} >50cm={Over50cm} >2m={Over2m}";
}

/// <summary>Gaps between host snapshots as seen by a client (count of stalls by length).</summary>
public sealed class GapStats
{
    public int Over250ms, Over500ms, Over1s, Over3s; public double Max;
    public void Add(double gapS)
    {
        if (gapS > 0.25) Over250ms++; if (gapS > 0.5) Over500ms++; if (gapS > 1) Over1s++; if (gapS > 3) Over3s++;
        if (gapS > Max) Max = gapS;
    }
    public void Reset() { Over250ms = Over500ms = Over1s = Over3s = 0; Max = 0; }
    public string Describe() => $">0.25s={Over250ms} >0.5s={Over500ms} >1s={Over1s} >3s={Over3s} max={Max:0.00}s";
}

/// <summary>Self-test / screenshot: scrub a real LAN address out of UI strings (owner of DemoIp replace).</summary>
public static class NetInfoLogic
{
    /// <summary>Replace <paramref name="realIp"/> with <paramref name="demoIp"/> inside <paramref name="s"/>.
    /// No-op when any argument is null/empty (does not cache a missing Radmin address).</summary>
    public static string MaskIp(string s, string realIp, string demoIp)
    {
        if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(realIp) || string.IsNullOrEmpty(demoIp)) return s;
        return s.Replace(realIp, demoIp);
    }
}

/// <summary>The game's room flags in a host's session properties ("RoomProperties" = "i:N"; bit 0 = AllowMods).</summary>
public static class RoomPropsLogic
{
    public static bool? AllowMods(IReadOnlyDictionary<string, string> props)
    {
        if (props == null || !props.TryGetValue("RoomProperties", out var v) || v == null) return null;
        v = v.Trim();
        if (v.StartsWith("i:")) v = v[2..];
        return int.TryParse(v, out var n) ? (n & 1) != 0 : null;
    }
}

/// <summary>Vertical menu navigation order (main menu with our button): which entries link up/down.</summary>
public static class MenuNavLogic
{
    /// <summary>Indices (into <paramref name="usable"/>) of each entry's up and down neighbour; -1 = none.</summary>
    public static (int up, int down)[] Vertical(bool[] usable, bool wrap)
    {
        var idx = new List<int>();
        for (int i = 0; i < usable.Length; i++) if (usable[i]) idx.Add(i);
        var res = new (int, int)[usable.Length];
        for (int i = 0; i < res.Length; i++) res[i] = (-1, -1);
        for (int k = 0; k < idx.Count; k++)
        {
            int up = k > 0 ? idx[k - 1] : wrap && idx.Count > 1 ? idx[^1] : -1;
            int dn = k + 1 < idx.Count ? idx[k + 1] : wrap && idx.Count > 1 ? idx[0] : -1;
            res[idx[k]] = (up, dn);
        }
        return res;
    }
}

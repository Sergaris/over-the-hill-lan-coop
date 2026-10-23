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

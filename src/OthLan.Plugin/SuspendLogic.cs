using System;

namespace OthLan;

/// <summary>
/// 0.1.9 R3/U2: when to suspend the client's OwnLocalClock + phase-2 authority during car-to-car contact or winching.
/// Pure logic (unit-tested). Detection and host-flag wiring live in OwnCar / CarSync; this owns enter/hold/exit only.
/// </summary>
public static class SuspendLogic
{
    public sealed class Cfg
    {
        public double ResumeSeconds = 1.0;   // leave after neither source has seen contact for this long
        public double WinchExtraSeconds = 1.0;
        public bool SuspendOnContact = true;
        public bool SuspendOnWinch = true;
    }

    public enum Reason : byte { None = 0, Contact = 1, Winch = 2, Both = 3 }

    /// <summary>Combine local detection with the host's authoritative flag.</summary>
    public static bool ActiveNow(bool localContact, bool localWinch, bool hostContact, bool hostWinch, Cfg c)
    {
        var contact = c.SuspendOnContact && (localContact || hostContact);
        var winch = c.SuspendOnWinch && (localWinch || hostWinch);
        return contact || winch;
    }

    public static Reason Why(bool localContact, bool localWinch, bool hostContact, bool hostWinch, Cfg c)
    {
        var contact = c.SuspendOnContact && (localContact || hostContact);
        var winch = c.SuspendOnWinch && (localWinch || hostWinch);
        if (contact && winch) return Reason.Both;
        if (contact) return Reason.Contact;
        if (winch) return Reason.Winch;
        return Reason.None;
    }

    /// <summary>Hysteresis: enter immediately when ActiveNow; leave only after clearForSeconds without activity.</summary>
    public static bool Hold(bool currentlyActive, double now, ref double lastActiveAt, double resumeSeconds)
    {
        if (currentlyActive) { lastActiveAt = now; return true; }
        if (lastActiveAt <= 0) return false;
        return now - lastActiveAt <= resumeSeconds;
    }

    /// <summary>Resume delay: at least ResumeSeconds, stretched by RTT + interpolation delay estimate.</summary>
    public static double ResumeDelay(Cfg c, double rttMs, double interpDelayS)
    {
        var net = (double.IsNaN(rttMs) ? 200 : Math.Max(0, rttMs)) / 1000.0 + Math.Max(0, interpDelayS);
        return Math.Max(c.ResumeSeconds, 1.0 + net);
    }

    /// <summary>Predictive contact margin on the client (remote poses are in the past): base + |v_rel| * lookAhead, capped.</summary>
    public static double PredictiveMargin(double baseMargin, double relSpeedMs, double lookAheadS, double capMeters = 3.0)
        => Math.Min(capMeters, Math.Max(0, baseMargin) + Math.Max(0, relSpeedMs) * Math.Max(0, lookAheadS));
}

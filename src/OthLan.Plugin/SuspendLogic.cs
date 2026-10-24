using System;

namespace OthLan;

/// <summary>
/// Legacy enter/hold helpers (0.1.9). New authority modes live in <see cref="InteractionLogic"/> (T4.1).
/// Kept for call sites / tests that still use contact+winch bools without Car/World distinction.
/// </summary>
public static class SuspendLogic
{
    public sealed class Cfg
    {
        public double ResumeSeconds = 1.0;
        public double WinchExtraSeconds = 1.0;
        public bool SuspendOnContact = true;
        public bool SuspendOnWinch = true;
    }

    public enum Reason : byte { None = 0, Contact = 1, Winch = 2, Both = 3 }

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

    public static bool Hold(bool currentlyActive, double now, ref double lastActiveAt, double resumeSeconds)
    {
        if (currentlyActive) { lastActiveAt = now; return true; }
        if (lastActiveAt <= 0) return false;
        return now - lastActiveAt <= resumeSeconds;
    }

    public static double ResumeDelay(Cfg c, double rttMs, double interpDelayS)
        => InteractionLogic.ResumeDelay(new InteractionLogic.Cfg { ResumeSeconds = c.ResumeSeconds }, rttMs, interpDelayS);

    public static double PredictiveMargin(double baseMargin, double relSpeedMs, double lookAheadS, double capMeters = 3.0)
        => InteractionLogic.PredictiveMargin(baseMargin, relSpeedMs, lookAheadS, capMeters);
}

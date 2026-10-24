using System;

namespace OthLan;

/// <summary>
/// 0.2.0 T4.1: Owner vs HostShared for the client's own car (evolution of SuspendLogic).
/// Single owner of enter/hold/exit; detection stays in OwnCar / CarSync.
/// </summary>
public static class InteractionLogic
{
    public enum Authority : byte { Owner = 0, Host = 1 }
    public enum Mode : byte { Owner = 0, HostShared = 1 }

    public sealed class Cfg
    {
        public Authority ContactAuthority = Authority.Owner;
        public Authority WorldWinchAuthority = Authority.Owner;
        public double ResumeSeconds = 1.5;
        public double MinDwellSeconds = 2.0;
    }

    /// <summary>True when the session should be in HostShared right now (no hysteresis).</summary>
    public static bool WantShared(
        bool contactLocal, bool contactHost,
        WinchKindLogic.Kind winchLocal, WinchKindLogic.Kind winchHost,
        Cfg c)
    {
        if (winchLocal == WinchKindLogic.Kind.Car || winchHost == WinchKindLogic.Kind.Car)
            return true;
        if (c.WorldWinchAuthority == Authority.Host
            && (winchLocal == WinchKindLogic.Kind.World || winchHost == WinchKindLogic.Kind.World))
            return true;
        if (c.ContactAuthority == Authority.Host && (contactLocal || contactHost))
            return true;
        return false;
    }

    /// <summary>
    /// Advance mode: enter HostShared immediately when want; leave only after clear for ResumeSeconds
    /// and at least MinDwellSeconds spent in HostShared.
    /// </summary>
    public static Mode Step(
        Mode current, double now,
        ref double enteredSharedAt, ref double lastWantAt,
        bool want, Cfg c)
    {
        if (want)
        {
            lastWantAt = now;
            if (current != Mode.HostShared)
                enteredSharedAt = now;
            return Mode.HostShared;
        }
        if (current != Mode.HostShared)
            return Mode.Owner;
        if (enteredSharedAt > 0 && now - enteredSharedAt < c.MinDwellSeconds)
            return Mode.HostShared;
        if (lastWantAt > 0 && now - lastWantAt <= c.ResumeSeconds)
            return Mode.HostShared;
        return Mode.Owner;
    }

    /// <summary>Resume delay stretched by RTT + interp (same formula SuspendLogic used).</summary>
    public static double ResumeDelay(Cfg c, double rttMs, double interpDelayS)
    {
        var net = (double.IsNaN(rttMs) ? 200 : Math.Max(0, rttMs)) / 1000.0 + Math.Max(0, interpDelayS);
        return Math.Max(c.ResumeSeconds, 1.0 + net);
    }

    /// <summary>Predictive contact margin on the client.</summary>
    public static double PredictiveMargin(double baseMargin, double relSpeedMs, double lookAheadS, double capMeters = 3.0)
        => Math.Min(capMeters, Math.Max(0, baseMargin) + Math.Max(0, relSpeedMs) * Math.Max(0, lookAheadS));
}

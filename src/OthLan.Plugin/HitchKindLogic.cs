namespace OthLan;

/// <summary>
/// 0.2.6: classify an own-car hitch sample so NetStats / offline log review share one rule.
/// SimJump = discontinuity in Fusion render-clock motion; FrameSkip = wall-clock hitch with little sim motion.
/// </summary>
public static class HitchKindLogic
{
    public enum Kind : byte { None = 0, SimJump = 1, FrameSkip = 2, Mixed = 3 }

    /// <summary>
    /// <paramref name="hitchM"/> / <paramref name="simClockM"/> are metres (same units as Smoothness.Add).
    /// SimJump when sim clock covers at least half the hitch; FrameSkip when sim is zero or under a quarter.
    /// </summary>
    public static Kind Classify(double hitchM, double simClockM)
    {
        if (hitchM <= 0) return Kind.None;
        if (simClockM < 0) simClockM = 0;
        if (simClockM * 2 >= hitchM) return Kind.SimJump;
        if (simClockM <= 0 || simClockM * 4 <= hitchM) return Kind.FrameSkip;
        return Kind.Mixed;
    }

    public static string Tag(Kind k) => k switch
    {
        Kind.SimJump => "sim",
        Kind.FrameSkip => "frame",
        Kind.Mixed => "mix",
        _ => "none",
    };

    /// <summary>True when a prediction-reset restore (or cheap skip) likely caused this hitch.</summary>
    public static bool NearReset(double sinceResetS, double windowS = 0.08)
        => sinceResetS >= 0 && sinceResetS <= windowS;
}

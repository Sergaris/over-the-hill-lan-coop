namespace OthLan;

/// <summary>
/// 0.2.3 T3.6 / 0.2.6.2: brake / reverse thresholds and headlight mode ↔ OTC3 flags (Unity-free).
/// Headlight modes match the game's HeadlightsMode on build 26326013+: Off / Low / High.
/// Wire: FlagHeadlights = any on (Low|High) for old peers; FlagHeadlightsHigh = High beam (bit 6).
/// </summary>
public static class VehicleLightLogic
{
    public const float BrakeInputMin = 0.1f;
    public const float ReverseSpeedMs = -0.8f;
    public const float ReverseThrottleMax = 0.05f;

    /// <summary>Game HeadlightsMode (VehicleInput / LightsController.CurrentHeadlightsMode).</summary>
    public enum HeadlightMode : byte { Off = 0, Low = 1, High = 2 }

    public static bool WantBrake(float brakeInput) => brakeInput > BrakeInputMin;

    /// <summary>True when the car is rolling backward with little/no throttle (same rule as pre-stream Fusion lights).</summary>
    public static bool WantReverse(float forwardMs, float throttle)
        => forwardMs < ReverseSpeedMs && throttle < ReverseThrottleMax;

    /// <summary>OR headlight bits into an existing flags byte (preserves other bits).</summary>
    public static byte WithHeadlightMode(byte flags, HeadlightMode mode)
    {
        flags = (byte)(flags & ~(CarWire.FlagHeadlights | CarWire.FlagHeadlightsHigh));
        if (mode == HeadlightMode.Off) return flags;
        flags |= CarWire.FlagHeadlights;
        if (mode == HeadlightMode.High) flags |= CarWire.FlagHeadlightsHigh;
        return flags;
    }

    /// <summary>Decode mode from OTC3 flags. Old peers only set FlagHeadlights → Low.</summary>
    public static HeadlightMode HeadlightModeFromFlags(byte flags)
    {
        if ((flags & CarWire.FlagHeadlights) == 0) return HeadlightMode.Off;
        if ((flags & CarWire.FlagHeadlightsHigh) != 0) return HeadlightMode.High;
        return HeadlightMode.Low;
    }

    public static bool IsOn(HeadlightMode mode) => mode != HeadlightMode.Off;

    /// <summary>Parse self-test / CLI token: off|on|low|high (on = Low).</summary>
    public static bool TryParseMode(string s, out HeadlightMode mode)
    {
        mode = HeadlightMode.Off;
        if (string.IsNullOrEmpty(s)) return false;
        switch (s.Trim().ToLowerInvariant())
        {
            case "off": case "0": mode = HeadlightMode.Off; return true;
            case "on": case "low": case "1": mode = HeadlightMode.Low; return true;
            case "high": case "2": mode = HeadlightMode.High; return true;
            default: return false;
        }
    }
}

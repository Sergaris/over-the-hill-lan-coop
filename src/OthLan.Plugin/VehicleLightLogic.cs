namespace OthLan;

/// <summary>
/// 0.2.3 T3.6 / 0.2.6.2: brake / reverse thresholds and headlight mode ↔ OTC3 flags (Unity-free).
/// Headlight modes match the game's HeadlightsMode on build 26326013+: Off / Low / High.
/// Wire: FlagHeadlights = any on (Low|High) for old peers; FlagHeadlightsHigh = High beam (bit 6).
/// </summary>
public static class VehicleLightLogic
{
    /// <summary>Legacy single threshold (tests / Fusion path without latch).</summary>
    public const float BrakeInputMin = 0.1f;
    /// <summary>0.2.8: real pedal press to arm FlagBrake (micro-brake pulses sit below this).</summary>
    public const float BrakeOnMin = 0.35f;
    public const float BrakeOffMax = 0.12f;
    /// <summary>Must stay above OnMin this long before FlagBrake arms (ignores 1–2 frame spikes).</summary>
    public const double BrakeEnterSeconds = 0.08;
    /// <summary>Keep FlagBrake / lamps this long after input drops (sender + receiver).</summary>
    public const double BrakeHoldSeconds = 0.35;
    public const float ReverseSpeedMs = -0.8f;
    public const float ReverseThrottleMax = 0.05f;

    /// <summary>Game HeadlightsMode (VehicleInput / LightsController.CurrentHeadlightsMode).</summary>
    public enum HeadlightMode : byte { Off = 0, Low = 1, High = 2 }

    public static bool WantBrake(float brakeInput) => brakeInput > BrakeInputMin;

    /// <summary>Level hysteresis only (no time). Prefer <see cref="WantBrakeLatched"/> on the OTC3 sender.</summary>
    public static bool WantBrake(float brakeInput, bool wasOn)
        => wasOn ? brakeInput > BrakeOffMax : brakeInput > BrakeOnMin;

    /// <summary>
    /// Sender latch: enter only after OnMin for EnterSeconds; leave below OffMax only after HoldSeconds.
    /// Stops rear-lamp flicker from sub-threshold / single-frame Brake chatter in CurrentFrameInput.
    /// </summary>
    public static bool WantBrakeLatched(float brakeInput, bool wasOn, double now,
        ref double aboveSince, ref double litUntil)
    {
        if (brakeInput > BrakeOnMin)
        {
            if (aboveSince <= 0) aboveSince = now;
            if (wasOn || now - aboveSince >= BrakeEnterSeconds)
            {
                litUntil = now + BrakeHoldSeconds;
                return true;
            }
            return false;
        }
        aboveSince = 0;
        if (wasOn && brakeInput > BrakeOffMax)
        {
            litUntil = now + BrakeHoldSeconds;
            return true;
        }
        return litUntil > 0 && now < litUntil;
    }

    /// <summary>True when the car is rolling backward with little/no throttle (same rule as pre-stream Fusion lights).</summary>
    public static bool WantReverse(float forwardMs, float throttle)
        => forwardMs < ReverseSpeedMs && throttle < ReverseThrottleMax;

    /// <summary>Keep a lamp on while raw is true, and for <paramref name="holdS"/> after it goes false.</summary>
    public static bool HoldSignal(bool rawOn, double now, double holdS, ref double litUntil)
    {
        if (rawOn)
        {
            litUntil = now + (holdS > 0 ? holdS : 0);
            return true;
        }
        return litUntil > 0 && now < litUntil;
    }

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

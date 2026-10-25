namespace OthLan;

/// <summary>
/// 0.2.3 T3.6: brake / reverse light thresholds (owner of FlagBrake / FlagReverse and Fusion fallback).
/// </summary>
public static class VehicleLightLogic
{
    public const float BrakeInputMin = 0.1f;
    public const float ReverseSpeedMs = -0.8f;
    public const float ReverseThrottleMax = 0.05f;

    public static bool WantBrake(float brakeInput) => brakeInput > BrakeInputMin;

    /// <summary>True when the car is rolling backward with little/no throttle (same rule as pre-stream Fusion lights).</summary>
    public static bool WantReverse(float forwardMs, float throttle)
        => forwardMs < ReverseSpeedMs && throttle < ReverseThrottleMax;
}

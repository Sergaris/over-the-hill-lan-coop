using System;

namespace OthLan;

/// <summary>
/// When the host parks (freezes) a driverless car and when it wakes it again. Pure logic (unit-tested);
/// the Unity side lives in <c>ParkedCars</c>.
/// </summary>
public static class ParkLogic
{
    public enum Act : byte { Keep, Park, Wake }

    /// <summary>Below these (m/s, rad/s) a car counts as standing still.</summary>
    public const float StillSpeed = 0.05f, StillAngular = 0.05f;
    /// <summary>A car must stand still this long before it is parked.</summary>
    public const float ParkAfterS = 3f;
    /// <summary>Wake distance = longest winch reach of a player car + this, never below <see cref="MinWakeM"/>.</summary>
    public const float ReachMarginM = 15f, MinWakeM = 40f;
    /// <summary>Parking needs every player car this much farther than the wake distance (no park/wake flapping).</summary>
    public const float HysteresisM = 10f;

    public static bool IsStill(float speed, float angular) => speed < StillSpeed && angular < StillAngular;

    public static float WakeDistance(float longestReachM) =>
        float.IsNaN(longestReachM) ? MinWakeM : Math.Max(MinWakeM, longestReachM + ReachMarginM);

    /// <param name="stillForS">How long the car has been standing still (0 while it moves).</param>
    /// <param name="nearestPlayerM">Distance to the nearest player car (+inf without player cars).</param>
    public static Act Decide(bool parked, float stillForS, float nearestPlayerM, float wakeM)
    {
        if (float.IsNaN(nearestPlayerM)) nearestPlayerM = 0;
        if (parked) return nearestPlayerM < wakeM ? Act.Wake : Act.Keep;
        return stillForS >= ParkAfterS && nearestPlayerM >= wakeM + HysteresisM ? Act.Park : Act.Keep;
    }
}

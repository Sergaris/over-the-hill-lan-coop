namespace OthLan;

/// <summary>
/// 0.2.0 T1.4: classify an attached winch as world anchor vs another player car.
/// Pure logic (unit-tested). Detection of fields lives in <c>CarSync</c>.
/// </summary>
public static class WinchKindLogic
{
    public enum Kind : byte { None = 0, World = 1, Car = 2 }

    /// <param name="ownRopeAttached">This car's winch rope is attached.</param>
    /// <param name="ownRopeToPlayer">Own rope's WinchedCarPlayerRef is a real player.</param>
    /// <param name="towedByOther">Another car's winch lists this car as WinchedCarPlayerRef.</param>
    public static Kind Classify(bool ownRopeAttached, bool ownRopeToPlayer, bool towedByOther)
    {
        if (towedByOther) return Kind.Car;
        if (!ownRopeAttached) return Kind.None;
        return ownRopeToPlayer ? Kind.Car : Kind.World;
    }

    public static string LogTag(Kind k, int otherSlot = -1) => k switch
    {
        Kind.World => "world",
        Kind.Car => otherSlot >= 0 ? $"car:{otherSlot}" : "car",
        _ => "none",
    };
}

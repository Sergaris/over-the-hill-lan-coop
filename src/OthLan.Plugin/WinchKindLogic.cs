namespace OthLan;

/// <summary>
/// 0.2.0 T1.4: classify an attached winch as world anchor vs another player car.
/// Pure logic (unit-tested). Detection of fields lives in <c>HostFollower</c>.
/// 0.2.8: short hold of the last non-None kind so raw frame flicker does not chatter HostShared / flags.
/// </summary>
public static class WinchKindLogic
{
    public enum Kind : byte { None = 0, World = 1, Car = 2 }

    /// <summary>Default hold of a non-None reading (shorter than SharedMinDwell so authority exit is not doubled).</summary>
    public const double HoldSeconds = 0.35;

    /// <summary>Per-car hold of the last non-None classify result.</summary>
    public struct HoldState
    {
        public Kind Kind;
        public int OtherSlot;
        public double Until;
    }

    /// <param name="ownRopeAttached">This car's winch rope is attached.</param>
    /// <param name="ownRopeToPlayer">Own rope's WinchedCarPlayerRef is a real player.</param>
    /// <param name="towedByOther">Another car's winch lists this car as WinchedCarPlayerRef.</param>
    public static Kind Classify(bool ownRopeAttached, bool ownRopeToPlayer, bool towedByOther)
    {
        if (towedByOther) return Kind.Car;
        if (!ownRopeAttached) return Kind.None;
        return ownRopeToPlayer ? Kind.Car : Kind.World;
    }

    /// <summary>Keep the last non-None kind for <paramref name="holdS"/> after the sensor goes None.</summary>
    public static Kind Hold(Kind raw, int rawOtherSlot, double now, double holdS, ref HoldState st, out int otherSlot)
    {
        if (raw != Kind.None)
        {
            st.Kind = raw;
            st.OtherSlot = rawOtherSlot;
            st.Until = now + (holdS > 0 ? holdS : 0);
            otherSlot = rawOtherSlot;
            return raw;
        }
        if (st.Kind != Kind.None && now < st.Until)
        {
            otherSlot = st.OtherSlot;
            return st.Kind;
        }
        st = default;
        otherSlot = -1;
        return Kind.None;
    }

    public static string LogTag(Kind k, int otherSlot = -1) => k switch
    {
        Kind.World => "world",
        Kind.Car => otherSlot >= 0 ? $"car:{otherSlot}" : "car",
        _ => "none",
    };
}

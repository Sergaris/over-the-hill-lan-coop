using UnityEngine;

namespace OthLan;

/// <summary>
/// 0.2.0: single monotonic clock for new netcode (plan NetClock). Proxies for legacy call sites stay on their own Now.
/// </summary>
internal static class NetClock
{
    public static double Now => Time.realtimeSinceStartupAsDouble;
}

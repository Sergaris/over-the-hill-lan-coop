using System.Diagnostics;

namespace OthLan;

/// <summary>
/// 0.2.0 T5.4: single process clock for netcode (sendTime, arrival, own-car).
/// Stopwatch so network threads can read it (Unity Time is main-thread only).
/// </summary>
internal static class NetClock
{
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    public static double Now => Clock.Elapsed.TotalSeconds;
}

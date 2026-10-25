using System.Collections.Generic;

namespace OthLan;

/// <summary>
/// 0.2.0 T4.4: process-wide Sync ring. CarStreamSender / CarRelay note raw OTC3 samples
/// (network thread); NetStats dumps from the main thread — all access under <see cref="_lock"/>.
/// </summary>
internal static class SyncProbe
{
    static readonly object _lock = new();
    static readonly SyncProbeLogic.Ring Samples = new();

    public static void Reset()
    {
        lock (_lock) Samples.Clear();
    }

    public static void Note(int slot, uint seq, float x, float y, float z)
    {
        if (!SyncProbeLogic.Probed(seq)) return;
        lock (_lock) Samples.Note(slot, seq, x, y, z);
    }

    public static List<SyncProbeLogic.Sample> Drain()
    {
        lock (_lock) return Samples.Drain();
    }
}

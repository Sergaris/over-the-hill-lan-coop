namespace OthLan;

/// <summary>
/// 0.2.8 / 0.2.8.1: which pose the client Late path draws for a remote car.
/// PrePhysics writes one jitter-delayed Hermite sample into Phys*; Late reuses it when the stream is fresh
/// (no second SampleAt at a different play-time on the same root — that fought accessories in 0.2.7).
/// </summary>
public static class RemotePoseLogic
{
    public enum Source : byte { Fusion = 0, Phys = 1, StreamSample = 2 }

    /// <param name="streamFresh">OTC3 arrival younger than FreshAgeS and buffer non-empty.</param>
    /// <param name="hasPhysThisFrame">PhysP was written on this Unity frame (not a leftover from an earlier frame).</param>
    public static Source Pick(bool streamFresh, bool hasPhysThisFrame)
    {
        if (!streamFresh) return Source.Fusion;
        return hasPhysThisFrame ? Source.Phys : Source.StreamSample;
    }
}

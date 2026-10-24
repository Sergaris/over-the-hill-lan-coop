using System;

namespace OthLan;

/// <summary>
/// 0.2.0 T0.4: single owner of frame/stage sample ring + percentiles.
/// Used by <c>NetStats</c> (frameMs) and <c>FrameCost</c> (per-stage cost). Pure logic (unit-tested).
/// </summary>
public static class FrameCostLogic
{
    public const double StageHotMs = 8.0;
    /// <summary>
    /// One 30 s NetStats window without wrapping. OwnLocal can push up to 2 LocalStep samples per frame
    /// (Debt loop capped at 2), so 30 s × 60 Hz × 2 = 3600; +headroom for hitch / higher refresh.
    /// </summary>
    public const int WindowCap = 4096;
    public const double MaxPlausibleFrameMs = 500.0;
    /// <summary>Resim / Physics.Simulate spikes can exceed 500 ms; still count them in cost max and &gt;8ms.</summary>
    public const double MaxPlausibleStageMs = 5000.0;

    /// <summary>Push one sample if plausible. Returns false when rejected (NaN / negative / above max).</summary>
    public static bool TryPush(double[] buf, ref int n, ref int i, double ms, double maxPlausibleMs)
    {
        if (buf == null || buf.Length == 0) return false;
        if (double.IsNaN(ms) || ms < 0 || ms > maxPlausibleMs) return false;
        buf[i % buf.Length] = ms;
        i++;
        if (n < buf.Length) n++;
        return true;
    }

    /// <summary>p50 / p99 / max over the first <paramref name="n"/> ring entries (any order in the buffer).</summary>
    public static bool TryStats(double[] buf, int n, out double p50, out double p99, out double max)
    {
        p50 = p99 = max = 0;
        if (buf == null || n <= 0) return false;
        var count = Math.Min(n, buf.Length);
        var tmp = new double[count];
        // Ring may have wrapped: take the newest `count` samples ending at i-1.
        // Callers that clear on each window never wrap with WindowCap≥1800; still correct if they did not clear.
        Array.Copy(buf, 0, tmp, 0, count);
        Array.Sort(tmp);
        p50 = Percentile(tmp, 0.50);
        p99 = Percentile(tmp, 0.99);
        max = tmp[count - 1];
        return true;
    }

    public static double Percentile(double[] sortedAsc, double q)
    {
        if (sortedAsc == null || sortedAsc.Length == 0) return 0;
        var i = (int)(q * (sortedAsc.Length - 1));
        if (i < 0) i = 0;
        if (i >= sortedAsc.Length) i = sortedAsc.Length - 1;
        return sortedAsc[i];
    }

    public static string Format(string name, double[] buf, int n, int overHot, double hotMs)
    {
        if (!TryStats(buf, n, out var p50, out var p99, out var max))
            return $"{name} p50=- p99=- max=- >{hotMs:0}ms={overHot}";
        return $"{name} p50={p50:0.00} p99={p99:0.00} max={max:0.00} >{hotMs:0}ms={overHot}";
    }

    public static void Clear(double[] buf, ref int n, ref int i)
    {
        n = i = 0;
        if (buf != null) Array.Clear(buf, 0, buf.Length);
    }
}

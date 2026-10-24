using System;
using System.Diagnostics;

namespace OthLan;

/// <summary>
/// 0.2.0 T0.4: Stopwatch accumulators for resim / localStep / remoteView / wheels / hostFollow.
/// Sample window is cleared every 30 s log so p50/p99/max cover that window (owner of math: <see cref="FrameCostLogic"/>).
/// </summary>
internal static class FrameCost
{
    public static readonly Stage Resim = new("resim");
    public static readonly Stage LocalStep = new("localStep");
    public static readonly Stage RemoteView = new("remoteView");
    public static readonly Stage Wheels = new("wheels");
    public static readonly Stage HostFollow = new("hostFollow");

    static readonly Stage[] All = { Resim, LocalStep, RemoteView, Wheels, HostFollow };

    public sealed class Stage
    {
        public readonly string Name;
        readonly double[] _buf = new double[FrameCostLogic.WindowCap];
        int _n, _i, _overHot;

        public Stage(string name) { Name = name; }

        public void AddMs(double ms)
        {
            if (FrameCostLogic.TryPush(_buf, ref _n, ref _i, ms, FrameCostLogic.MaxPlausibleStageMs)
                && ms > FrameCostLogic.StageHotMs)
                _overHot++;
        }

        public string Describe() => FrameCostLogic.Format(Name, _buf, _n, _overHot, FrameCostLogic.StageHotMs);

        /// <summary>End of a 30 s NetStats window: drop samples so the next line is that window only.</summary>
        public void ResetWindow()
        {
            FrameCostLogic.Clear(_buf, ref _n, ref _i);
            _overHot = 0;
        }

        public void ResetAll() => ResetWindow();
    }

    public readonly struct Scope : IDisposable
    {
        readonly Stage _s;
        readonly long _t0;
        public Scope(Stage s) { _s = s; _t0 = Stopwatch.GetTimestamp(); }
        public void Dispose()
        {
            var ms = (Stopwatch.GetTimestamp() - _t0) * 1000.0 / Stopwatch.Frequency;
            _s.AddMs(ms);
        }
    }

    public static Scope Measure(Stage s) => new(s);

    public static string Describe()
        => $"cost {Resim.Describe()} | {LocalStep.Describe()} | {RemoteView.Describe()} | {Wheels.Describe()} | {HostFollow.Describe()}";

    public static void ResetWindow()
    {
        foreach (var s in All) s.ResetWindow();
    }

    public static void ResetAll()
    {
        foreach (var s in All) s.ResetAll();
    }
}

using System;
using System.Runtime.InteropServices;

namespace OthLan;

/// <summary>
/// 0.2.7 empty resimulation (issue #1: resim cost 6-13 ms p50 per snapshot at 180 ms ping). When phase 2 is going to
/// put our own car state back after the loop anyway (<see cref="OwnCarLogic.CanRunEmptyResim"/>), Fusion's
/// RunClientSideResimulationLoop still runs (ticks, buffers, other behaviours stay consistent) but the scene is not
/// Physics.Simulate'd and our car's Vehicle Physics Pro step (VehicleBase.FixedUpdateNetwork) is skipped.
/// The 0.2.3 SimulateForward failure (spawn bounce, dead steering) came from VPP stepping without physics and that state
/// being kept; here VPP does not step and the full own state is restored, and only for loops phase 2 discards.
/// </summary>
internal static class OwnResim
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void InstFn(IntPtr self, IntPtr mi);
    const string K_Fun = "VehiclePhysics.VehicleBase::FixedUpdateNetwork()";
    static InstFn _orig, _hook;
    static IntPtr _muted;
    public static bool HookOk;
    public static int Loops, MutedSteps, Refused;
    public static double MaxHostErr;

    public static bool Enabled => Plugin.OwnResimSkip.Value;

    public static void Install()
    {
        if (HookOk || !Enabled) return;
        var p = OptionalHooks.Resolve(typeof(VehiclePhysics.VehicleBase), "NativeMethodInfoPtr_FixedUpdateNetwork_Public_Virtual_Void_0", K_Fun);
        _hook = Detour;
        HookOk = OptionalHooks.Apply(p, _hook, out _orig, K_Fun);
        Plugin.L.LogInfo($"[OwnResim] empty own-car resimulation {(HookOk ? "installed" : "unavailable (normal resimulation)")}");
    }

    /// <summary>Mute <paramref name="ownVb"/> and scene physics for the loop that follows. False = run it normally.
    /// True must be paired with <see cref="End"/> (finally).</summary>
    public static bool Begin(IntPtr ownVb, double hostErr)
    {
        if (!HookOk || ownVb == IntPtr.Zero || _muted != IntPtr.Zero) return false;
        if (!ClientPhysics.BeginResimWithoutPhysics()) { Refused++; return false; }
        _muted = ownVb;
        Loops++;
        if (hostErr > MaxHostErr) MaxHostErr = hostErr;
        return true;
    }

    public static void End()
    {
        if (_muted == IntPtr.Zero) return;
        _muted = IntPtr.Zero;
        ClientPhysics.EndResimWithoutPhysics();
    }

    static void Detour(IntPtr self, IntPtr mi)
    {
        if (self != IntPtr.Zero && self == _muted) { MutedSteps++; return; }
        _orig(self, mi);
    }

    public static string Describe() =>
        $"emptyResim={(Enabled ? HookOk ? "on" : "unavailable" : "off")} loops={Loops} mutedSteps={MutedSteps} refused={Refused} maxHostErr={MaxHostErr * 100:0}cm";
}

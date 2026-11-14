using System;
using System.Runtime.InteropServices;

namespace OthLan;

/// <summary>
/// 0.2.8: skip <see cref="VehicleWinchSystem.FixedUpdateNetwork"/> during the client's Fusion resimulation loop.
/// Friend dump 0.2.7: five identical NullReferenceException stacks in that method inside OnTick/resim while winch
/// chatter put the car in HostShared (empty OwnResim is off then). Forward ticks still run the game method.
/// Kept as skip (not try/catch-around-orig): HostShared keeps the resim result, so a mid-tick NRE would leave the
/// rope/pose half-updated. A field-level null guard needs the exact null inside FixedUpdateNetwork (still unknown).
/// </summary>
internal static class WinchResim
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void InstFn(IntPtr self, IntPtr mi);
    const string K_Fun = "VehicleWinchSystem::FixedUpdateNetwork()";
    static InstFn _orig, _hook;
    public static bool HookOk;
    public static int Skipped, Forward;

    public static void Install()
    {
        if (HookOk) return;
        var p = OptionalHooks.Resolve(typeof(VehicleWinchSystem), "NativeMethodInfoPtr_FixedUpdateNetwork_Public_Virtual_Void_0", K_Fun);
        _hook = Detour;
        HookOk = OptionalHooks.Apply(p, _hook, out _orig, K_Fun);
        Plugin.L.LogInfo($"[WinchResim] skip winch FixedUpdateNetwork during client resim {(HookOk ? "installed" : "unavailable")}");
    }

    static void Detour(IntPtr self, IntPtr mi)
    {
        if (OwnCar.InClientResim) { Skipped++; return; }
        Forward++;
        _orig(self, mi);
    }

    public static string Describe() =>
        $"winchResim={(HookOk ? "on" : "off")} skipped={Skipped} forward={Forward}";
}

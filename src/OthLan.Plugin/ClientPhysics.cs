using System;
using Fusion.Addons.Physics;

namespace OthLan;

/// <summary>
/// 0.2.0 T2.1: set Fusion client physics to SimulateForward so resim ticks do not Physics.Simulate the whole scene.
/// </summary>
internal static class ClientPhysics
{
    static IntPtr _appliedRunner;

    public static void Reset() => _appliedRunner = IntPtr.Zero;

    /// <summary>Main thread: apply once per runner while we are a pure client.</summary>
    public static void Tick()
    {
        if (LanState.Active != LanRole.Client || LanState.LanRunner == IntPtr.Zero)
        {
            if (_appliedRunner != IntPtr.Zero) Reset();
            return;
        }
        try
        {
            var onr = OverlandNetworkRunner.Instance;
            var r = onr?.NetworkRunner;
            if (r == null || r.Pointer != LanState.LanRunner || !r.IsRunning || !r.IsClient || r.IsServer) return;
            if (r.Pointer == _appliedRunner) return;
            var phys = onr.RunnerSimulatePhysics3D;
            if (phys == null) { _appliedRunner = r.Pointer; return; }

            var mode = (Plugin.ClientPhysicsMode?.Value ?? "SimulateForward").Trim();
            if (string.Equals(mode, "Game", StringComparison.OrdinalIgnoreCase))
            {
                _appliedRunner = r.Pointer;
                Plugin.L.LogInfo($"[Netcode] client physics: left at game default ({phys.ClientPhysicsSimulation})");
                return;
            }

            var was = phys.ClientPhysicsSimulation;
            phys.ClientPhysicsSimulation = ClientPhysicsSimulation.SimulateForward;
            _appliedRunner = r.Pointer;
            Plugin.L.LogInfo($"[Netcode] client physics: {was} -> {phys.ClientPhysicsSimulation}");
        }
        catch (Exception e)
        {
            Plugin.L.LogWarning($"[Netcode] client physics: {e.Message}");
            _appliedRunner = LanState.LanRunner; // avoid spam
        }
    }
}

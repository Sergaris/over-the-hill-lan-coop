using System;
using Fusion.Addons.Physics;

namespace OthLan;

/// <summary>
/// 0.2.0 T2.1: opt-in ClientPhysicsMode=SimulateForward (resim ticks do not Physics.Simulate the scene).
/// Any other value leaves the game default: with SimulateForward the prediction reset teleports the own car
/// without stepping contacts/suspension, so it bounces and ignores steering (0.2.3 steer A/B).
/// </summary>
internal static class ClientPhysics
{
    static IntPtr _appliedRunner;

    public static void Reset() => _appliedRunner = IntPtr.Zero;

    /// <summary>Main thread: apply once per runner while we are a pure client.</summary>
    public static void Tick()
    {
        if (LanState.Active != LanRole.Client || !LanState.InSession)
        {
            if (_appliedRunner != IntPtr.Zero) Reset();
            return;
        }
        try
        {
            var r = LanState.Runner();
            if (r == null || !r.IsClient || r.IsServer) return;
            var onr = OverlandNetworkRunner.Instance;
            if (r.Pointer == _appliedRunner) return;
            var phys = onr.RunnerSimulatePhysics3D;
            if (phys == null) { _appliedRunner = r.Pointer; return; }

            var mode = Plugin.ClientPhysicsMode?.Value?.Trim();
            if (!string.Equals(mode, "SimulateForward", StringComparison.OrdinalIgnoreCase))
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

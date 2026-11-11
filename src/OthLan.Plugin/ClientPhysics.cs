using System;
using Fusion.Addons.Physics;

namespace OthLan;

/// <summary>
/// Single owner of client scene-physics step policy (Fusion RunnerSimulatePhysics3D.ClientPhysicsSimulation).
/// 0.2.0 T2.1: opt-in ClientPhysicsMode=SimulateForward (resim ticks do not Physics.Simulate the scene).
/// Default stays the game value (SimulateAlways): after a prediction-reset teleport the own car must still get a
/// contact/suspension step — SimulateForward as default caused bounce / dead steering (v9 / 0.2.3 steer A/B).
/// 0.2.6 hitch fix is not here: cheap-skip Previous restore lives in OwnCar (offline: dominant sim-jump after reset).
/// Do not reintroduce SimulateForward as the release default.
/// 0.2.7: <see cref="BeginResimWithoutPhysics"/> drops scene physics for one resimulation loop only (OwnResim).
/// </summary>
internal static class ClientPhysics
{
    static IntPtr _appliedRunner;
    static RunnerSimulatePhysics3D _phys;
    static ClientPhysicsSimulation _beforeResim;
    static bool _resimForced;

    public static void Reset() { _appliedRunner = IntPtr.Zero; _phys = null; _resimForced = false; }

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
            _phys = phys;
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

    /// <summary>The scene is not stepped in the resimulation loop that follows (forward ticks keep the configured
    /// mode). False = physics runner unknown, run the normal resimulation. Always pair with <see cref="EndResimWithoutPhysics"/>.</summary>
    public static bool BeginResimWithoutPhysics()
    {
        var phys = _phys;
        if (phys == null || _resimForced) return false;
        _beforeResim = phys.ClientPhysicsSimulation;
        if (_beforeResim != ClientPhysicsSimulation.SimulateForward)
            phys.ClientPhysicsSimulation = ClientPhysicsSimulation.SimulateForward;
        _resimForced = true;
        return true;
    }

    public static void EndResimWithoutPhysics()
    {
        if (!_resimForced) return;
        _resimForced = false;
        if (_phys != null && _beforeResim != ClientPhysicsSimulation.SimulateForward) _phys.ClientPhysicsSimulation = _beforeResim;
    }
}

using System;
using System.Linq;
using Fusion;
using UnityEngine;

namespace OthLan;

/// <summary>Logs, once per LAN session, the runtime network/physics facts that the IL2CPP dump cannot show
/// (they live in serialized assets): tick rate, input transfer mode, client physics mode, car components and
/// the Overland prediction-correction smoothing values.</summary>
internal static class Diagnostics
{
    static IntPtr _loggedRunner, _loggedCarsRunner;
    static float _next;

    public static void Tick()
    {
        if (LanState.Active == LanRole.Off || LanState.LanRunner == IntPtr.Zero) return;
        var now = Time.realtimeSinceStartup; if (now < _next) return; _next = now + 3f;
        var onr = OverlandNetworkRunner.Instance; var r = onr?.NetworkRunner;
        if (r == null || r.Pointer != LanState.LanRunner || !r.IsRunning) return;
        try
        {
            if (_loggedRunner != r.Pointer)
            {
                _loggedRunner = r.Pointer;
                var sim = r.Config?.Simulation;
                Plugin.L.LogInfo($"[Diag] runner: TickRate={r.TickRate} DeltaTime={r.DeltaTime:0.0000}s IsServer={r.IsServer} IsClient={r.IsClient} " +
                                 $"LocalPlayer={r.LocalPlayer} Topology={sim?.Topology} InputTransferMode={sim?.InputTransferMode} " +
                                 $"PlayerCount={sim?.PlayerCount} Replication={sim?.ReplicationFeatures} TimeMode={sim?.SimulationUpdateTimeMode}");
                var phys = onr.RunnerSimulatePhysics3D;
                if (phys != null)
                    Plugin.L.LogInfo($"[Diag] RunnerSimulatePhysics3D: ClientPhysicsSimulation={phys.ClientPhysicsSimulation} PhysicsTiming={phys.PhysicsTiming} " +
                                     $"PhysicsAuthority={phys.PhysicsAuthority} DeltaTimeMultiplier={phys.DeltaTimeMultiplier} SetUnityFixedTimestep={phys.SetUnityFixedTimestep} " +
                                     $"Time.fixedDeltaTime={Time.fixedDeltaTime:0.0000}");
                else Plugin.L.LogInfo("[Diag] RunnerSimulatePhysics3D: null");
            }
            if (_loggedCarsRunner != r.Pointer)
            {
                var players = UnityEngine.Object.FindObjectsOfType<FusionPlayer>();
                if (players.Length == 0) return;
                _loggedCarsRunner = r.Pointer;
                foreach (var fp in players)
                {
                    var o = fp.Object;
                    var comps = fp.gameObject.GetComponents<Component>().Select(c => c == null ? "null" : c.GetIl2CppType().Name);
                    Plugin.L.LogInfo($"[Diag] car '{fp.gameObject.name}': InputAuthority={o?.InputAuthority} StateAuth={o?.HasStateAuthority} InputAuth={o?.HasInputAuthority} " +
                                     $"IsProxy={o?.IsProxy} components=[{string.Join(", ", comps)}]");
                    var nrb = fp.gameObject.GetComponent<Fusion.Addons.Physics.NetworkRigidbody3D>();
                    if (nrb != null)
                        Plugin.L.LogInfo($"[Diag]   NetworkRigidbody3D: CorrectionSmoothingTime={nrb.CorrectionSmoothingTime} SnapDistance={nrb.CorrectionSnapDistance} " +
                                         $"SnapAngle={nrb.CorrectionSnapAngle} UsePreciseRotation={nrb.UsePreciseRotation} SyncParent={nrb.SyncParent}");
                }
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[Diag] {e.Message}"); _loggedRunner = r.Pointer; _loggedCarsRunner = r.Pointer; }
    }
}

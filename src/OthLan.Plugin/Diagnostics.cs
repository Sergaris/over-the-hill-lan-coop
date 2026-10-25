using System;
using System.Linq;
using Fusion;
using UnityEngine;

namespace OthLan;

/// <summary>Logs, once per LAN session, the runtime network/physics facts that the IL2CPP dump cannot show
/// (they live in serialized assets): tick rate, input transfer mode, client physics mode, car components and
/// the Overland prediction-correction smoothing values. Zip export lives in <see cref="SessionLogs"/>.</summary>
internal static class Diagnostics
{
    static IntPtr _loggedRunner, _loggedCarsRunner;
    static float _next; static int _retries;
    public static string LastExportPath { get => SessionLogs.LastExportPath; set => SessionLogs.LastExportPath = value; }

    public static void Tick()
    {
        if (!LanState.InSession) return;
        var now = Time.realtimeSinceStartup; if (now < _next) return; _next = now + 3f;
        var r = LanState.Runner();
        if (r == null) return;
        var onr = OverlandNetworkRunner.Instance;
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
                                     $"Physics.simulationMode={Physics.simulationMode} Time.fixedDeltaTime={Time.fixedDeltaTime:0.0000} TickRate={r.TickRate}");
                else Plugin.L.LogInfo($"[Diag] RunnerSimulatePhysics3D: null Physics.simulationMode={Physics.simulationMode} Time.fixedDeltaTime={Time.fixedDeltaTime:0.0000} TickRate={r.TickRate}");
            }
            if (_loggedCarsRunner != r.Pointer)
            {
                CarRegistry.RefreshNow();
                var players = CarRegistry.Players;
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
        catch (Exception e) when (DiagLogic.ShouldRetryRuntimeConfig(e, _retries))
        {
            // 0.2.0 T0.3: IL2CPP may wrap RuntimeConfig-not-ready as a non-InvalidOperationException; retry up to 20× / 3 s.
            _retries++;
            _loggedRunner = IntPtr.Zero;
            Plugin.L.LogInfo($"[Diag] runner not ready yet ({e.GetType().Name}: {e.Message}); retry {_retries}/{DiagLogic.MaxRuntimeConfigRetries}");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[Diag] {e.Message}"); _loggedRunner = r.Pointer; _loggedCarsRunner = r.Pointer; }
    }

    /// <summary>0.2.0 T0.2: thin wrapper — ownership moved to <see cref="SessionLogs"/>.</summary>
    public static string ExportZip(string why) => SessionLogs.ExportZip(why);
}

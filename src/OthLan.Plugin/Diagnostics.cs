using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Fusion;
using UnityEngine;

namespace OthLan;

/// <summary>Logs, once per LAN session, the runtime network/physics facts that the IL2CPP dump cannot show
/// (they live in serialized assets): tick rate, input transfer mode, client physics mode, car components and
/// the Overland prediction-correction smoothing values. Also F7 "Export diagnostics" zip for live A/B with a friend.</summary>
internal static class Diagnostics
{
    static IntPtr _loggedRunner, _loggedCarsRunner;
    static float _next; static int _retries;
    public static string LastExportPath;

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
        catch (InvalidOperationException e) when (_retries++ < 5)
        {   // 0.1.8: read too early (Fusion's RuntimeConfig not set yet, seen in the 2026-10-22 log): try again in 3 s
            _loggedRunner = IntPtr.Zero; Plugin.L.LogInfo($"[Diag] runner not ready yet ({e.Message}); retrying");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[Diag] {e.Message}"); _loggedRunner = r.Pointer; _loggedCarsRunner = r.Pointer; }
    }

    /// <summary>0.1.9 U0: zip BepInEx + Unity logs, cfg and a NetStats snapshot to the Desktop (no personal Steam paths).</summary>
    public static string ExportZip(string why)
    {
        try
        {
            var desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desk) || !Directory.Exists(desk)) desk = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var path = Path.Combine(desk, $"OTH-LAN-diag-{stamp}.zip");
            using (var zs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(zs, ZipArchiveMode.Create))
            {
                void AddFile(string src, string entry)
                {
                    if (string.IsNullOrEmpty(src) || !File.Exists(src)) return;
                    try { zip.CreateEntryFromFile(src, entry, System.IO.Compression.CompressionLevel.Optimal); } catch { }
                }
                AddFile(Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput.log"), "LogOutput.log");
                try { AddFile(Plugin.CfgPath, "OthLan.cfg"); } catch { }
                try
                {
                    var pl = Path.Combine(Application.persistentDataPath, "Player.log");
                    AddFile(pl, "Player.log");
                }
                catch { }
                var snap = new StringBuilder();
                snap.AppendLine($"OTH-LAN {Plugin.Version} diag ({why}) {DateTime.Now:o}");
                snap.AppendLine($"role={LanState.Active} runner={(LanState.LanRunner != IntPtr.Zero)}");
                snap.AppendLine(NetStats.Describe());
                snap.AppendLine(NetStats.FrameDescribe());
                snap.AppendLine(RemoteSmooth.Describe());
                snap.AppendLine(RemoteVisuals.Describe());
                snap.AppendLine(OwnCar.Describe());
                snap.AppendLine(OwnRender.Describe());
                snap.AppendLine(OwnLocal.Describe());
                var e = zip.CreateEntry("netstats.txt");
                using (var w = new StreamWriter(e.Open())) w.Write(snap.ToString());
            }
            LastExportPath = path;
            Plugin.L.LogInfo($"[Diag] exported {path}");
            return path;
        }
        catch (Exception e)
        {
            Plugin.L.LogWarning($"[Diag] export failed: {e.Message}");
            LastExportPath = null;
            return null;
        }
    }
}

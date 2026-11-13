using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;

namespace OthLan;

[BepInPlugin(Guid, Name, Version)]
public partial class Plugin : BasePlugin
{
    public const string Guid = "dev.othlan.lan";
    public const string Name = "OTH-LAN";
    public const string Version = "0.2.8.1";

    internal static ManualLogSource L;

    public override void Load()
    {
        L = Log; _cfg = Config;
        // Several game instances on one PC (Local Co-op tests, 2-4 players on one machine) start at the same time and
        // share this cfg. BepInEx writes the file on every Bind that adds an entry and on every Value set; when two
        // processes did that at once, Bind threw IOException ("used by another process") and the plugin did not load
        // at all (0.1.8 self-test fc4, client2: vanilla game, no save protection). Write once at the end, with retries.
        Config.SaveOnConfigSet = false;
        BindConfig(Config);
        MigrateConfig(Config);
        SaveConfigShared();
        Config.SettingChanged += OnConfigChanged;
        try { LangWatch.Init(); } catch (Exception e) { L.LogWarning($"[Lang] init: {e.Message}"); }
        BuildGate.Run();     // tiers: supported / unverified (one-time warning) / incompatible; applies the detours if allowed
        // Autopilot must never abort the plugin: two loopback instances used to race on selftest/solo-metrics.csv and
        // throw IOException here → no Overlay / no Local Co-op on the second process.
        try { Autopilot.Init(); }
        catch (Exception e) { L.LogError($"[Autopilot] init failed (plugin continues without autopilot): {e.Message}"); }
        ClassInjector.RegisterTypeInIl2Cpp<Overlay>();
        AddComponent<Overlay>();
        try { Updater.Init(); } catch (Exception e) { L.LogWarning($"[Update] init: {e.Message}"); }
        L.LogInfo($"{Name} {Version} loaded. Press {ToggleOverlayKey.Value} for the LAN window. LocalIPs: {string.Join(", ", NetInfo.LocalIPv4())}");
    }
}

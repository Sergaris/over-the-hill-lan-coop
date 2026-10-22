using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;

namespace OthLan;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "dev.othlan.lan";
    public const string Name = "OTH-LAN";
    public const string Version = "0.1.7";

    internal static ManualLogSource L;
    internal static ConfigEntry<int> Port;
    internal static ConfigEntry<int> PlayerCount;
    internal static ConfigEntry<string> JoinAddress;
    internal static ConfigEntry<string> LastGameLanguage;
    internal static ConfigEntry<string> JoinArea;
    internal static ConfigEntry<string> ClientId;
    internal static ConfigEntry<string> AutoTest;
    internal static ConfigEntry<float> AutoTestDelay;
    internal static ConfigEntry<string> UnverifiedChoices;
    internal static ConfigEntry<BepInEx.Unity.IL2CPP.Configuration.KeyboardShortcut> ToggleOverlayKey;
    internal static ConfigEntry<bool> PingShow;
    internal static ConfigEntry<float> PingYellowAboveMs, PingRedAboveMs, PingSmoothing, PingUpdateHz, PingFallbackX, PingFallbackY;
    internal static ConfigEntry<bool> CarSyncEnabled;
    internal static ConfigEntry<int> ConfigVersion;
    internal static ConfigEntry<float> CarSendHz, CarHighPingMs, CarHysteresisMs, CarCheckpointSeconds, CarDesyncMeters, CarDesyncDegrees, CarBlendTau,
        CarCheckpointMinMeters, CarCheckpointMinDegrees, CarCheckpointSoftSeconds, CarContactHoldSeconds,
        CarHalfWidth, CarHalfLength, CarHalfHeight, CarContactMargin, PingShowLossFromPct;
    internal static ConfigEntry<bool> CarContactPause;
    internal static ConfigEntry<bool> OwnCarAuthority, ContactBoxFromColliders, MenuButton;
    internal static ConfigEntry<string> HostName, ManualHosts, RecentHosts;
    internal static ConfigEntry<bool> OutAutoRejoin;
    internal static ConfigEntry<float> OutExtrapolateFrom, OutExtrapolateSeconds, OutBrakeFrom, OutBrakeSeconds, OutSnapMeters, OutContactWait;
    internal static ConfigEntry<float> OwnSoftGapSeconds, OwnSoftSeconds, OwnSoftSmoothing, OwnSoftSnapMeters, OwnAcceptSeconds, OwnAcceptMeters, OwnAcceptDegrees, OwnPostOutageSeconds,
        OwnStuckMeters, OwnStuckSeconds, OwnFallbackSeconds;

    public override void Load()
    {
        L = Log;
        Port = Config.Bind("Network", "Port", 27015, "UDP port the LAN host listens on (open it in Windows Firewall; the installer does).");
        PlayerCount = Config.Bind("Network", "PlayerCount", 4,
            "Session size. MUST be identical on host and every client (Fusion sizes its simulation from it).");
        LastGameLanguage = Config.Bind("General", "LastGameLanguage", "", "Internal: the game language seen last time (the mod's texts follow the game's Settings → Language; this is only used until the game has loaded its own setting).");
        JoinAddress = Config.Bind("Network", "JoinAddress", "", "Host address for 'Join by IP' (Radmin VPN 26.x.x.x:port).");
        HostName = Config.Bind("LocalCoop", "HostName", "", "Lobby name others see in 'Local Co-op' (empty = your Steam name, else the PC name).");
        ManualHosts = Config.Bind("LocalCoop", "ManualHosts", "", "Hosts added with 'Add by IP' (ip:port, comma separated); always asked directly.");
        RecentHosts = Config.Bind("LocalCoop", "RecentHosts", "", "Hosts you joined before (filled automatically; asked directly).");
        MenuButton = Config.Bind("LocalCoop", "MenuButton", true, "Show the 'Local Co-op' button in the game's main menu (F7 always works).");
        JoinArea = Config.Bind("Network", "JoinArea", "", "Client fallback only: area asset name (e.g. R2A1_Desert) used if the host does not answer the info query on Port+1. Normally blank: the client loads the host's area automatically.");
        ClientId = Config.Bind("Network", "ClientId", "", "Random id of this PC for host-assigned player slots (same id = same slot on reconnect). Generated once; leave it.");
        if (string.IsNullOrWhiteSpace(ClientId.Value)) ClientId.Value = System.Guid.NewGuid().ToString("N").Substring(0, 12);
        AutoTest = Config.Bind("Debug", "AutoTest", "", "DEV ONLY. 'host' = start a LAN host automatically after AutoTestDelay s; 'join:IP:PORT' = join. Blank = off.");
        AutoTestDelay = Config.Bind("Debug", "AutoTestDelay", 60f, "Seconds after game start before AutoTest fires.");
        UnverifiedChoices = Config.Bind("Safety", "UnverifiedBuildChoices", "",
            "Your answers to the 'game updated, not tested' warning, per GameAssembly.dll hash (hash=continue|disable;...). " +
            "Clear it to be asked again. Builds whose Fusion code or layout changed are always blocked; there is no bypass.");
        ToggleOverlayKey = Config.Bind("UI", "ToggleOverlay",
            new BepInEx.Unity.IL2CPP.Configuration.KeyboardShortcut(UnityEngine.KeyCode.F7), "Show/hide the OTH-LAN window.");

        PingShow = Config.Bind("PingIndicator", "Show", true, "Show 'local NN ms' next to the game's connection icon during OTH-LAN sessions.");
        PingYellowAboveMs = Config.Bind("PingIndicator", "YellowAboveMs", 200f, "Ping above this (and up to RedAboveMs) is yellow. Up to it: green.");
        PingRedAboveMs = Config.Bind("PingIndicator", "RedAboveMs", 300f, "Ping above this is red.");
        PingSmoothing = Config.Bind("PingIndicator", "Smoothing", 0.3f, "Exponential smoothing factor per sample (1 = none).");
        PingUpdateHz = Config.Bind("PingIndicator", "UpdateHz", 5f, "Echo pings per second to the host (UDP Port+1).");
        PingFallbackX = Config.Bind("PingIndicator", "FallbackX", 110f, "Label position (1080p pixels, scaled) if the game's ping icon cannot be found.");
        PingFallbackY = Config.Bind("PingIndicator", "FallbackY", 24f, "See FallbackX.");
        PingShowLossFromPct = Config.Bind("PingIndicator", "ShowLossFromPercent", 10f, "Append 'loss NN%' to the label when echo loss (last 20 pings) reaches this. 0 = never.");
        CarSyncEnabled = Config.Bind("CarSync", "Enabled", true, "Client-authoritative car sync over UDP Port+1 (on by default). The HOST's value decides for the whole session; clients follow it. Also switchable live in the F7 window.");
        ConfigVersion = Config.Bind("Meta", "ConfigVersion", 0, "Internal: config schema version (used for one-time migrations). Do not edit.");
        if (ConfigVersion.Value < 2)
        {
            // Up to 0.1.5-pre the default was false, and BepInEx wrote "Enabled = false" into every cfg, so the new default
            // would never apply. Migrate once; after that the user's choice (F7 or cfg) is kept.
            if (!CarSyncEnabled.Value) L.LogWarning("[Config] migration v2: CarSync.Enabled false -> true (new default; switch it off in F7 if wanted)");
            CarSyncEnabled.Value = true;
            ConfigVersion.Value = 2;
        }
        if (ConfigVersion.Value < 3)
        {
            // 0.1.7: colours green <= 200 / yellow <= 300 / red. The 0.1.6 defaults (OrangeFromMs=150, RedAboveMs=250) sit in
            // every cfg; replace them once, keep values a user changed by hand.
            double? oldOrange = null;
            try
            {
                foreach (var line in System.IO.File.ReadAllLines(Config.ConfigFilePath))
                {
                    var t = line.Trim();
                    if (t.StartsWith("OrangeFromMs", StringComparison.Ordinal) && t.Contains('=')
                        && double.TryParse(t[(t.IndexOf('=') + 1)..].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var o)) oldOrange = o;
                }
            }
            catch { }
            var (y, r) = PingLogic.MigrateV3(oldOrange, PingRedAboveMs.Value);
            L.LogWarning($"[Config] migration v3: ping colours yellow above {y:0} ms (was orange from {oldOrange?.ToString() ?? "-"}), red above {r:0} ms (was {PingRedAboveMs.Value:0})");
            PingYellowAboveMs.Value = (float)y; PingRedAboveMs.Value = (float)r;
            ConfigVersion.Value = 3;
        }
        CarSendHz = Config.Bind("CarSync", "SendHz", 20f, "Client: own-car pose packets per second.");
        CarHighPingMs = Config.Bind("CarSync", "HighPingMs", 150f, "RTT above which the host switches from continuous blending to checkpoints.");
        CarHysteresisMs = Config.Bind("CarSync", "HysteresisMs", 15f, "Switch to checkpoint mode above HighPingMs+this, back below HighPingMs-this.");
        CarCheckpointSeconds = Config.Bind("CarSync", "CheckpointSeconds", 1.5f, "High-ping mode: force-align every N seconds.");
        CarDesyncMeters = Config.Bind("CarSync", "DesyncMeters", 3f, "High-ping mode: snap immediately if host and client positions differ by more than this.");
        CarDesyncDegrees = Config.Bind("CarSync", "DesyncDegrees", 25f, "High-ping mode: snap immediately above this heading/orientation difference.");
        CarBlendTau = Config.Bind("CarSync", "BlendTau", 0.1f, "Low-ping mode: interpolation time constant (s) toward the client's pose.");
        CarCheckpointMinMeters = Config.Bind("CarSync", "CheckpointMinMeters", 0.25f, "High-ping mode: a checkpoint does nothing if the error is below this AND below CheckpointMinDegrees.");
        CarCheckpointMinDegrees = Config.Bind("CarSync", "CheckpointMinDegrees", 3f, "See CheckpointMinMeters.");
        CarCheckpointSoftSeconds = Config.Bind("CarSync", "CheckpointSoftSeconds", 0.2f, "High-ping mode: a checkpoint correction is a fast blend over this many seconds (others see a short slide, not a jump). 0 = hard snap. Desync snaps stay hard.");
        CarContactPause = Config.Bind("CarSync", "ContactPause", true, "Host: while a client's car touches another car (pushing, bumping), leave it to the host's physics; resume ContactHoldSeconds after.");
        CarContactHoldSeconds = Config.Bind("CarSync", "ContactHoldSeconds", 0.5f, "See ContactPause.");
        CarHalfWidth = Config.Bind("CarSync", "CarHalfWidth", 1.1f, "Contact test: half width of a car in metres (estimate).");
        CarHalfLength = Config.Bind("CarSync", "CarHalfLength", 2.4f, "Contact test: half length of a car in metres (estimate).");
        CarHalfHeight = Config.Bind("CarSync", "CarHalfHeight", 1.2f, "Contact test: half height of a car in metres (estimate).");
        CarContactMargin = Config.Bind("CarSync", "ContactMargin", 0.3f, "Contact test: extra margin in metres.");

        OutExtrapolateFrom = Config.Bind("Outage", "ExtrapolateFromSeconds", 0.15f, "Host: a client's car without fresh poses for longer than this is extrapolated by its last velocity (no pull to a frozen point).");
        OutExtrapolateSeconds = Config.Bind("Outage", "PhysicsOnlyFromSeconds", 1.5f, "Host: after this the car is left to the host's physics, and everyone sees '⚠ no link N s' over it.");
        OutBrakeFrom = Config.Bind("Outage", "BrakeFromSeconds", 1.5f, "Host: after this the car brakes gently to a stop. 1.5 (= PhysicsOnlyFromSeconds) by default: the client's own simulation freezes ~0.85 s into an outage, so a host copy that keeps driving only drifts away (self-test o1m/o2m: error at link-back 6-14 m with 5, 1-5 m with 1.5). Set 5 for the old physics-only phase.");
        OutBrakeSeconds = Config.Bind("Outage", "BrakeSeconds", 1f, "Host: braking to a stop takes about this long.");
        OutSnapMeters = Config.Bind("Outage", "RecoverySnapMeters", 15f, "Host, link back: errors up to this converge softly (0.3..1.5 s); bigger ones (up to 50 m) snap, but never during contact.");
        OutAutoRejoin = Config.Bind("Outage", "AutoRejoinOnFreeze", true, "Client: when the game's network simulation stays frozen (tick unchanged 12 s) although the link to the host is fine again (seen after heavy packet loss), quit to the menu and rejoin the same host automatically (the host keeps your slot).");
        OutContactWait = Config.Bind("Outage", "ContactWaitSeconds", 2f, "Host, link back: a snap waits this long for a car contact to end, then converges softly instead.");
        ContactBoxFromColliders = Config.Bind("CarSync", "ContactBoxFromColliders", true, "Host: measure each car's contact box from its colliders (fallback: HalfWidth/HalfLength/HalfHeight).");
        OwnCarAuthority = Config.Bind("OwnCar", "ClientAuthoritative", true, "Client: Fusion never pulls YOUR car back to the host's position (the host follows you instead). Needs CarSync on (the host decides). Off = vanilla corrections, softened after a link gap. Also in F7.");
        OwnSoftGapSeconds = Config.Bind("OwnCar", "SoftGapSeconds", 0.3f, "Phase 1: a gap without host snapshots longer than this softens the following correction.");
        OwnSoftSeconds = Config.Bind("OwnCar", "SoftSeconds", 1.5f, "Phase 1: how long the softer correction settings stay after a gap.");
        OwnSoftSmoothing = Config.Bind("OwnCar", "SoftSmoothingSeconds", 0.5f, "Phase 1: CorrectionSmoothingTime during that time (game default 0.1).");
        OwnSoftSnapMeters = Config.Bind("OwnCar", "SoftSnapMeters", 20f, "Phase 1: CorrectionSnapDistance during that time (game default 2).");
        OwnAcceptMeters = Config.Bind("OwnCar", "AcceptMeters", 25f, "Phase 2 safety: if the host's position is further than this, take it (game teleport, rescue).");
        OwnPostOutageSeconds = Config.Bind("OwnCar", "PostOutageSeconds", 4f, "Phase 2: for this long after a link gap the turned-host-pose rule is off (the host copy turned under the repeated input during the outage and converges back to us; a real reset still arrives as the host's accept packet). Self-test fo1: without it 4 of 7 outages ended in a rollback to the host pose.");
        OwnAcceptDegrees = Config.Bind("OwnCar", "AcceptDegrees", 30f, "Phase 2 safety: if the host's car is turned by more than this against ours, take it (reset upright, rescue).");
        OwnAcceptSeconds = Config.Bind("OwnCar", "AcceptSeconds", 1.5f, "Phase 2 safety: how long the host's position is taken after such an event.");
        OwnStuckMeters = Config.Bind("OwnCar", "StuckMeters", 8f, "Phase 2 safety: host and client further apart than this ...");
        OwnStuckSeconds = Config.Bind("OwnCar", "StuckSeconds", 3f, "... for this long (host does not converge): take the host's position briefly.");
        OwnFallbackSeconds = Config.Bind("OwnCar", "FallbackSeconds", 10f, "Phase 2 safety: after an invalid own physics state, use host corrections (phase 1) for this long.");

        try { LangWatch.Init(); } catch (Exception e) { L.LogWarning($"[Lang] init: {e.Message}"); }
        BuildGate.Run();     // tiers: supported / unverified (one-time warning) / incompatible; applies the detours if allowed
        Autopilot.Init();    // dev self-test; inert without --othlan-auto on the command line (compiled out of public builds)
        ClassInjector.RegisterTypeInIl2Cpp<Overlay>();
        AddComponent<Overlay>();
        L.LogInfo($"{Name} {Version} loaded. Press {ToggleOverlayKey.Value} for the LAN window. LocalIPs: {string.Join(", ", NetInfo.LocalIPv4())}");
    }
}

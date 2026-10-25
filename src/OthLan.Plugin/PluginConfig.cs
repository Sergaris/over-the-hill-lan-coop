using System;
using BepInEx.Configuration;

namespace OthLan;

/// <summary>cfg schema (every Bind) and one-time migrations by [Meta] ConfigVersion.</summary>
public partial class Plugin
{
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
    internal static ConfigEntry<bool> CarContactPause, CarPosePlayback;
    internal static ConfigEntry<int> CarStreamRedundancy;
    internal static ConfigEntry<bool> OwnCarAuthority, ContactBoxFromColliders, MenuButton, AllowMods, NetStatsLog;
    internal static ConfigEntry<bool> RemoteSmoothing, CarFollowVelocity, CarTeleportOnGameMove, ParkIdleCars;
    internal static ConfigEntry<float> CarServoTau, CarServoVelTau, CarServoMaxCorrection, CarServoMaxTurn, CarServoRotLerpTau, CarServoPosLerpTau;
    internal static ConfigEntry<float> RemoteExtrapolateSeconds, RemoteSmoothTau, RemoteSmoothTauLarge, RemoteMaxCorrection, RemoteMaxCorrectionDeg, RemoteSnapMeters, RemoteAbsorbDegrees, RemoteStarvedSeconds, RemoteProxySmoothing, RemoteProxySnapMeters, RemoteProxySnapDegrees;
    internal static ConfigEntry<float> InputBufferSeconds;
    internal static ConfigEntry<bool> OwnFreezeSmoothing, OwnLocalClock, RemoteInterpolate, OwnLocalFreshInput;
    internal static ConfigEntry<string> ClientPhysicsMode, ContactAuthority, WorldWinchAuthority, ProxyBody;
    internal static ConfigEntry<bool> RelayCarState;
    internal static ConfigEntry<bool> SuspendOnContact, SuspendOnWinch;
    internal static ConfigEntry<float> SuspendResumeSeconds, SharedMinDwellSeconds, ServoSeconds;
    internal static ConfigEntry<float> OwnFreezeExtrapSeconds, OwnFreezeDecaySeconds, OwnFreezeSnapMeters;
    internal static ConfigEntry<string> HostName, ManualHosts, RecentHosts;
    internal static ConfigEntry<bool> OutAutoRejoin, UpdateCheck;
    internal static ConfigEntry<string> UpdateSkipped, UpdateTestApi;
    internal static ConfigEntry<float> OutExtrapolateFrom, OutExtrapolateSeconds, OutBrakeFrom, OutBrakeSeconds, OutSnapMeters, OutContactWait;
    internal static ConfigEntry<float> OwnSoftGapSeconds, OwnSoftSeconds, OwnSoftSmoothing, OwnSoftSnapMeters, OwnAcceptSeconds, OwnAcceptMeters, OwnAcceptDegrees, OwnPostOutageSeconds,
        OwnStuckMeters, OwnStuckSeconds, OwnFallbackSeconds; // AcceptDegrees/Stuck*: bound for cfg compat, unused since 0.1.8 (R4)

    static ConfigFile _cfg;
    internal static string CfgPath => _cfg?.ConfigFilePath;
    /// <summary>Live bound config (SaveOnConfigSet=false — disk file may lag; prefer this for session dumps).</summary>
    internal static ConfigFile Cfg => _cfg;

    /// <summary>Bind every entry; MigrateConfig runs after, so migrations see all entries.</summary>
    static void BindConfig(ConfigFile cfg)
    {
        BindSession(cfg);
        BindPing(cfg);
        BindCarSync(cfg);
        BindNetcode(cfg);
        BindRemote(cfg);
        BindOutage(cfg);
        BindOwnCar(cfg);
        BindUpdate(cfg);
    }

    static void BindSession(ConfigFile cfg)
    {
        Port = cfg.Bind("Network", "Port", 27015, "UDP port the LAN host listens on (open it in Windows Firewall; the installer does).");
        PlayerCount = cfg.Bind("Network", "PlayerCount", 4,
            "Session size. MUST be identical on host and every client (Fusion sizes its simulation from it).");
        LastGameLanguage = cfg.Bind("General", "LastGameLanguage", "", "Internal: the game language seen last time (the mod's texts follow the game's Settings → Language; this is only used until the game has loaded its own setting).");
        JoinAddress = cfg.Bind("Network", "JoinAddress", "", "Host address for 'Join by IP' (Radmin VPN 26.x.x.x:port).");
        HostName = cfg.Bind("LocalCoop", "HostName", "", "Lobby name others see in 'Local Co-op' (empty = your Steam name, else the PC name).");
        ManualHosts = cfg.Bind("LocalCoop", "ManualHosts", "", "Hosts added with 'Add by IP' (ip:port, comma separated); always asked directly.");
        RecentHosts = cfg.Bind("LocalCoop", "RecentHosts", "", "Hosts you joined before (filled automatically; asked directly).");
        MenuButton = cfg.Bind("LocalCoop", "MenuButton", true, "Show the 'Local Co-op' button in the game's main menu (F7 always works).");
        AllowMods = cfg.Bind("LocalCoop", "AllowMods", true, "Host: allow the game's mods in sessions you host from 'Local Co-op' / F7 (the game's own AllowMods room flag; a client takes the host's value). Switch in the Local Co-op screen.");
        JoinArea = cfg.Bind("Network", "JoinArea", "", "Client fallback only: area asset name (e.g. R2A1_Desert) used if the host does not answer the info query on Port+1. Normally blank: the client loads the host's area automatically.");
        ClientId = cfg.Bind("Network", "ClientId", "", "Random id of this PC for host-assigned player slots (same id = same slot on reconnect). Generated once; leave it.");
        if (string.IsNullOrWhiteSpace(ClientId.Value)) ClientId.Value = System.Guid.NewGuid().ToString("N").Substring(0, 12);
        AutoTest = cfg.Bind("Debug", "AutoTest", "", "DEV ONLY. 'host' = start a LAN host automatically after AutoTestDelay s; 'join:IP:PORT' = join. Blank = off.");
        AutoTestDelay = cfg.Bind("Debug", "AutoTestDelay", 60f, "Seconds after game start before AutoTest fires.");
        UnverifiedChoices = cfg.Bind("Safety", "UnverifiedBuildChoices", "",
            "Your answers to the 'game updated, not tested' warning, per GameAssembly.dll hash (hash=continue|disable;...). " +
            "Clear it to be asked again. Builds whose Fusion code or layout changed are always blocked; there is no bypass.");
        ToggleOverlayKey = cfg.Bind("UI", "ToggleOverlay",
            new BepInEx.Unity.IL2CPP.Configuration.KeyboardShortcut(UnityEngine.KeyCode.F7), "Show/hide the OTH-LAN window.");
    }

    static void BindPing(ConfigFile cfg)
    {
        PingShow = cfg.Bind("PingIndicator", "Show", true, "Show 'local NN ms' next to the game's connection icon during OTH-LAN sessions.");
        PingYellowAboveMs = cfg.Bind("PingIndicator", "YellowAboveMs", 200f, "Ping above this (and up to RedAboveMs) is yellow. Up to it: green.");
        PingRedAboveMs = cfg.Bind("PingIndicator", "RedAboveMs", 300f, "Ping above this is red.");
        PingSmoothing = cfg.Bind("PingIndicator", "Smoothing", 0.3f, "Exponential smoothing factor per sample (1 = none).");
        PingUpdateHz = cfg.Bind("PingIndicator", "UpdateHz", 5f, "Echo pings per second to the host (UDP Port+1).");
        PingFallbackX = cfg.Bind("PingIndicator", "FallbackX", 110f, "Label position (1080p pixels, scaled) if the game's ping icon cannot be found.");
        PingFallbackY = cfg.Bind("PingIndicator", "FallbackY", 24f, "See FallbackX.");
        PingShowLossFromPct = cfg.Bind("PingIndicator", "ShowLossFromPercent", 10f, "Append 'loss NN%' to the label when echo loss (last 20 pings) reaches this. 0 = never.");
    }

    static void BindCarSync(ConfigFile cfg)
    {
        CarSyncEnabled = cfg.Bind("CarSync", "Enabled", true, "Ping compensation (0.1.8 netcode v2, on by default): each player's car is simulated on their own PC and the host follows it over UDP Port+1. The HOST's value decides for the whole session; clients follow it. Also switchable live in the F7 window.");
        CarSendHz = cfg.Bind("CarSync", "SendHz", 30f, "Client: own-car pose packets per second (0.1.9.1: was 20; higher rate shortens host follower lag).");
        CarPosePlayback = cfg.Bind("CarSync", "PosePlayback", true, "Host: jitter buffer + Hermite for the follower target (0.1.9.1). Play head holds the newest sample (tick-age still applied in CarSync); delay is jitter-only (~1 packet).");
        CarStreamRedundancy = cfg.Bind("CarSync", "Redundancy", 3, "0.2.0 OTC3: how many recent own-car states to pack in each stream packet (1..3).");
        CarHighPingMs = cfg.Bind("CarSync", "HighPingMs", 150f, "RTT above which the host switches from continuous blending to checkpoints.");
        CarHysteresisMs = cfg.Bind("CarSync", "HysteresisMs", 15f, "Switch to checkpoint mode above HighPingMs+this, back below HighPingMs-this.");
        CarCheckpointSeconds = cfg.Bind("CarSync", "CheckpointSeconds", 1.5f, "High-ping mode: force-align every N seconds.");
        CarDesyncMeters = cfg.Bind("CarSync", "DesyncMeters", 3f, "High-ping mode: snap immediately if host and client positions differ by more than this.");
        CarDesyncDegrees = cfg.Bind("CarSync", "DesyncDegrees", 25f, "High-ping mode: snap immediately above this heading/orientation difference.");
        CarBlendTau = cfg.Bind("CarSync", "BlendTau", 0.1f, "Low-ping mode: interpolation time constant (s) toward the client's pose.");
        CarFollowVelocity = cfg.Bind("CarSync", "FollowByVelocity", true, "Host (0.1.8 v3): follow a client's car by steering its velocity toward the client's pose instead of moving the body. Collisions stay physical, no push-out jumps. Off = 0.1.8 position blend (BlendTau).");
        CarTeleportOnGameMove = cfg.Bind("CarSync", "TeleportOnGameMove", true, "Host: when the game moves a client's car (respawn, rescue, jack, reset upright), mark it as a Fusion teleport so every other player sees it jump at once instead of sliding there.");
        CarServoTau = cfg.Bind("CarSync", "ServoTau", 0.25f, "FollowByVelocity: time constant (s) of the position/rotation error.");
        CarServoVelTau = cfg.Bind("CarSync", "ServoVelocityTau", 0.03f, "FollowByVelocity: how fast (s) the car's velocity takes the commanded one.");
        CarServoMaxCorrection = cfg.Bind("CarSync", "ServoMaxCorrection", 6f, "FollowByVelocity: correction speed limit in m/s on top of the client's speed (plus half of it).");
        CarServoMaxTurn = cfg.Bind("CarSync", "ServoMaxTurn", 3f, "FollowByVelocity: rotation correction limit in rad/s.");
        CarServoRotLerpTau = cfg.Bind("CarSync", "ServoRotationLerpTau", 0.5f, "FollowByVelocity: small direct rotation blend (s).");
        CarServoPosLerpTau = cfg.Bind("CarSync", "ServoPositionLerpTau", 1.0f, "FollowByVelocity: small direct position blend (s), only below 0.5 m of error.");
        CarCheckpointMinMeters = cfg.Bind("CarSync", "CheckpointMinMeters", 0.25f, "High-ping mode: a checkpoint does nothing if the error is below this AND below CheckpointMinDegrees.");
        CarCheckpointMinDegrees = cfg.Bind("CarSync", "CheckpointMinDegrees", 3f, "See CheckpointMinMeters.");
        CarCheckpointSoftSeconds = cfg.Bind("CarSync", "CheckpointSoftSeconds", 0.2f, "High-ping mode: a checkpoint correction is a fast blend over this many seconds (others see a short slide, not a jump). 0 = hard snap. Desync snaps stay hard.");
        CarContactPause = cfg.Bind("CarSync", "ContactPause", true, "Host: while a client's car touches another car (pushing, bumping), leave it to the host's physics; resume ContactHoldSeconds after.");
        CarContactHoldSeconds = cfg.Bind("CarSync", "ContactHoldSeconds", 0.5f, "See ContactPause.");
        CarHalfWidth = cfg.Bind("CarSync", "CarHalfWidth", 1.1f, "Contact test: half width of a car in metres (estimate).");
        CarHalfLength = cfg.Bind("CarSync", "CarHalfLength", 2.4f, "Contact test: half length of a car in metres (estimate).");
        CarHalfHeight = cfg.Bind("CarSync", "CarHalfHeight", 1.2f, "Contact test: half height of a car in metres (estimate).");
        CarContactMargin = cfg.Bind("CarSync", "ContactMargin", 0.3f, "Contact test: extra margin in metres.");
        ContactBoxFromColliders = cfg.Bind("CarSync", "ContactBoxFromColliders", true, "Host: measure each car's contact box from its colliders (fallback: HalfWidth/HalfLength/HalfHeight).");
    }

    static void BindNetcode(ConfigFile cfg)
    {
        OwnFreezeSmoothing = cfg.Bind("Netcode", "OwnFreezeSmoothing", true, "Client: when host data stalls, Fusion stops simulating after 1 s of prediction and then catches up in one frame (car stops, then lurches). On = your car keeps moving on screen with its last velocity and the catch-up is blended (render only, never toward the host). Works with ping compensation on and off.");
        RemoteInterpolate = cfg.Bind("Netcode", "RemoteInterpolate", true, "Client: other players' cars are shown from Fusion's interpolation buffer (slightly in the past, smooth, never corrected) instead of being predicted. Collisions with them are approximate.");
        OwnLocalClock = cfg.Bind("Netcode", "OwnLocalClock", true, "EXPERIMENTAL. Client, ping compensation on: your car is stepped by real time like in single player. When the host's data stalls and Fusion stops simulating, your car keeps driving with your last input; Fusion's catch-up ticks are then not applied to it. If your car twitches, turn this off (also in F7 on the client).");
        OwnLocalFreshInput = cfg.Bind("Netcode", "OwnLocalFreshInput", true, "With OwnLocalClock: local steps use your current steering/throttle/brake (read from the game's input code) instead of the last input Fusion took.");
        SuspendOnContact = cfg.Bind("Netcode", "SuspendOnContact", true, "0.1.9.1: client pauses OwnLocalClock and phase-2 authority while your car touches another player's car, so both sides share the host's collision. Resumes after SuspendResumeSeconds clear.");
        SuspendOnWinch = cfg.Bind("Netcode", "SuspendOnWinch", true, "0.1.9.1: same pause while a winch involves your car.");
        SuspendResumeSeconds = cfg.Bind("Netcode", "SuspendResumeSeconds", 1f, "0.1.9.1: how long after the last contact/winch before OwnLocal/phase2 resume (also stretched by RTT).");
        OwnFreezeExtrapSeconds = cfg.Bind("Netcode", "OwnFreezeExtrapSeconds", 0.5f, "Client: longest freeze your car is drawn moving through (longer freezes hold the last extrapolated pose).");
        OwnFreezeDecaySeconds = cfg.Bind("Netcode", "OwnFreezeDecaySeconds", 0.1f, "Client: time constant of the blend after a freeze.");
        OwnFreezeSnapMeters = cfg.Bind("Netcode", "OwnFreezeSnapMeters", 2f, "Client: differences larger than this (respawn, rescue) snap instead of blending.");
        InputBufferSeconds = cfg.Bind("Netcode", "InputBufferSeconds", 8f, "Client: how many seconds of own inputs Fusion may keep unconfirmed (game: 1 s). With 1 s, a host-to-client stall of ~0.7 s at 200 ms ping made Fusion drop your inputs (log: 'buffer was full') = jerky control. 0 = game default.");
        NetStatsLog = cfg.Bind("Netcode", "StatsToLog", true, "Write network statistics (stalls, corrections, smoothness) to the log every 30 s of a session.");
        ClientPhysicsMode = cfg.Bind("Netcode", "ClientPhysicsMode", "Game",
            "Client only: Fusion ClientPhysicsSimulation. Game = leave the game's default (SimulateAlways; recommended). SimulateForward = Physics.Simulate only on forward ticks (less CPU on resim, but the own car bounces and ignores steering after prediction resets).");
        ParkIdleCars = cfg.Bind("Netcode", "ParkIdleCars", true,
            "Host only: driverless cars standing still out of every player's winch reach are frozen, so their idle physics noise stops filling every network packet (fewer client freezes on lossy links). They wake as soon as a player car comes near.");
        ContactAuthority = cfg.Bind("Netcode", "ContactAuthority", "Owner",
            "0.2.0 T4.1: who owns car-to-car contact. Owner = each PC resolves its own car (D-1). Host = enter HostShared on contact.");
        WorldWinchAuthority = cfg.Bind("Netcode", "WorldWinchAuthority", "Owner",
            "0.2.0 T4.1: world/tree winch. Owner = stay on owner stream; Host = HostShared while winched to world.");
        SharedMinDwellSeconds = cfg.Bind("Netcode", "SharedMinDwellSeconds", 2f,
            "0.2.0 T4.1: minimum seconds in HostShared before exit is allowed (kills suspend chatter).");
        ProxyBody = cfg.Bind("Netcode", "ProxyBody", "Kinematic",
            "0.2.0 T4.0: remote car rigidbody for contacts. Kinematic = MovePosition (infinite mass). Servo = dynamic + stream pull.");
        ServoSeconds = cfg.Bind("Netcode", "ServoSeconds", 0.1f,
            "0.2.0 T4.0: ProxyBody=Servo time constant (s) to pull the remote body back to the owner stream.");
        RelayCarState = cfg.Bind("Netcode", "RelayCarState", true,
            "0.2.0 T3: send/relay OTC3 own-car stream (Port+1). Off = observers fall back to Fusion + RemoteSmoother (rollback if stream is blocked).");
    }

    static void BindRemote(ConfigFile cfg)
    {
        RemoteSmoothing = cfg.Bind("Netcode", "RemoteSmoothing", false, "Experimental, off: needs a separate InterpolationTarget, which Over The Hill's cars do not have (then it does nothing; the other players' cars are smoothed by RemoteProxy* instead). Draw the other players' cars smoothly: keep them moving through short host stalls (up to RemoteExtrapolateSeconds) and fade network corrections out instead of jumping. Visual only (physics and collisions unchanged).");
        RemoteExtrapolateSeconds = cfg.Bind("Netcode", "RemoteExtrapolateSeconds", 0.5f, "Remote smoothing: how long another car keeps moving at its last speed while no host data arrives.");
        RemoteSmoothTau = cfg.Bind("Netcode", "RemoteSmoothTau", 0.25f, "Remote smoothing: time constant (s) for fading out a small correction (up to 25 cm).");
        RemoteSmoothTauLarge = cfg.Bind("Netcode", "RemoteSmoothTauLarge", 0.1f, "Remote smoothing: time constant (s) for fading out a big correction (1 m and more): big pops are corrected quickly.");
        RemoteMaxCorrection = cfg.Bind("Netcode", "RemoteMaxCorrection", 6f, "Remote smoothing: correction speed limit in m/s (plus half the car's speed).");
        RemoteMaxCorrectionDeg = cfg.Bind("Netcode", "RemoteMaxCorrectionDeg", 180f, "Remote smoothing: rotation correction speed limit in deg/s.");
        RemoteSnapMeters = cfg.Bind("Netcode", "RemoteSnapMeters", 15f, "Remote smoothing: jumps larger than this are shown at once (respawn, rescue).");
        RemoteAbsorbDegrees = cfg.Bind("Netcode", "RemoteAbsorbDegrees", 3f, "Remote smoothing: rotation steps larger than this in one frame are faded out.");
        RemoteStarvedSeconds = cfg.Bind("Netcode", "RemoteStarvedSeconds", 0.1f, "Remote smoothing (client): no NEW host snapshot (ServerTickAge) for this long = stall; only then extrapolate. A car that really stopped is not extrapolated.");
        RemoteProxySmoothing = cfg.Bind("Netcode", "RemoteProxySmoothingSeconds", 0.25f, "Client: Fusion's correction smoothing time for the other players' cars (game default 0.1 s). Remote cars are predicted ahead of the host; when the other driver brakes or turns, the correction is drawn over this time instead of in one frame. 0 = game default.");
        RemoteProxySnapMeters = cfg.Bind("Netcode", "RemoteProxySnapMeters", 10f, "Client: corrections of the other players' cars larger than this snap (game default 2 m; respawns and rescues are larger). 0 = game default.");
        RemoteProxySnapDegrees = cfg.Bind("Netcode", "RemoteProxySnapDegrees", 90f, "Client: rotation corrections of the other players' cars larger than this snap (game default 30). 0 = game default.");
    }

    static void BindOutage(ConfigFile cfg)
    {
        OutExtrapolateFrom = cfg.Bind("Outage", "ExtrapolateFromSeconds", 0.15f, "Host: a client's car without fresh poses for longer than this is extrapolated by its last velocity (no pull to a frozen point).");
        OutExtrapolateSeconds = cfg.Bind("Outage", "PhysicsOnlyFromSeconds", 3f, "Host: after this the car is left to the host's physics, and everyone sees '⚠ no link N s' over it.");
        OutBrakeFrom = cfg.Bind("Outage", "BrakeFromSeconds", 3f, "Host: after this the car brakes gently to a stop. 0.1.8: 3 s (was 1.5): the client's simulation no longer freezes ~0.85 s into an outage (its input buffer is larger now), it keeps driving, so the host copy keeps extrapolating longer.");
        OutBrakeSeconds = cfg.Bind("Outage", "BrakeSeconds", 1f, "Host: braking to a stop takes about this long.");
        OutSnapMeters = cfg.Bind("Outage", "RecoverySnapMeters", 15f, "Host, link back: errors up to this converge softly (0.3..1.5 s); bigger ones (up to 50 m) snap, but never during contact.");
        OutAutoRejoin = cfg.Bind("Outage", "AutoRejoinOnFreeze", true, "Client: when the game's network simulation stays frozen (tick unchanged 12 s) although the link to the host is fine again (seen after heavy packet loss), quit to the menu and rejoin the same host automatically (the host keeps your slot).");
        OutContactWait = cfg.Bind("Outage", "ContactWaitSeconds", 2f, "Host, link back: a snap waits this long for a car contact to end, then converges softly instead.");
    }

    static void BindOwnCar(ConfigFile cfg)
    {
        OwnCarAuthority = cfg.Bind("OwnCar", "ClientAuthoritative", true, "Expert: client part of ping compensation (your car is simulated on your PC and never pulled back; the host follows you). Normally leave on and use the one F7 switch (ping compensation, the host decides). Off = vanilla corrections even when the host has compensation on.");
        OwnSoftGapSeconds = cfg.Bind("OwnCar", "SoftGapSeconds", 0.3f, "Phase 1: a gap without host snapshots longer than this softens the following correction.");
        OwnSoftSeconds = cfg.Bind("OwnCar", "SoftSeconds", 1.5f, "Phase 1: how long the softer correction settings stay after a gap.");
        OwnSoftSmoothing = cfg.Bind("OwnCar", "SoftSmoothingSeconds", 0.5f, "Phase 1: CorrectionSmoothingTime during that time (game default 0.1).");
        OwnSoftSnapMeters = cfg.Bind("OwnCar", "SoftSnapMeters", 20f, "Phase 1: CorrectionSnapDistance during that time (game default 2).");
        OwnAcceptMeters = cfg.Bind("OwnCar", "AcceptMeters", 25f, "Phase 2 safety: if the host's position is further than this, take it (game teleport, rescue).");
        OwnPostOutageSeconds = cfg.Bind("OwnCar", "PostOutageSeconds", 4f, "Phase 2: for this long after a link gap the turned-host-pose rule is off (the host copy turned under the repeated input during the outage and converges back to us; a real reset still arrives as the host's accept packet). Self-test fo1: without it 4 of 7 outages ended in a rollback to the host pose.");
        OwnAcceptDegrees = cfg.Bind("OwnCar", "AcceptDegrees", 30f, "UNUSED since 0.1.8 (kept so old cfgs still load). Was: take host pose if heading differed by more than this.");
        OwnAcceptSeconds = cfg.Bind("OwnCar", "AcceptSeconds", 1.5f, "Phase 2 safety: how long the host's position is taken after such an event.");
        OwnStuckMeters = cfg.Bind("OwnCar", "StuckMeters", 8f, "UNUSED since 0.1.8 (kept so old cfgs still load). Was: take host pose if stuck far apart.");
        OwnStuckSeconds = cfg.Bind("OwnCar", "StuckSeconds", 3f, "UNUSED since 0.1.8 (kept so old cfgs still load). Was: stuck duration before taking host pose.");
        OwnFallbackSeconds = cfg.Bind("OwnCar", "FallbackSeconds", 10f, "Phase 2 safety: after an invalid own physics state, use host corrections (phase 1) for this long.");
    }

    static void BindUpdate(ConfigFile cfg)
    {
        UpdateCheck = cfg.Bind("Update", "CheckForUpdates", true, "Once per game start, ask GitHub (github.com/Sergaris/over-the-hill-lan-coop, latest release) whether a newer OTH-LAN exists, and offer it in the main menu. Nothing is installed without your click; the download is sha256-checked.");
        UpdateSkipped = cfg.Bind("Update", "SkippedVersion", "", "Internal: the version you chose 'Skip this version' for (a newer one is offered again). F7 'Check now' clears it.");
#if SELFTEST
        UpdateTestApi = cfg.Bind("Update", "TestApiUrl", "", "DEV/self-test build only: release JSON URL instead of the GitHub API (http://127.0.0.1 allowed). Not in public builds.");
#endif
    }

    /// <summary>Steps v2..v5 (0.1.x defaults). A changed default only reaches existing users through a new step.</summary>
    static void MigrateConfig(ConfigFile cfg)
    {
        ConfigVersion = cfg.Bind("Meta", "ConfigVersion", 0, "Internal: config schema version (used for one-time migrations). Do not edit.");
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
            var oldOrange = ReadOldOrangeFromMs(cfg);
            var (y, r) = PingLogic.MigrateV3(oldOrange, PingRedAboveMs.Value);
            L.LogWarning($"[Config] migration v3: ping colours yellow above {y:0} ms{(oldOrange.HasValue ? $" (was orange from {oldOrange:0})" : "")}, red above {r:0} ms (was {PingRedAboveMs.Value:0})");
            PingYellowAboveMs.Value = (float)y; PingRedAboveMs.Value = (float)r;
            ConfigVersion.Value = 3;
        }
        if (ConfigVersion.Value < 4)
        {
            // 0.1.8 netcode v2: ping compensation (own car simulated locally + host follows) is ON by default and is one
            // switch (CarSync.Enabled + OwnCar.ClientAuthoritative). Many 0.1.7 cfgs have one of them off after testing
            // the old, jerky version; reset once, the user can switch it off again in F7.
            L.LogWarning($"[Config] migration v4: ping compensation on (was CarSync={CarSyncEnabled.Value}, OwnCar={OwnCarAuthority.Value})");
            CarSyncEnabled.Value = true; OwnCarAuthority.Value = true;
            ConfigVersion.Value = 4;
            if (Math.Abs(OutExtrapolateSeconds.Value - 1.5f) < 1e-4) OutExtrapolateSeconds.Value = 3f;
            if (Math.Abs(OutBrakeFrom.Value - 1.5f) < 1e-4) OutBrakeFrom.Value = 3f;
        }
        if (ConfigVersion.Value < 5)
        {
            // 0.1.8 netcode v4 (own car on the local clock, remote cars interpolated): on by default; dev/test cfgs may
            // still hold the old experimental "false" values. Reset once; each can be switched off again in the cfg.
            L.LogWarning($"[Config] migration v5: OwnLocalClock/RemoteInterpolate/OwnFreezeSmoothing on (were {OwnLocalClock.Value}/{RemoteInterpolate.Value}/{OwnFreezeSmoothing.Value})");
            OwnLocalClock.Value = true; RemoteInterpolate.Value = true; OwnFreezeSmoothing.Value = true;
            ConfigVersion.Value = 5;
        }
        MigrateConfig02x();
    }

    /// <summary>Steps v6.. (0.2.x defaults).</summary>
    static void MigrateConfig02x()
    {
        if (ConfigVersion.Value < 6)
        {
            // 0.2.0: SimulateForward + OTC3 stream defaults. OwnLocalClock stays on (T2.4; LIVE A/B after release).
            L.LogWarning($"[Config] migration v6: ClientPhysicsMode=SimulateForward (was {ClientPhysicsMode.Value})");
            ClientPhysicsMode.Value = "SimulateForward";
            ConfigVersion.Value = 6;
        }
        if (ConfigVersion.Value < 7)
        {
            L.LogWarning($"[Config] migration v7: ContactAuthority=Owner WorldWinchAuthority=Owner ProxyBody=Kinematic (D-1)");
            ContactAuthority.Value = "Owner";
            WorldWinchAuthority.Value = "Owner";
            ProxyBody.Value = "Kinematic";
            ConfigVersion.Value = 7;
        }
        if (ConfigVersion.Value < 8)
        {
            L.LogWarning("[Config] migration v8: RelayCarState=true (OTC3 stream on)");
            RelayCarState.Value = true;
            ConfigVersion.Value = 8;
        }
        if (ConfigVersion.Value < 9)
        {
            L.LogWarning($"[Config] migration v9: ClientPhysicsMode=Game (was {ClientPhysicsMode.Value})");
            ClientPhysicsMode.Value = "Game";
            ConfigVersion.Value = 9;
        }
    }

    /// <summary>v3: the 0.1.6 OrangeFromMs value, read from the file (the key is no longer bound).</summary>
    static double? ReadOldOrangeFromMs(ConfigFile cfg)
    {
        double? oldOrange = null;
        try
        {
            foreach (var line in System.IO.File.ReadAllLines(cfg.ConfigFilePath))
            {
                var t = line.Trim();
                if (t.StartsWith("OrangeFromMs", StringComparison.Ordinal) && t.Contains('=')
                    && double.TryParse(t[(t.IndexOf('=') + 1)..].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var o)) oldOrange = o;
            }
        }
        catch { }
        return oldOrange;
    }

    static void OnConfigChanged(object sender, SettingChangedEventArgs e) => SaveConfigShared();

    /// <summary>Config.Save() with retries: another game instance may be writing the same cfg right now.</summary>
    internal static void SaveConfigShared()
    {
        var cfg = _cfg;
        if (cfg == null) return;
        var rnd = new Random();
        for (var i = 0; ; i++)
        {
            try { cfg.Save(); return; }
            catch (System.IO.IOException e)
            {
                if (i >= 9) { L?.LogWarning($"[Config] could not write the cfg (another game instance holds it?): {e.Message}"); return; }
                System.Threading.Thread.Sleep(30 + rnd.Next(70));
            }
            catch (Exception e) { L?.LogWarning($"[Config] write failed: {e.Message}"); return; }
        }
    }
}

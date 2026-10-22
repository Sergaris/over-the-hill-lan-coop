using System;
using System.Net;

namespace OthLan;

public enum LanRole { Off, Host, Client }

/// <summary>Armed LAN intent. The next Fusion StartGame whose GameMode matches the role is redirected to direct UDP.</summary>
internal static class LanState
{
    public static bool Disabled;
    public static LanRole Armed = LanRole.Off;
    public static string TargetIp;
    public static ushort TargetPort;

    // Captured in StartGame, consumed in Initialize (same synchronous call chain).
    [ThreadStatic] public static LanRole Pending;
    [ThreadStatic] public static bool ForceNullSocketPath;   // makes Initialize pick the non-cloud socket branch
    [ThreadStatic] public static bool SwapSocket;            // Simulation.NetworkInit gets a NetSocketNative
    [ThreadStatic] public static byte[] Token;
    [ThreadStatic] public static long UniqueId;

    public static LanRole Active = LanRole.Off;              // role of the currently running LAN session

    // The NetworkRunner whose StartGame we redirected and its role. Fusion 2 takes player identity from Photon Cloud
    // (CloudServices); for this runner the hooks supply it locally instead (see FusionHooks, 0.1.3).
    public static IntPtr LanRunner;
    public static LanRole LanRunnerRole = LanRole.Off;
    public static IntPtr LanSim;                              // cached runner._simulation pointer
    public static int ClientSlot;                             // PlayerRef index this client claims (host is index 1)
    public static int AssignedSlot = -1;                      // client: slot the host reserved for us (-1 = none: IP fallback)
    public static string SlotSource = "";                     // client: "host" | "fallback" (for F7 and logs)
    public static readonly SlotAllocator Slots = new(4, HostIndex);   // host: reservations (0.1.6)
    public static string ClientId => SelfTestClientId ?? Plugin.ClientId.Value;
    public static string SelfTestClientId;                    // self-test: several clients share one config file
    public static readonly System.Collections.Generic.Dictionary<int, ulong> SlotOwners = new();
    public static int HostPlayers;                            // client: PlayerCount reported by the host (0 = unknown)
    public static int EffectivePlayers => Math.Max(2, HostPlayers > 0 ? HostPlayers : Plugin.PlayerCount.Value);
    public const int HostIndex = 1;                           // what Fusion's own single-player path uses (raw 2)
    public const byte UidMagic = 0x4C;                        // 'L' in byte 1 of the connect uniqueId
    public static Msg LastStatus = new("status.idle");
    public static void Status(string key, params object[] args) => LastStatus = new Msg(key, args);

    // Watchdog for a LAN start attempt (main thread only).
    public static bool Watching;
    public static LanRole WatchRole;
    public static float WatchStart;          // Time.realtimeSinceStartup when the game flow was started
    public static float StartGameSeenAt = -1; // set by the StartGame detour when the redirect happened
    public static volatile string FailureText; // set from the Unity log listener

    public static bool TryParseEndpoint(string s, out string ip, out ushort port)
    {
        ip = null; port = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        var i = s.LastIndexOf(':');
        var host = i > 0 ? s[..i] : s;
        var p = i > 0 ? s[(i + 1)..] : Plugin.Port.Value.ToString();
        if (!IPAddress.TryParse(host, out var a) || a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        if (!ushort.TryParse(p, out port) || port == 0) return false;
        ip = a.ToString();
        return true;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx.Unity.IL2CPP.Hook;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace OthLan;

/// <summary>
/// DEV self-test autopilot. Inert unless the game is started with --othlan-auto on its command line (never set by the
/// installer). Lets one PC run a host and a client instance unattended:
///   --othlan-auto host | join:IP:PORT     start a LAN host / join (after --othlan-delay s, default 25, once the menu is up)
///   --othlan-area NAME                    host area when none is loaded (default R2A1_Desert)
///   --othlan-tag NAME                     names the output files (default "solo")
///   --othlan-out DIR                      output folder (default BepInEx\selftest)
///   --othlan-drive S                      scripted driving input for S seconds after the own car exists (default 0)
///   --othlan-quit S                       Application.Quit S seconds after the own car exists (default 120); hard cap 20 min
///   --othlan-carsync off|measure|on       CarSync override (measure = send poses, host logs the gap, never moves cars)
///   --othlan-netem t:rttMs:loss%,...      client side: extra RTT / loss on OUR UDP side channel from t s on (Fusion untouched)
///   --othlan-toggle t:on|off,...          host: flip CarSync via CarSync.UserToggle (the F7 button's call)
///   --othlan-winch tAttach,tDetach        client: attach the own winch to the ground ~12 m ahead, detach later
///   --othlan-hostjump t                   host: move the client's car 6 m on the host once (like a game teleport)
///   --othlan-ram t0:t1                    while driving, from t0 to t1 s: steer at the nearest other car (collision test)
///   --othlan-lights SEC:on|off,...        toggle own headlights at scenario second (U3/R1); metrics CSV gains lightWant/lightSeen columns
///   --othlan-cpmin t:metres,...           host: checkpoint skip threshold override (-1 = config) to exercise soft snaps
///   --othlan-contactbox t:metres,...      host: force the car-contact test box (0 = real size) to exercise the contact pause
///   --othlan-f7 t1,...                    open the F7 window, screenshot it, close it
///   --othlan-fakeliverymods CSV           host: force HostInfo liverymods= (e.g. 999001:0) for the pre-join mod gate test
///   --othlan-modgate cancel|download      client: when the missing-mods dialog appears, screenshot it then Cancel or Download
///   --othlan-demo NAME[:IP[:LOBBY]]       README shots: placeholder IP/lobby name in the mod UI, Steam name -> NAME in game texts
///   --othlan-shots t1,t2,...              ScreenCapture at those seconds (game renders it itself: no focus or input needed)
///   --othlan-simbuild unverified|incompatible   pretend the game files are unknown (read by BuildGate; files untouched)
///   --othlan-gate T:continue|disable      realtime T: screenshot the version dialog ({tag}-gate.png) and answer it (same call as its buttons)
///   --othlan-menuf7 T1,...                realtime: open F7 in the menu and screenshot it ({tag}-menu-f7-tNNN.png)
///   --othlan-menuquit T                   realtime: quit at T even if never in the world (gate-only runs)
///   --othlan-clientid ID                  client id for the host's slot reservation (instances share one config file)
///   --othlan-gahash HEX                   report this GameAssembly hash to the host/client build check (mismatch test)
///   --othlan-rejoin t                     client: leave the session at scenario t (game's own quit-to-menu) and join again
///   --othlan-outage t:dur,...             FULL outage of this process (Fusion + side channel, both directions) at t for dur s;
///                                         screenshots at mid-outage and 0.5 s after it ({tag}-out-tNNN-mid/after.png)
///   --othlan-coop look|host:N|join|joinfull  (with --othlan-auto coop) drive the main-menu Local Co-op screen: screenshots
///                                         {tag}-coop-menu/-screen/-region/-area.png, then become host (game's own region/area
///                                         flow, submitted like the Enter key) or join the first lobby from the list
///   --othlan-fnetem t:delayMs:jitterMs:stallEveryMs:stallMs,...  Fusion datagrams of this process from t on: delay+jitter
///                                         per direction, incoming held for stallMs every stallEveryMs (0.1.8; 0:0:0:0:0 = off)
///   --othlan-own off|measure|suppress     own-car prediction reset: no hooks / vanilla but measured / phase 2 (default: config)
///   (always) {tag}-trace.csv at 20 Hz: outage state, own car, rollback, the host's view of client cars and their inputs
/// While active it also: sets Application.runInBackground, suppresses the game's own save writes (OverlandSaver.SaveGame,
/// SaveSettings, SaveJson, FogOfWarManager.SaveFogOfWarTexture) so two instances never touch the real profile, and does
/// not persist JoinAddress to the shared config.
/// </summary>
#if SELFTEST
internal static unsafe class Autopilot
{
    public static bool Active;
    public static string Mode, Tag = "solo", OutDir, CarSyncMode, Area = "R2A1_Desert";
    public static float Delay = 25, DriveS, QuitS = 120;
    static readonly List<(float t, int rtt, float loss)> Netem = new();
    static readonly List<float> Shots = new();
    static readonly List<(float t, bool on)> Toggles = new();
    static readonly List<(float t, float box)> ContactBoxes = new(); static int _nextBox;
    static float _hostJumpAt = -1;
    static float _ramFrom = -1, _ramTo = -1, _ramSteer, _ramDist = -1; static bool _ramming; public static int RamContacts; static float _nextRamScan; static FusionPlayer[] _ramCars = Array.Empty<FusionPlayer>();
    static readonly List<(float t, bool on)> Lights = new(); static int _nextLight; public static int LightToggles, LightMismatches;
    static readonly List<(float t, float v)> CpMins = new(); static int _nextCpMin;
    static float _winchAt = -1, _winchOffAt = -1, _winchCheckAt = -1;
    static readonly List<float> F7Shots = new();
    static int _nextToggle, _nextF7; static float _f7HideAt = -1, _f7ShotAt = -1; static string _f7Name;
    /// <summary>Host self-test: override HostInfo liverymods= (null = collect for real).</summary>
    public static string FakeLiveryMods;
    /// <summary>Client self-test: when ModLiveryGate opens — "cancel" or "download".</summary>
    public static string ModGateAction;
    static float _modGateAt = -1; static bool _modGateShot;
    static int _nextShot, _nextNetem;
    static StreamWriter _log;
    static float _t0 = -1, _nextRow, _lastStatusLog, _pausedT = -1, _carSeenAt = -1;
    public static volatile bool Driving;
    static float _driveT, _reverseUntil, _slowSince = -1;
    public static int Unstucks;
    static readonly List<string> Checks = new();
    static int _passed, _failed;
    public static string DemoName; static string _persona; static float _nextScrub;
    /// <summary>Demo mode: replace the Steam persona name (and the PC user name) in every TextMeshPro text.</summary>
    static void Scrub(float now)
    {
        if (DemoName == null || now < _nextScrub) return;
        _nextScrub = now + 0.2f;
        if (string.IsNullOrEmpty(_persona)) { try { _persona = Steamworks.SteamFriends.GetPersonaName(); } catch { } }
        var user = Environment.UserName;
        try
        {
            foreach (var t in UnityEngine.Object.FindObjectsOfType<TMPro.TMP_Text>())
            {
                var s = t.text; if (string.IsNullOrEmpty(s)) continue;
                var r = s;
                if (!string.IsNullOrEmpty(_persona) && r.Contains(_persona)) r = r.Replace(_persona, DemoName);
                if (!string.IsNullOrEmpty(user) && r.Contains(user)) r = r.Replace(user, DemoName);
                if (r != s) t.text = r;
            }
        }
        catch { }
    }
    static float _gateAt = -1, _gateShotDone = -1, _menuQuit = -1, _rejoinAt = -1; static string _gateChoice;
    static readonly List<float> MenuF7 = new(); static int _nextMenuF7; static float _menuShotAt = -1, _menuHideAt = -1; static string _menuName;
    public static int Rejoins; public static readonly List<int> SlotsSeen = new();
    static readonly List<(float t, float dur)> Outages = new(); static int _nextOutage = 0; static float _outageEnd = -1, _outageDelay;
    static readonly List<(float t, string name)> OutShots = new();
    static readonly List<(float t, float loss)> Losses = new(); static int _nextLoss;
    static readonly List<(float t, int delay, int jitter, int every, int stall)> FNetem = new(); static int _nextFNetem;
    static StreamWriter _trace; static float _nextTrace; static FusionPlayer[] _tracePlayers = Array.Empty<FusionPlayer>(); static float _nextTraceScan;

    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr GetCommandLineW();
    [DllImport("shell32", CharSet = CharSet.Unicode)] static extern IntPtr CommandLineToArgvW(string cmd, out int n);
    [DllImport("kernel32")] static extern IntPtr LocalFree(IntPtr p);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);

    static string[] RawArgs()
    {
        var cmd = Marshal.PtrToStringUni(GetCommandLineW());
        var p = CommandLineToArgvW(cmd, out var n);
        var a = new string[n];
        for (int i = 0; i < n; i++) a[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, i * IntPtr.Size));
        LocalFree(p);
        return a;
    }

    /// <summary>Value after a command-line flag (null if absent). Usable before Init (the gate reads --othlan-simbuild).</summary>
    public static string ArgValue(string k)
    {
        try { var a = RawArgs(); var i = Array.IndexOf(a, k); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
        catch { return null; }
    }

    public static void Init()
    {
        string[] args;
        try { args = RawArgs(); } catch { return; }
        string Arg(string k) { var i = Array.IndexOf(args, k); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        Mode = Arg("--othlan-auto");
        if (string.IsNullOrEmpty(Mode)) return;
        Active = true;
        var ci = CultureInfo.InvariantCulture;
        Tag = Arg("--othlan-tag") ?? Tag;
        OutDir = Arg("--othlan-out") ?? Path.Combine(BepInEx.Paths.BepInExRootPath, "selftest");
        Area = Arg("--othlan-area") ?? Area;
        if (Arg("--othlan-delay") is { } d) Delay = float.Parse(d, ci);
        if (Arg("--othlan-drive") is { } dr) DriveS = float.Parse(dr, ci);
        if (Arg("--othlan-quit") is { } q) QuitS = float.Parse(q, ci);
        CarSyncMode = Arg("--othlan-carsync");
        if (CarSyncMode != null) CarSync.Override = CarSyncMode;
        if (Arg("--othlan-netem") is { } ne)
            foreach (var part in ne.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = part.Split(':');
                Netem.Add((float.Parse(f[0], ci), int.Parse(f[1], ci), f.Length > 2 ? float.Parse(f[2], ci) / 100f : 0f));
            }
        if (Arg("--othlan-toggle") is { } tg)   // t:on|off,... (host): flips through CarSync.UserToggle = the F7 button's call
            foreach (var part in tg.Split(',', StringSplitOptions.RemoveEmptyEntries))
            { var f = part.Split(':'); Toggles.Add((float.Parse(f[0], ci), f[1] == "on")); }
        if (Arg("--othlan-winch") is { } wa)
        { var f = wa.Split(','); _winchAt = float.Parse(f[0], ci); _winchOffAt = float.Parse(f[1], ci); }
        if (Arg("--othlan-cpmin") is { } cm)
            foreach (var part in cm.Split(',', StringSplitOptions.RemoveEmptyEntries))
            { var f = part.Split(':'); CpMins.Add((float.Parse(f[0], ci), float.Parse(f[1], ci))); }
        if (Arg("--othlan-hostjump") is { } hj) _hostJumpAt = float.Parse(hj, ci);
        if (Arg("--othlan-ram") is { } rm) { var rq = rm.Split(':'); _ramFrom = float.Parse(rq[0], ci); _ramTo = float.Parse(rq[1], ci); }
        if (Arg("--othlan-lights") is { } lt)   // seconds:on|off,... (literal "t" is not a time — that killed Load with FormatException)
            foreach (var part in lt.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = part.Split(':');
                if (f.Length < 2 || !float.TryParse(f[0], NumberStyles.Float, ci, out var at))
                { Plugin.L.LogWarning($"[Autopilot] --othlan-lights skip '{part}' (need seconds:on|off)"); continue; }
                Lights.Add((at, f[1].Equals("on", StringComparison.OrdinalIgnoreCase)));
            }
        Lights.Sort((a, b) => a.t.CompareTo(b.t));
        if (Arg("--othlan-contactbox") is { } cb)
            foreach (var part in cb.Split(',', StringSplitOptions.RemoveEmptyEntries))
            { var f = part.Split(':'); ContactBoxes.Add((float.Parse(f[0], ci), float.Parse(f[1], ci))); }
        if (Arg("--othlan-f7") is { } f7)       // t,... : open the F7 window, screenshot it, close it
            foreach (var part in f7.Split(',', StringSplitOptions.RemoveEmptyEntries)) F7Shots.Add(float.Parse(part, ci));
        FakeLiveryMods = Arg("--othlan-fakeliverymods");
        ModGateAction = Arg("--othlan-modgate");
        if (Arg("--othlan-gate") is { } ga) { var f = ga.Split(':'); _gateAt = float.Parse(f[0], ci); _gateChoice = f[1]; }
        if (Arg("--othlan-menuf7") is { } mf) foreach (var part in mf.Split(',', StringSplitOptions.RemoveEmptyEntries)) MenuF7.Add(float.Parse(part, ci));
        if (Arg("--othlan-menuquit") is { } mq) _menuQuit = float.Parse(mq, ci);
        if (Arg("--othlan-clientid") is { } cid) LanState.SelfTestClientId = cid;
        if (Arg("--othlan-rejoin") is { } rj) _rejoinAt = float.Parse(rj, ci);
        CoopAction = Arg("--othlan-coop");
        UpdAction = Arg("--othlan-update");   // install | badhash | offline | uptodate | skip (0.1.8 updater, fake endpoint in cfg Update.TestApiUrl)
        if (Arg("--othlan-outage") is { } og)
            foreach (var part in og.Split(',', StringSplitOptions.RemoveEmptyEntries))
            { var f = part.Split(':'); Outages.Add((float.Parse(f[0], ci), float.Parse(f[1], ci))); }
        Outages.Sort((a, b) => a.t.CompareTo(b.t));
        OutShots.Sort((a, b) => a.t.CompareTo(b.t));
        if (Arg("--othlan-loss") is { } ls)   // t:pct,... : partial loss of Fusion + side channel from t on (0 = off)
            foreach (var part in ls.Split(',', StringSplitOptions.RemoveEmptyEntries))
            { var f = part.Split(':'); Losses.Add((float.Parse(f[0], ci), float.Parse(f[1], ci) / 100f)); }
        Losses.Sort((a, b) => a.t.CompareTo(b.t));
        if (Arg("--othlan-fnetem") is { } fn)
            foreach (var part in fn.Split(',', StringSplitOptions.RemoveEmptyEntries))
            { var f = part.Split(':'); FNetem.Add((float.Parse(f[0], ci), int.Parse(f[1], ci), int.Parse(f[2], ci), int.Parse(f[3], ci), int.Parse(f[4], ci))); }
        FNetem.Sort((a, b) => a.t.CompareTo(b.t));
        if (Outages.Count > 0 || Losses.Count > 0 || FNetem.Count > 0) OutageSim.Install();
        if (Arg("--othlan-lang") is { } lg)   // realtime t:code[:f7],... : switch the GAME's language (what Settings > Language does), screenshot 1.5 s later
            foreach (var part in lg.Split(',', StringSplitOptions.RemoveEmptyEntries))
            { var f = part.Split(':'); LangSteps.Add((float.Parse(f[0], ci), f[1], f.Length > 2 && f[2] == "f7")); }
        if (Arg("--othlan-demo") is { } demo)
        {   // README screenshots: placeholder IP in the mod UI, neutral lobby name, Steam name replaced in the game's own texts
            var parts = demo.Split(':');
            DemoName = parts[0]; NetInfo.DemoIp = parts.Length > 1 ? parts[1] : "26.10.20.30";
            CoopMenu.DemoName = parts.Length > 2 ? parts[2] : DemoName;
            try { _persona = Steamworks.SteamFriends.GetPersonaName(); } catch { _persona = null; }
            Plugin.L.LogInfo($"[Autopilot] demo mode: name '{DemoName}', ip {NetInfo.DemoIp}, lobby '{CoopMenu.DemoName}'");
        }
        if (Arg("--othlan-shots") is { } sh)
            foreach (var part in sh.Split(',', StringSplitOptions.RemoveEmptyEntries)) Shots.Add(float.Parse(part, ci));
        Shots.Sort(); Netem.Sort((a, b) => a.t.CompareTo(b.t));
        Directory.CreateDirectory(OutDir);
        _log = new StreamWriter(Path.Combine(OutDir, $"{Tag}-metrics.csv"), false, new UTF8Encoding(false)) { AutoFlush = true };
        _log.WriteLine("t,csLocal,csHostFlag,csEnabled,csApply,posesSent,posesRecv,posesRejected,teleportsIgnored,cfgEnabled,role,label,level,anchor,lx,ly,lw,lh,ix,iy,iw,ih,iconActive,screenW,screenH,echoMs,timedOut,loss,fusionRtt,netemRtt,netemLoss,carX,carY,carZ,speedKmh,driving,csSlot,csMode,csRtt,csErrM,csErrDeg,csPoseAge,snaps,blends,switches,hostPingsEchoed,softSnaps,cpSkipped,contactPauses,lastAction,contactBox,winchPauses,hostJumps");
        _trace = new StreamWriter(Path.Combine(OutDir, $"{Tag}-trace.csv"), false, new UTF8Encoding(false));
        _trace.WriteLine("t,rt,outage,role,running,tick,players,ownX,ownY,ownZ,ownKmh,ownState,resets,restores,accepts,lastErr,lastErrDeg,echoMs,timedOut,label,carsyncEnabled,hostFlag," +
                         "rSlot,rX,rY,rZ,rKmh,rSteer,rThrottle,rBrake,rInTick,csMode,csErr,csAction,snaps,blends,droppedOut,droppedIn");
        Plugin.L.LogWarning($"[Autopilot] ACTIVE tag={Tag} mode={Mode} out={OutDir} drive={DriveS}s quit={QuitS}s carsync={CarSyncMode ?? "config"} netem={Netem.Count} steps shots={Shots.Count}");
        try { InstallHooks(); } catch (Exception e) { Plugin.L.LogError($"[Autopilot] hooks failed (saves NOT suppressed -> aborting autopilot): {e}"); Active = false; Mode = null; }
    }

    // ---------------- hooks (dev only) ----------------
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void OnInputFn(IntPtr self, IntPtr runner, IntPtr input, IntPtr mi);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void StaticVoidFn(IntPtr mi);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void InstVoidFn(IntPtr self, IntPtr mi);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SaveJsonFn(IntPtr a, IntPtr b, IntPtr mi);
    static OnInputFn _inOrig, _inHook;
    static StaticVoidFn _sgOrig, _sgHook, _ssOrig, _ssHook;
    static InstVoidFn _fowOrig, _fowHook;
    static SaveJsonFn _sjOrig, _sjHook;
    static INativeDetour[] _detours;
    public static int SavesSuppressed, InputsInjected, HandbrakeReleases;

    // 0.1.8.1: addresses come from the manifest (optionalRva per build, optionalSig on an unverified build) through
    // OptionalHooks.Resolve, not from constants of one build. Missing/mismatching -> throw: the autopilot (self-test only,
    // --othlan-auto) aborts and nothing is hooked.
    static IntPtr Fn(Type t, string field, string key)
    {
        var fn = OptionalHooks.Resolve(t, field, key);
        if (fn == IntPtr.Zero) throw new InvalidOperationException($"{key}: {(OptionalHooks.Status.TryGetValue(key, out var st) ? st : "unavailable")}");
        return fn;
    }

    static void InstallHooks()
    {
        var pIn = Fn(typeof(VehicleInput), "NativeMethodInfoPtr_Fusion_INetworkRunnerCallbacks_OnInput_Private_Virtual_Final_New_Void_NetworkRunner_NetworkInput_0", "VehicleInput::OnInput(NetworkRunner,NetworkInput)");
        var pSg = Fn(typeof(OverlandSaver), "NativeMethodInfoPtr_SaveGame_Public_Static_Void_0", "OverlandSaver::SaveGame()");
        var pSs = Fn(typeof(OverlandSaver), "NativeMethodInfoPtr_SaveSettings_Public_Static_Void_0", "OverlandSaver::SaveSettings()");
        var pSj = Fn(typeof(OverlandSaver), "NativeMethodInfoPtr_SaveJson_Private_Static_Void_String_String_0", "OverlandSaver::SaveJson(string,string)");
        var pFow = Fn(typeof(FogOfWarManager), "NativeMethodInfoPtr_SaveFogOfWarTexture_Public_Void_0", "FogOfWarManager::SaveFogOfWarTexture()");
        _inHook = OnInputDetour;
        _sgHook = mi => { SavesSuppressed++; };
        _ssHook = mi => { SavesSuppressed++; };
        _sjHook = (a, b, mi) => { SavesSuppressed++; };
        _fowHook = (s, mi) => { SavesSuppressed++; };
        _detours = new[]
        {
            INativeDetour.CreateAndApply(pSg, _sgHook, out _sgOrig),
            INativeDetour.CreateAndApply(pSs, _ssHook, out _ssOrig),
            INativeDetour.CreateAndApply(pSj, _sjHook, out _sjOrig),
            INativeDetour.CreateAndApply(pFow, _fowHook, out _fowOrig),
            INativeDetour.CreateAndApply(pIn, _inHook, out _inOrig),
        };
        Plugin.L.LogWarning("[Autopilot] save writes suppressed for this process; scripted input hook installed");
    }

    /// <summary>NetworkInput is 16 bytes (uint* _ptr, int _wordCount) so x64 passes it by hidden pointer. Data = _ptr + 1 word;
    /// NetworkInputData: +0 InputFlags, +4 Steering, +8 Throttle, +0xC Brake.</summary>
    static void OnInputDetour(IntPtr self, IntPtr runner, IntPtr input, IntPtr mi)
    {
        // VehicleInput.handbrakeState (+0x80): 2 = ExternallyEngaged (set at spawn). The game releases it in
        // UpdateHandbrakeInput when throttle > 0 at that moment; our throttle is written after, so release it first.
        if (Driving && self != IntPtr.Zero && *(int*)((byte*)self + 0x80) == 2) { *(int*)((byte*)self + 0x80) = 0; HandbrakeReleases++; }
        _inOrig(self, runner, input, mi);
        if (!Driving || input == IntPtr.Zero) return;
        try
        {
            var ptr = *(uint**)input;
            if (ptr == null || *(int*)((byte*)input + 8) < 16) return;
            var data = (byte*)(ptr + 1);
            var t = _driveT;
            if (t < _reverseUntil)
            {   // stuck: back off (brake = reverse with the default auto forward/reverse behaviour), opposite lock
                *(float*)(data + 4) = -0.7f * Mathf.Sign(Mathf.Sin(t * 0.5f) + 0.01f);
                *(float*)(data + 8) = 0f;
                *(float*)(data + 0xC) = 0.9f;
            }
            else if (_ramming)
            {   // collision test: full lock toward the nearest other car, steady throttle
                *(float*)(data + 4) = _ramSteer;
                *(float*)(data + 8) = 0.7f;
                *(float*)(data + 0xC) = 0f;
            }
            else
            {
                *(float*)(data + 4) = 0.6f * Mathf.Sin(t * 0.5f);         // weave
                *(float*)(data + 8) = t % 20f < 16f ? 0.8f : 0f;           // throttle, coast 4 s of every 20
                *(float*)(data + 0xC) = 0f;
            }
            InputsInjected++;
        }
        catch { }
    }

    // ---------------- per frame ----------------
    static FusionPlayer _own; static float _nextFind;

    static Rigidbody FindOwnCar(float now)
    {
        if ((_own == null || _own.Object == null) && now >= _nextFind)
        {
            _nextFind = now + 1f; _own = null;
            try
            {
                if (CarRegistry.TryGetOwn(out var e) && e.Fp != null) _own = e.Fp;
            }
            catch { }
        }
        if (_own == null) return null;
        var rb = _own.rigidbody;
        if (rb == null && _own.vehicleBase != null) rb = _own.vehicleBase.cachedRigidbody;
        return rb;
    }

    /// <summary>FreezeWatch rejoined: back through lobby + spawn map, the scenario clock resumes where it was.</summary>
    public static void OnAutoRejoin()
    {
        if (!Active) return;
        Rejoins++;
        if (_t0 >= 0) _pausedT = Time.realtimeSinceStartup - _t0;
        _t0 = -1; _own = null;
    }

    public static void Tick()
    {
        if (!Active) return;
        var now = Time.realtimeSinceStartup;
        Application.runInBackground = true;
        if (now > 20 * 60) { Finish("hard cap 20 min"); return; }
        RealtimeActions(now);
        if (_finished) return;
        var rb = LanState.Active != LanRole.Off ? FindOwnCar(now) : null;
        if (_t0 < 0)
        {
            // client: wait for the spawn (lobby -> map -> spawn teleport); c4: with 4 games on one PC the lobby came seconds
            // after the car appeared, the scenario had already started and nobody pressed Join -> the parked car fell
            if (rb != null && LanState.Active == LanRole.Client && !OthLan.OwnCar.SpawnSeen) { if (_carSeenAt < 0) _carSeenAt = now; }
            else if (rb == null) _carSeenAt = -1;
            var spawnOk = LanState.Active != LanRole.Client || OthLan.OwnCar.SpawnSeen || (_carSeenAt >= 0 && now - _carSeenAt > 45);
            if (rb != null && !InLobby() && spawnOk) { _carSeenAt = -1; _t0 = now - Math.Max(0, _pausedT); Plugin.L.LogWarning($"[Autopilot] own car found at {now:0.0}s; scenario clock starts at {now - _t0:0.0}"); _pausedT = -1; }
            else
            {
                if (LanState.Active != LanRole.Off && now >= _nextLobbyPoke) { _nextLobbyPoke = now + 3f; PokeLobby(); }
                if (now - _lastStatusLog > 10) { _lastStatusLog = now; Plugin.L.LogInfo($"[Autopilot] waiting: active={LanState.Active} status={LanState.LastStatus}"); }
                if (now > Delay + 240 + Rejoins * 240 && _menuQuit < 0) Finish("never got into the world");
                return;
            }
        }
        var t = now - _t0;
        _driveT = t;
        Driving = t < DriveS;
        if (Driving && rb != null && t > 5 && t >= _reverseUntil && t % 20f < 16f)
        {
            if (rb.linearVelocity.magnitude * 3.6f < 2f) { if (_slowSince < 0) _slowSince = t; else if (t - _slowSince > 3f) { _reverseUntil = t + 2.5f; _slowSince = -1; Unstucks++; } }
            else _slowSince = -1;
        }

        _ramming = Driving && rb != null && _ramFrom >= 0 && t >= _ramFrom && t < _ramTo;
        if (_ramming)
        {
            if (now >= _nextRamScan) { _nextRamScan = now + 1f; _ramCars = CarRegistry.Players; }
            FusionPlayer best = null; float bd = float.MaxValue;
            foreach (var fp in _ramCars)
            {
                try { if (fp == null || fp.WasCollected || fp.Object == null || fp.Object.HasInputAuthority) continue;
                      var d = Vector3.Distance(fp.transform.position, rb.position); if (d < bd) { bd = d; best = fp; } } catch { }
            }
            if (best != null)
            {
                var local = rb.transform.InverseTransformPoint(best.transform.position);
                _ramSteer = Mathf.Clamp(Mathf.Atan2(local.x, local.z) / 0.5f, -1f, 1f);
                if (_ramDist >= 6f && bd < 6f) { RamContacts++; Plugin.L.LogWarning($"[Autopilot] t={t:0.0}s ram: closing in on {best.gameObject.name} ({bd:0.0} m, {rb.linearVelocity.magnitude * 3.6f:0} km/h)"); }
                _ramDist = bd;
            }
            else _ramSteer = 0;
        }
        while (_nextNetem < Netem.Count && Netem[_nextNetem].t <= t)
        {
            var n = Netem[_nextNetem++];
            ClientLink.NetemRttMs = n.rtt; ClientLink.NetemLoss = n.loss;
            Plugin.L.LogWarning($"[Autopilot] t={t:0.0}s netem: +{n.rtt} ms RTT, {n.loss * 100:0}% loss per direction");
        }
        while (_nextToggle < Toggles.Count && Toggles[_nextToggle].t <= t)
        {
            var want = Toggles[_nextToggle++].on;
            if (CarSync.LocalSetting != want) CarSync.UserToggle("autopilot (same call as the F7 button)");
            Plugin.L.LogWarning($"[Autopilot] t={t:0.0}s toggle -> {(want ? "on" : "off")}: local={CarSync.LocalSetting} cfg={Plugin.CarSyncEnabled.Value}");
        }
        while (_nextLight < Lights.Count && Lights[_nextLight].t <= t)
        {
            var want = Lights[_nextLight++].on;
            try
            {
                var vi = _own?.GetComponentInChildren<VehicleInput>() ?? UnityEngine.Object.FindObjectOfType<VehicleInput>();
                if (vi != null)
                {
                    var cur = vi.HeadlightsOn;
                    if (cur != want) { vi.ToggleHeadlights(); LightToggles++; }
                    Plugin.L.LogWarning($"[Autopilot] t={t:0.0}s headlights want={want} was={cur} now={vi.HeadlightsOn}");
                }
                else Plugin.L.LogWarning($"[Autopilot] t={t:0.0}s headlights: no VehicleInput");
            }
            catch (Exception e) { Plugin.L.LogWarning($"[Autopilot] headlights: {e.Message}"); }
        }
        if (Lights.Count > 0) SampleRemoteLights(t);
        while (_nextCpMin < CpMins.Count && CpMins[_nextCpMin].t <= t)
        {
            CarSync.CheckpointMinOverride = CpMins[_nextCpMin++].v;
            Plugin.L.LogWarning($"[Autopilot] t={t:0.0}s checkpoint-min override = {CarSync.CheckpointMinOverride}");
        }
        if (_rejoinAt >= 0 && t >= _rejoinAt && LanState.Active == LanRole.Client)
        {
            _rejoinAt = -1; Rejoins++;
            Plugin.L.LogWarning($"[Autopilot] t={t:0.0}s rejoin test: leaving the session (slot {LanState.ClientSlot}, {LanState.SlotSource})");
            try { OverlandNetworkRunner.Instance?.QuitToMenuAsync(MainMenuContext.Reason.PlayerChoice, "OTH-LAN self-test rejoin"); } catch (Exception e) { Plugin.L.LogWarning($"[Autopilot] quit for rejoin: {e.Message}"); }
            Overlay.RearmAutoTestForRejoin();
            _t0 = -1; _own = null;   // back through lobby + spawn map after the rejoin (lobby pokes only run while _t0 < 0)
        }
        if (_hostJumpAt >= 0 && t >= _hostJumpAt)
        {
            _hostJumpAt = -1;
            foreach (var k in CarSync.Metrics.Keys) { CarSync.TestJumpSlot = k; break; }
            Plugin.L.LogWarning($"[Autopilot] t={t:0.0}s host jump test on slot {CarSync.TestJumpSlot}");
        }
        if (_winchAt >= 0 && t >= _winchAt) { _winchAt = -1; WinchAttach(t); _winchCheckAt = t + 2; }
        if (_winchCheckAt >= 0 && t >= _winchCheckAt)
        {
            _winchCheckAt = -1;
            try { var w = CarSync.OwnWinch(out _); Plugin.L.LogWarning($"[Autopilot] t={t:0.0}s winch state: attached={w?.WinchIsAttached} state={w?.WinchState} tension={(w != null ? w.Tension.ToString("0") : "-")}"); }
            catch (Exception e) { Plugin.L.LogWarning($"[Autopilot] winch state read failed: {e.Message}"); }
        }
        if (_winchOffAt >= 0 && t >= _winchOffAt)
        {
            _winchOffAt = -1;
            try { CarSync.OwnWinch(out _)?.RPC_DetachWinchAndDisableWinchPoints(); Plugin.L.LogWarning($"[Autopilot] t={t:0.0}s winch detach requested"); }
            catch (Exception e) { Plugin.L.LogWarning($"[Autopilot] winch detach failed: {e.Message}"); }
        }
        while (_nextBox < ContactBoxes.Count && ContactBoxes[_nextBox].t <= t)
        {
            CarSync.ContactBoxOverride = ContactBoxes[_nextBox++].box;
            Plugin.L.LogWarning($"[Autopilot] t={t:0.0}s contact box override = {CarSync.ContactBoxOverride} m");
        }
        if (_nextF7 < F7Shots.Count && F7Shots[_nextF7] <= t && _f7ShotAt < 0)
        {
            Overlay.SetShownForTest(true);
            _f7Name = Path.Combine(OutDir, $"{Tag}-f7-t{F7Shots[_nextF7]:000}.png"); _nextF7++;
            _f7ShotAt = t + 0.6f; _f7HideAt = t + 1.5f;
        }
        if (_f7ShotAt >= 0 && t >= _f7ShotAt)
        {
            ScreenCapture.CaptureScreenshot(_f7Name); Plugin.L.LogInfo($"[Autopilot] F7 screenshot {_f7Name}"); _f7ShotAt = -1;
            if (PingHud.LastAnchor != "none")   // only meaningful in a LAN session
            { var ok = PingHud.LastAnchor == "f7-open"; (ok ? ref _passed : ref _failed)++; Checks.Add($"{(ok ? "PASS" : "FAIL")} t={t:0} HUD label hidden while F7 is open (anchor={PingHud.LastAnchor})"); }
        }
        if (_f7HideAt >= 0 && t >= _f7HideAt && _f7ShotAt < 0) { Overlay.SetShownForTest(false); _f7HideAt = -1; }
        if (_nextShot < Shots.Count && Shots[_nextShot] <= t)
        {
            var path = Path.Combine(OutDir, $"{Tag}-t{Shots[_nextShot]:000}.png");
            _nextShot++;
            ScreenCapture.CaptureScreenshot(path);
            Plugin.L.LogInfo($"[Autopilot] screenshot {path} label='{PingHud.LastText}' {PingHud.LastLevel} anchor={PingHud.LastAnchor} rect={R(PingHud.LastLabel)} icon={R(PingHud.LastIcon)}");
            CheckLabel(t);
        }
        // An outage waits (max 20 s) until the own car moves > 8 km/h: outages "at speed" must not hit a stuck car.
        while (_nextOutage < Outages.Count && Outages[_nextOutage].t + _outageDelay <= t && _outageEnd < 0)
        {
            var kmh = rb != null ? rb.linearVelocity.magnitude * 3.6f : 0f;
            if (kmh < 8f && t - Outages[_nextOutage].t - _outageDelay < 20f && DriveS > 0) break;
            var o = Outages[_nextOutage++]; _outageEnd = t + o.dur; OutageSim.Active = true;
            _outageDelay = t - o.t;   // later outages keep their spacing
            OutShots.Add((t + o.dur * 0.5f, $"out-t{o.t:000}-mid")); OutShots.Add((t + o.dur + 0.5f, $"out-t{o.t:000}-after"));
            OutShots.Sort((a, b) => a.t.CompareTo(b.t));
            Plugin.L.LogWarning($"[Autopilot] t={t:0.00}s OUTAGE start ({o.dur:0.0} s, planned {o.t:0}, at {kmh:0} km/h) own={OthLan.OwnCar.Describe()}");
        }
        while (_nextLoss < Losses.Count && Losses[_nextLoss].t <= t)
        {
            OutageSim.Loss = Losses[_nextLoss++].loss;
            Plugin.L.LogWarning($"[Autopilot] t={t:0.00}s LOSS {OutageSim.Loss * 100:0}% per direction (Fusion + side channel); lost so far out={OutageSim.LostOut} in={OutageSim.LostIn}");
        }
        while (_nextFNetem < FNetem.Count && FNetem[_nextFNetem].t <= t)
        {
            var f = FNetem[_nextFNetem++];
            OutageSim.DelayMs = f.delay; OutageSim.JitterMs = f.jitter; OutageSim.StallEveryMs = f.every; OutageSim.StallMs = f.stall;
            Plugin.L.LogWarning($"[Autopilot] t={t:0.00}s FUSION NETEM delay {f.delay} ms + jitter {f.jitter} ms per direction, incoming stall {f.stall} ms every {f.every} ms; {NetStats.Describe()}");
        }
        OutageSim.Pump();
        if (_outageEnd >= 0 && t >= _outageEnd)
        {
            OutageSim.Active = false; _outageEnd = -1;
            Plugin.L.LogWarning($"[Autopilot] t={t:0.00}s OUTAGE end; dropped out={OutageSim.DroppedOut} in={OutageSim.DroppedIn}");
        }
        while (OutShots.Count > 0 && OutShots[0].t <= t)
        {
            var path = Path.Combine(OutDir, $"{Tag}-{OutShots[0].name}.png"); OutShots.RemoveAt(0);
            ScreenCapture.CaptureScreenshot(path);
            Plugin.L.LogInfo($"[Autopilot] outage screenshot {path}");
        }
        if (now >= _nextTrace) { _nextTrace = now + 0.05f; Trace(t, now, rb); }
        if (now >= _nextRow) { _nextRow = now + 0.5f; Row(t, rb); try { _trace?.Flush(); } catch { } }
        if (t > QuitS) Finish("scenario done");
    }

    /// <summary>Things on the realtime clock (before/without the world): the version dialog, menu F7 shots, gate-only quit.</summary>
    static float _rtBase = -1;
    static void RealtimeActions(float now)
    {
        // Times are seconds since the plugin's first frame (a first start can spend minutes in BepInEx's interop
        // generation before that, which realtimeSinceStartup may or may not include).
        if (_rtBase < 0) { _rtBase = now; Plugin.L.LogInfo($"[Autopilot] realtime base {now:0.0}s"); }
        now -= _rtBase;
        if (_gateAt >= 0 && now >= _gateAt && _gateShotDone < 0)
        {
            var path = Path.Combine(OutDir, $"{Tag}-gate.png");
            var visible = BuildGate.NeedsChoice && Overlay.GateDialogFrame >= Time.frameCount - 5;
            (visible ? ref _passed : ref _failed)++;
            Checks.Add($"{(visible ? "PASS" : "FAIL")} realtime {now:0}s version dialog visible (tier={BuildGate.Tier} outcome={BuildGate.Outcome})");
            if (BuildGate.NeedsChoice) ScreenCapture.CaptureScreenshot(path);
            Plugin.L.LogWarning($"[Autopilot] gate: dialog visible={visible} tier={BuildGate.Tier} outcome={BuildGate.Outcome}; screenshot {path}");
            _gateShotDone = now + 1.5f;
        }
        if (_gateShotDone >= 0 && now >= _gateShotDone && _gateChoice != null)
        {
            var c = _gateChoice; _gateChoice = null;
            if (BuildGate.NeedsChoice) BuildGate.UserChoice(c == "continue", "autopilot (same call as the dialog button)");
            Checks.Add($"INFO realtime {now:0}s gate answered '{c}': outcome={BuildGate.Outcome} lanDisabled={LanState.Disabled} hooks={FusionHooks.Applied} saved='{Plugin.UnverifiedChoices.Value}'");
        }
        if (_nextMenuF7 < MenuF7.Count && MenuF7[_nextMenuF7] <= now && _menuShotAt < 0)
        {
            Overlay.SetShownForTest(true);
            _menuName = Path.Combine(OutDir, $"{Tag}-menu-f7-t{MenuF7[_nextMenuF7]:000}.png"); _nextMenuF7++;
            _menuShotAt = now + 0.8f; _menuHideAt = now + 1.8f;
        }
        if (_menuShotAt >= 0 && now >= _menuShotAt)
        {
            ScreenCapture.CaptureScreenshot(_menuName); _menuShotAt = -1;
            Checks.Add($"INFO realtime {now:0}s F7 shot {Path.GetFileName(_menuName)}: status='{BuildGate.Status()}' lanDisabled={LanState.Disabled} tier={BuildGate.Tier} outcome={BuildGate.Outcome}");
        }
        if (_menuHideAt >= 0 && now >= _menuHideAt && _menuShotAt < 0) { Overlay.SetShownForTest(false); _menuHideAt = -1; }
        if (_menuQuit >= 0 && now >= _menuQuit) Finish("menu quit (gate-only run)");
        if (CoopAction != null) CoopSteps(now);
        if (UpdAction != null) UpdSteps(now);
        ModGateTick(now);
        LangTick(now);
        Scrub(now);
    }

    /// <summary>Pre-join missing-mods dialog: screenshot, then Cancel or Download (self-test).</summary>
    static void ModGateTick(float now)
    {
        if (string.IsNullOrEmpty(ModGateAction) || !ModLiveryGate.Active) return;
        if (_modGateAt < 0) { _modGateAt = now + 1.5f; Plugin.L.LogWarning($"[Autopilot] mod gate visible → {ModGateAction} at rt {_modGateAt:0.0}s"); }
        if (!_modGateShot && now >= _modGateAt)
        {
            var path = Path.Combine(OutDir, $"{Tag}-modgate.png");
            ScreenCapture.CaptureScreenshot(path);
            _modGateShot = true;
            Checks.Add($"PASS realtime {now:0}s mod gate dialog (action={ModGateAction}) shot {Path.GetFileName(path)}");
            Plugin.L.LogWarning($"[Autopilot] mod gate screenshot {path}");
            _modGateAt = now + 1.0f;
            return;
        }
        if (_modGateShot && now >= _modGateAt)
        {
            var act = ModGateAction; ModGateAction = null;
            if (act.Equals("download", StringComparison.OrdinalIgnoreCase)) ModLiveryGate.StartDownload();
            else if (act.Equals("joinwithout", StringComparison.OrdinalIgnoreCase) || act.Equals("skip", StringComparison.OrdinalIgnoreCase))
                ModLiveryGate.JoinWithoutMods();
            else ModLiveryGate.Cancel();
            Checks.Add($"INFO realtime {now:0}s mod gate answered '{act}'");
        }
    }

    // ---------- 0.1.8 updater ----------
    public static string UpdAction;
    static int _updStep; static float _updAt = -1;
    static void UpdShot(string name) { var p = Path.Combine(OutDir, $"{Tag}-upd-{name}.png"); ScreenCapture.CaptureScreenshot(p); Plugin.L.LogWarning($"[Autopilot] update screenshot {p}"); }
    static void UpdCheck(bool ok, string what) { (ok ? ref _passed : ref _failed)++; Checks.Add($"{(ok ? "PASS" : "FAIL")} update: {what}"); Plugin.L.LogWarning($"[Autopilot] update {(ok ? "PASS" : "FAIL")}: {what}"); }
    static void UpdSteps(float now)
    {
        if (now < Delay) return;
        var a = UpdAction;
        if (_updStep == 0) { _updAt = now; _updStep = 1; Checks.Add($"INFO update test '{a}' running {Plugin.Version}"); return; }
        if (a == "uptodate")
        {
            var done = Updater.State is Updater.St.Offline or Updater.St.UpToDate or Updater.St.Failed or Updater.St.Available;
            if (!done && now < _updAt + 45) return;
            UpdCheck(Updater.State == Updater.St.UpToDate, $"state {Updater.State} ({Updater.Detail}), expected UpToDate");
            UpdCheck(!Updater.PromptOpen && Updater.PromptFrame < 0, "no prompt");
            UpdCheck(CoopMenu.MainMenuVisible, "main menu still usable (Local Co-op button visible)");
            Overlay.SetShownForTest(true); _updStep = 50; _updAt = now + 1f; UpdAction = "_f7"; return;
        }
        if (a == "offline")
        {
            // 0.1.9.1: offline is no longer silent — main-menu notice with Retry / Later
            if (_updStep == 1)
            {
                if (Updater.State != Updater.St.Offline && now < _updAt + 45) return;
                if (Updater.State != Updater.St.Offline)
                { UpdCheck(false, $"state {Updater.State} ({Updater.Detail}), expected Offline"); UpdAction = null; Finish("update test failed"); return; }
                if (!Updater.PromptOpen)
                {
                    if (now > _updAt + 60) { UpdCheck(false, $"offline prompt never shown ({Updater.Detail})"); UpdAction = null; Finish("update test failed"); }
                    return;
                }
                UpdCheck(true, $"state Offline ({Updater.Detail})");
                UpdCheck(Updater.TestButtons() == "upd.retry,upd.later", $"offline buttons {Updater.TestButtons()}");
                _updStep = 2; _updAt = now + 1.5f; return;
            }
            if (_updStep == 2)
            {
                if (now < _updAt) return;
                UpdShot("offline"); _updStep = 21; _updAt = now + 0.5f; return;
            }
            if (_updStep == 21)
            {
                if (now < _updAt) return;
                Updater.TestPress("upd.later");
                UpdCheck(!Updater.PromptOpen, "offline prompt closed after Later");
                UpdCheck(CoopMenu.MainMenuVisible, "main menu still usable (Local Co-op button visible)");
                Overlay.SetShownForTest(true); _updStep = 50; _updAt = now + 1f; UpdAction = "_f7"; return;
            }
        }
        if (a == "_f7") { if (now < _updAt) return; UpdShot("f7"); Overlay.SetShownForTest(false); UpdAction = null; Finish("update test done"); return; }
        switch (_updStep)
        {
            case 1:
                if (!Updater.PromptOpen) { if (now > _updAt + 60) { UpdCheck(false, $"prompt never shown (state {Updater.State}: {Updater.Detail})"); UpdAction = null; Finish("update test failed"); } return; }
                _updStep = 2; _updAt = now + 1.5f; return;
            case 2:
                // ScreenCapture writes at the end of the frame: shoot first, act half a second later (0.1.8 u19: the
                // "prompt" and "failed" shots showed the state after the button press)
                if (now < _updAt) return;
                UpdShot("prompt"); _updStep = 21; _updAt = now + 0.5f; return;
            case 21:
                if (now < _updAt) return;
                UpdCheck(Updater.TestButtons() == "upd.install,upd.later,upd.skip", $"buttons {Updater.TestButtons()}");
                UpdCheck(Updater.TestFocus == 0, $"keyboard/gamepad focus on the first button ({Updater.TestFocus})");
                bool esOff; try { esOff = UnityEngine.EventSystems.EventSystem.current == null || !UnityEngine.EventSystems.EventSystem.current.enabled; } catch { esOff = true; }
                UpdCheck(esOff, "game menu navigation paused behind the prompt");
                if (a == "skip") { Updater.TestPress("upd.skip"); UpdCheck(Plugin.UpdateSkipped.Value == Updater.Latest?.Version, $"skipped version saved '{Plugin.UpdateSkipped.Value}'"); UpdAction = null; Finish("update test done"); return; }
                Updater.TestPress("upd.install"); _updStep = 3; _updAt = now; return;
            case 3:
                if (Updater.State == Updater.St.Downloading && now < _updAt + 240) return;
                _updStep = 31; _updAt = now + 0.5f; return;   // let the prompt redraw in its new state
            case 31:
                if (now < _updAt) return;
                UpdShot(Updater.State == Updater.St.Ready ? "ready" : "failed"); _updStep = 32; _updAt = now + 0.5f; return;
            case 32:
                if (now < _updAt) return;
                UpdCheck(Updater.PromptOpen, $"prompt shows the result ({Updater.State})");
                if (a == "install")
                {
                    UpdCheck(Updater.State == Updater.St.Ready, $"downloaded + verified ({Updater.State}: {Updater.Detail})");
                    UpdCheck(Updater.Started == 1, $"updater started ({Updater.Started})");
                    UpdCheck(Updater.TestButtons() == "upd.quitUpdate,upd.later", $"ready buttons {Updater.TestButtons()}");
                    UpdAction = null; Finish("update: quit and update"); return;
                }
                UpdCheck(Updater.State == Updater.St.Failed && Updater.Detail.Contains("sha256 mismatch"), $"bad hash refused ({Updater.State}: {Updater.Detail})");
                UpdCheck(Updater.TestButtons() == "upd.retry,upd.later", $"failed buttons {Updater.TestButtons()}");
                UpdCheck(Updater.Started == 0, $"nothing started ({Updater.Started})");
                Updater.TestPress("upd.later");
                UpdCheck(!Updater.PromptOpen && CoopMenu.MainMenuVisible, "prompt closed, menu usable");
                UpdAction = null; Finish("update test done"); return;
        }
    }

    // ---------- language switching (i18n) ----------
    static readonly List<(float t, string code, bool f7)> LangSteps = new();
    static int _nextLang; static float _langShotAt = -1, _langHideAt = -1; static string _langShotName, _langWant; static bool _langF7;
    static void LangTick(float now)
    {
        if (_langShotAt >= 0 && now >= _langShotAt)
        {
            _langShotAt = -1;
            ScreenCapture.CaptureScreenshot(_langShotName);
            var want = Loc.Resolve(_langWant);
            var ok = Loc.Lang == want;
            var detail = $"game '{LangWatch.GameCode}' mod '{Loc.Lang}' coopButton='{CoopMenu.ButtonLabel}' coopOpen={CoopMenu.Open} label='{PingHud.LastText}' status='{LanState.LastStatus}' f7={_langF7}";
            (ok ? ref _passed : ref _failed)++;
            Checks.Add($"{(ok ? "PASS" : "FAIL")} realtime {now:0}s language -> {_langWant}: {detail}; shot {Path.GetFileName(_langShotName)}");
            if (CoopMenu.ButtonLabel.Length > 0)
            {
                var bok = CoopMenu.ButtonLabel.ToLowerInvariant() == Loc.TIn(want, "coop.button").ToLowerInvariant();
                (bok ? ref _passed : ref _failed)++; Checks.Add($"{(bok ? "PASS" : "FAIL")} realtime {now:0}s Local Co-op button in {want}: '{CoopMenu.ButtonLabel}'");
            }
            if (!_langF7 && PingHud.LastAnchor == "icon" && PingHud.LastFrame >= Time.frameCount - 5)
            {   // in-session ping label must be in the new language too (unit word of the ping.* table)
                // markers come from the tables: unit word of ping.local, words before the number of ping.noLink / ping.loss
                string[] M(string l) => new[] { " " + Loc.TIn(l, "ping.local").Split(' ')[^1], Loc.TIn(l, "ping.noLink").Split(" {")[0], Loc.TIn(l, "ping.loss").Split(" {")[0].Trim() };
                string[] ruM = M("ru"), enM = M("en");
                var txt = PingHud.LastText ?? "";
                bool Has(string[] m) { foreach (var x in m) if (txt.Contains(x)) return true; return false; }
                var lok = txt == "local --" || (want == "ru" ? Has(ruM) && !Has(enM) : Has(enM) && !Has(ruM));
                (lok ? ref _passed : ref _failed)++; Checks.Add($"{(lok ? "PASS" : "FAIL")} realtime {now:0}s ping label in {want}: '{PingHud.LastText}'");
            }
            Plugin.L.LogWarning($"[Autopilot] lang shot {_langShotName}: {detail}");
            if (_langF7) _langHideAt = now + 0.8f;   // CaptureScreenshot grabs the END of this frame: keep F7 up a bit longer
            return;
        }
        if (_langHideAt >= 0 && now >= _langHideAt) { _langHideAt = -1; Overlay.SetShownForTest(false); }
        if (_nextLang >= LangSteps.Count || _langShotAt >= 0 || _langHideAt >= 0 || LangSteps[_nextLang].t > now) return;
        var (t, code, f7) = LangSteps[_nextLang++];
        _langWant = code; _langF7 = f7;
        try
        {
            var locs = UnityEngine.Localization.Settings.LocalizationSettings.AvailableLocales.Locales;
            UnityEngine.Localization.Locale pick = null;
            for (int i = 0; i < locs.Count; i++) if (Loc.Normalize(locs[i].Identifier.Code) == Loc.Normalize(code)) { pick = locs[i]; break; }
            if (pick == null) { Checks.Add($"INFO realtime {now:0}s language {code}: the game has no such locale"); return; }
            UnityEngine.Localization.Settings.LocalizationSettings.SelectedLocale = pick;
            Plugin.L.LogWarning($"[Autopilot] realtime {now:0.0}s: game language set to {pick.Identifier.Code} (LocalizationSettings.SelectedLocale, as the game's language menu does)");
        }
        catch (Exception e) { Checks.Add($"INFO realtime {now:0}s language {code}: {e.Message}"); return; }
        if (f7) Overlay.SetShownForTest(true);
        _langShotName = Path.Combine(OutDir, $"{Tag}-lang-t{t:000}-{code}{(f7 ? "-f7" : "")}.png");
        _langShotAt = now + 1.5f;
    }

    // ---------- Local Co-op screen (0.1.7) ----------
    public static string CoopAction;
    static int _coopStep, _coopPick; static float _coopAt = -1, _coopDeadline; static string _coopLastState; static int _coopSubmits;
    static readonly HashSet<string> _coopShots = new();
    static void CoopShot(string name) { var p = Path.Combine(OutDir, $"{Tag}-coop-{name}.png"); ScreenCapture.CaptureScreenshot(p); Plugin.L.LogWarning($"[Autopilot] coop screenshot {p}"); }
    static void CoopCheck(bool ok, string what) { (ok ? ref _passed : ref _failed)++; Checks.Add($"{(ok ? "PASS" : "FAIL")} coop: {what}"); Plugin.L.LogWarning($"[Autopilot] coop {(ok ? "PASS" : "FAIL")}: {what}"); }
    static string TopState() { try { return FunCore.Managers.HybridSingleton<FunCore.Managers.GameStateManager>.Instance?.PeekState()?.GetIl2CppType()?.Name ?? "null"; } catch { return "?"; } }

    static bool _modsBefore;
    static GameObject Sel() { try { return UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject; } catch { return null; } }
    static void Select(string what) { var go = CoopMenu.TestObj(what); try { UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(go); } catch { } }
    static void NavCheck(string what, string expect)
    {
        var want = CoopMenu.TestObj(expect); var got = Sel();
        CoopCheck(want != null && got != null && got.Pointer == want.Pointer, $"{what} (selected '{got?.name ?? "none"}', expected '{want?.name ?? "?"}')");
    }
    static void Move(UnityEngine.EventSystems.MoveDirection d)
    {
        var es = UnityEngine.EventSystems.EventSystem.current; var go = es?.currentSelectedGameObject; if (go == null) return;
        var v = d switch { UnityEngine.EventSystems.MoveDirection.Up => new Vector2(0, 1), UnityEngine.EventSystems.MoveDirection.Down => new Vector2(0, -1), UnityEngine.EventSystems.MoveDirection.Left => new Vector2(-1, 0), _ => new Vector2(1, 0) };
        var ev = new UnityEngine.EventSystems.AxisEventData(es) { moveDir = d, moveVector = v };
        UnityEngine.EventSystems.ExecuteEvents.Execute<UnityEngine.EventSystems.IMoveHandler>(go, ev, UnityEngine.EventSystems.ExecuteEvents.moveHandler);
    }
    static void Submit()
    {
        var es = UnityEngine.EventSystems.EventSystem.current; var go = es?.currentSelectedGameObject; if (go == null) return;
        UnityEngine.EventSystems.ExecuteEvents.Execute<UnityEngine.EventSystems.ISubmitHandler>(go, new UnityEngine.EventSystems.BaseEventData(es), UnityEngine.EventSystems.ExecuteEvents.submitHandler);
    }

    static void CoopSteps(float now)
    {
        if (_coopStep == 0)
        {
            if (now < Delay) return;
            if (CoopMenu.State == "button") { _coopStep = 1; _coopAt = now + 2f; }
            else if (now > Delay + 90) { CoopCheck(false, $"main-menu button never appeared (state {CoopMenu.State})"); CoopAction = null; }
            return;
        }
        if (now < _coopAt) return;
        switch (_coopStep)
        {
            case 1:
                CoopShot("menu"); CoopCheck(CoopMenu.ButtonLabel.ToLowerInvariant().Contains(Loc.T("coop.button").ToLowerInvariant()), $"Local Co-op button in the main menu (label '{CoopMenu.ButtonLabel}')");
                _coopStep = 20; _coopAt = now + 1.5f; return;
            // ---- 0.1.8: keyboard / gamepad navigation, through the same Move/Submit events the game's UI input module sends
            case 20:
                Select("multiplayer"); _coopStep = 21; _coopAt = now + 0.5f; return;
            case 21:
                NavCheck("main menu: Multiplayer selected", "multiplayer");
                Move(UnityEngine.EventSystems.MoveDirection.Down); _coopStep = 22; _coopAt = now + 0.5f; return;
            case 22:
                NavCheck("main menu: Down from Multiplayer -> Local Co-op", "coop"); CoopShot("nav-selected");
                Move(UnityEngine.EventSystems.MoveDirection.Down); _coopStep = 23; _coopAt = now + 0.5f; return;
            case 23:
                NavCheck("main menu: Down from Local Co-op -> next game button", "afterCoop");
                Move(UnityEngine.EventSystems.MoveDirection.Up); _coopStep = 24; _coopAt = now + 0.5f; return;
            case 24:
                NavCheck("main menu: Up -> Local Co-op again", "coop");
                Submit(); _coopStep = 25; _coopAt = now + 2f; return;
            case 25:
                CoopCheck(CoopMenu.Open, "Submit (Enter / gamepad A) on Local Co-op opens the screen");
                if (!CoopMenu.Open) { CoopMenu.OpenScreen(); _coopStep = 2; _coopAt = now + 7f; _coopDeadline = now + 70f; return; }
                NavCheck("screen: 'Become host' selected first", "host");
                Move(UnityEngine.EventSystems.MoveDirection.Left); _coopStep = 26; _coopAt = now + 0.5f; return;
            case 26:
                NavCheck("screen: Left from 'Become host' -> Mods", "mods");
                _modsBefore = Plugin.AllowMods.Value; Submit(); _coopStep = 27; _coopAt = now + 0.5f; return;
            case 27:
                CoopCheck(Plugin.AllowMods.Value != _modsBefore, $"screen: Submit on Mods toggles it ({_modsBefore} -> {Plugin.AllowMods.Value})"); CoopShot("mods-toggled");
                Submit(); _coopStep = 28; _coopAt = now + 0.5f; return;
            case 28:
                CoopCheck(Plugin.AllowMods.Value == _modsBefore, $"screen: Submit again restores it ({Plugin.AllowMods.Value})");
                Move(UnityEngine.EventSystems.MoveDirection.Left); _coopStep = 29; _coopAt = now + 0.5f; return;
            case 29:
                NavCheck("screen: Left -> Players", "players");
                Move(UnityEngine.EventSystems.MoveDirection.Up); _coopStep = 30; _coopAt = now + 0.5f; return;
            case 30:
                CoopCheck(Sel() != null && Sel().name.StartsWith("Row"), $"screen: Up from the bar -> a lobby row ('{Sel()?.name}')");
                Move(UnityEngine.EventSystems.MoveDirection.Down); _coopStep = 31; _coopAt = now + 0.5f; return;
            case 31:
                NavCheck("screen: Down from the last row -> 'Become host'", "host");
                Move(UnityEngine.EventSystems.MoveDirection.Right); _coopStep = 32; _coopAt = now + 0.5f; return;
            case 32:
                NavCheck("screen: Right -> Add by IP", "add");
                Move(UnityEngine.EventSystems.MoveDirection.Right); _coopStep = 33; _coopAt = now + 0.5f; return;
            case 33:
                NavCheck("screen: Right -> Back", "back"); CoopShot("nav-screen");
                Move(UnityEngine.EventSystems.MoveDirection.Left); Move(UnityEngine.EventSystems.MoveDirection.Left);
                _coopStep = 2; _coopAt = now + 5f; _coopDeadline = now + 70f; return;
            case 5:
                CoopMenu.OpenScreen(); _coopStep = 2; _coopAt = now + 7f; _coopDeadline = now + 70f; return;
            case 2:
            {
                var list = Discovery.Snapshot();
                Plugin.L.LogWarning($"[Autopilot] coop list ({list.Count}): {string.Join(" | ", list.Select(l => $"{l.Name} {l.Endpoint} {l.Cur}/{l.Max} {l.PingMs:0}ms via {l.Via} mod {l.Mod}"))}");
                if (!_coopShots.Contains("screen")) { _coopShots.Add("screen"); CoopShot("screen"); CoopCheck(CoopMenu.Open, "Local Co-op screen opened"); }
                var a = CoopAction;
                if (a == "look") { if (now > _coopDeadline - 50) { CoopShot("screen-late"); CoopMenu.Close(true); _coopStep = 99; } else _coopAt = now + 3f; return; }
                if (a.StartsWith("host"))
                {
                    var n = a.Contains(':') ? int.Parse(a.Split(':')[1]) : 4;
                    CoopMenu.TestPlayers(n); _coopAt = now + 1f; _coopStep = 3; CoopShot("players"); return;
                }
                // join / joinfull: wait for a lobby
                var pick = list.FindIndex(l => DiscoveryLogic.Check(l, Plugin.Version, BuildGate.BuildKey, BuildGate.GaHash) == DiscoveryLogic.Compat.Ok);
                if (pick < 0) { if (now > _coopDeadline) { CoopCheck(false, "no lobby found in 70 s"); CoopShot("nolobby"); CoopAction = null; } else _coopAt = now + 2f; return; }
                CoopShot("list");
                var lb = list[pick];
                CoopCheck(lb.Name.Length > 0 && lb.Max >= 2 && !double.IsNaN(lb.PingMs), $"lobby row '{lb.Name}' {lb.Cur}/{lb.Max} ping {lb.PingMs:0} ms via {lb.Via}");
                if (a == "joinfull") CoopCheck(lb.Full, $"lobby shows full ({lb.Cur}/{lb.Max})");
                _coopPick = pick; _coopStep = 6; _coopAt = now + 1f; return;
            }
            case 6:
                CoopMenu.TestClickRow(_coopPick);
                if (CoopAction == "joinfull") { _coopAt = now + 1f; _coopStep = 10; } else _coopStep = 99;
                return;
            case 3:
                CoopMenu.BecomeHost(); _coopStep = 4; _coopAt = now + 2f; _coopDeadline = now + 60f; return;
            case 4:
            {
                // the game's own region -> area flow; unattended: press Submit on what the game selected (like Enter)
                var st = TopState();
                if (st != _coopLastState) { _coopLastState = st; Plugin.L.LogWarning($"[Autopilot] coop host: top state {st}"); }
                if (LanState.Active == LanRole.Host || LanState.StartGameSeenAt >= 0 || st == "StateStartGame" || st == "StateLobbyMenu")
                { CoopCheck(true, $"host started through the game's region/area menus ({_coopSubmits} submits), players {Plugin.PlayerCount.Value}"); _coopStep = 99; return; }
                if (now > _coopDeadline) { CoopCheck(false, $"host flow stuck in {st}"); CoopShot("stuck"); _coopStep = 99; return; }
                if ((st.Contains("Region") || st.Contains("Area")) && _coopShots.Add(st)) { CoopShot(st.Contains("Region") ? "region" : "area"); _coopAt = now + 1.5f; return; }
                var es = UnityEngine.EventSystems.EventSystem.current;
                var go = es?.currentSelectedGameObject;
                if (go != null && (st.Contains("Region") || st.Contains("Area") || st.Contains("Modal")))
                {
                    UnityEngine.EventSystems.ExecuteEvents.Execute<UnityEngine.EventSystems.ISubmitHandler>(go, new UnityEngine.EventSystems.BaseEventData(es),
                        UnityEngine.EventSystems.ExecuteEvents.submitHandler);
                    _coopSubmits++;
                    Plugin.L.LogWarning($"[Autopilot] coop host: submit '{go.name}' in {st}");
                }
                else Plugin.L.LogWarning($"[Autopilot] coop host: nothing selected in {st}");
                _coopAt = now + 2.5f; return;
            }
            case 10:
                CoopShot("full"); CoopCheck(CoopMenu.StatusText.Contains("заняты") || CoopMenu.StatusText.Contains("full"), $"full lobby refused: '{CoopMenu.StatusText}'");
                CoopMenu.Close(true); _coopStep = 99; if (_menuQuit < 0) _menuQuit = now + 3; return;
        }
    }

    public static void OnClientSlot(int slot) { SlotsSeen.Add(slot); }

    static float _nextLobbyPoke;
    static int _lobbyPokes;
    /// <summary>The game parks every player in its lobby scene (StateLobbyMenu, "Enter JOIN") before spawning into the
    /// world. Unattended, press that button the way the game's own input action does.</summary>
    static string _lastState;
    /// <summary>Spawn map (StateMapScreen): request a spawn at the first point the game allows, through the same static
    /// MapController.RequestSpawn action the map's own spawn button raises.</summary>
    static void PickSpawn()
    {
        var pois = UnityEngine.Object.FindObjectsOfType<SpawnablePointOfInterest>();
        SpawnablePointOfInterest pick = null; int n = 0;
        foreach (var p in pois) { if (p == null) continue; n++; if (pick == null && p.CanSpawnAtLocation) pick = p; }
        if (pick == null) { Plugin.L.LogWarning($"[Autopilot] map: no spawnable point allowed ({n} found)"); return; }
        var a = MapController.RequestSpawn;
        if (a == null) { Plugin.L.LogWarning("[Autopilot] map: MapController.RequestSpawn has no subscriber"); return; }
        a.Invoke(pick);
        Plugin.L.LogWarning($"[Autopilot] map: RequestSpawn('{pick.name}') of {n} points");
    }

    static bool InLobby()
    {
        try { var st = FunCore.Managers.HybridSingleton<FunCore.Managers.GameStateManager>.Instance?.PeekState(); return st?.TryCast<StateLobbyMenu>() != null || st?.TryCast<StateMapScreen>() != null; }
        catch { return false; }
    }

    static void PokeLobby()
    {
        try
        {
            var st = FunCore.Managers.HybridSingleton<FunCore.Managers.GameStateManager>.Instance?.PeekState();
            var name = st?.GetIl2CppType()?.Name ?? "null";
            if (name != _lastState) { _lastState = name; Plugin.L.LogInfo($"[Autopilot] top state: {name}"); }
            if (st?.TryCast<StateMapScreen>() != null) { PickSpawn(); return; }
            var lobby = st?.TryCast<StateLobbyMenu>();
            if (lobby == null) return;
            var m = typeof(StateLobbyMenu).GetMethod("JoinButtonPressed", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var arg = Activator.CreateInstance(m.GetParameters()[0].ParameterType);
            m.Invoke(lobby, new[] { arg });
            _lobbyPokes++;
            Plugin.L.LogWarning($"[Autopilot] lobby: JoinButtonPressed #{_lobbyPokes}");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[Autopilot] lobby poke failed: {e.InnerException?.Message ?? e.Message}"); }
    }

    /// <summary>Self-test: the same RPC the game's winch UI ends with (static anchor on the ground ahead).</summary>
    static void WinchAttach(float t)
    {
        try
        {
            var w = CarSync.OwnWinch(out var car);
            if (w == null || car == null) { Plugin.L.LogWarning($"[Autopilot] t={t:0.0}s winch: no own winch component"); return; }
            var pts = w.winchPoints;
            var wp = pts != null && pts.Length > 0 ? pts[0] : car.transform;
            var rbT = car.rigidbody != null ? car.rigidbody.transform : car.transform;
            var local = rbT.InverseTransformPoint(wp.position);
            var probe = wp.position + car.transform.forward * 12f + Vector3.up * 30f;
            var anchor = Physics.Raycast(probe, Vector3.down, out var hit, 80f) ? hit.point : wp.position + car.transform.forward * 12f;
            w.RPC_AttachWinchToStaticObject(local, anchor);
            Plugin.L.LogWarning($"[Autopilot] t={t:0.0}s winch attach requested: point {local} anchor {anchor}");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[Autopilot] winch attach failed: {e}"); }
    }

    static string R(Rect r) => $"{r.x:0},{r.y:0},{r.width:0},{r.height:0}";
    static string F(double v) => double.IsNaN(v) ? "" : v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>U3: every remote car's LightsController.AreHeadlightsOn must match the Headlights bit of its
    /// CurrentFrameInput (what RemoteVisuals applies under RemoteInterpolate).</summary>
    static float _nextLightSample = -1;
    static void SampleRemoteLights(float t)
    {
        if (t < _nextLightSample) return;
        _nextLightSample = t + 0.5f;
        try
        {
            foreach (var fp in CarRegistry.Players)
            {
                if (fp == null || fp.Object == null || fp.Object.HasInputAuthority) continue;
                var want = fp.ButtonDown(NetworkInputData.VehicleButtons.Headlights);
                var lc = fp.GetComponentInChildren<LightsController>();
                var seen = lc != null && lc.AreHeadlightsOn;
                if (want != seen)
                {
                    LightMismatches++;
                    if (LightMismatches <= 8)
                        Plugin.L.LogWarning($"[Autopilot] t={t:0.0}s light mismatch {fp.gameObject.name}: input={want} lights={seen}");
                }
            }
        }
        catch { }
    }

    static void CheckLabel(float t)
    {
        void C(bool ok, string what) { (ok ? ref _passed : ref _failed)++; Checks.Add($"{(ok ? "PASS" : "FAIL")} t={t:0} {what}"); }
        var l = PingHud.LastLabel; var i = PingHud.LastIcon;
        C(PingHud.LastFrame >= Time.frameCount - 5, $"label drawn recently (frame {PingHud.LastFrame} vs {Time.frameCount})");
        C(PingHud.LastAnchor == "icon", $"anchored to the game's ping icon (anchor={PingHud.LastAnchor})");
        if (PingHud.LastAnchor == "icon")
        {
            C(l.x >= i.xMax && l.x - i.xMax < 20, $"label starts right of the icon (icon right {i.xMax:0}, label x {l.x:0})");
            C(Mathf.Abs(l.center.y - i.center.y) < Mathf.Max(4, i.height / 2), $"label vertically centred on the icon (dy {l.center.y - i.center.y:0.0})");
        }
        C(l.xMin >= 0 && l.yMin >= 0 && l.xMax <= Screen.width && l.yMax <= Screen.height, $"label inside screen {Screen.width}x{Screen.height}: {R(l)}");
        var expect = PingLogic.ClassifyEcho(PingHud.SmoothedMs, PingHud.TimedOut, Plugin.PingYellowAboveMs.Value, Plugin.PingRedAboveMs.Value);
        C(expect == PingHud.LastLevel, $"colour {PingHud.LastLevel} for '{PingHud.LastText}' (expected {expect})");
        var expText = PingLogic.LabelText(PingHud.SmoothedMs, PingHud.LossPct, Plugin.PingShowLossFromPct.Value, PingHud.NoLinkS); // 0.1.8 fo1: oracle ignored "no link N s"
        C(expText == PingHud.LastText, $"text '{PingHud.LastText}' (expected '{expText}', loss {PingHud.LossPct:0}%)");
    }

    static void Row(float t, Rigidbody rb)
    {
        var role = LanState.Active.ToString();
        double ms = PingHud.SmoothedMs, loss = double.NaN, fus = double.NaN;
        try
        {
            var r = OverlandNetworkRunner.Instance?.NetworkRunner;
            if (r != null && r.IsRunning) fus = LanState.Active == LanRole.Client ? r.GetPlayerRtt(r.LocalPlayer) * 1000.0 : double.NaN;
        }
        catch { }
        if (LanState.Active == LanRole.Client) lock (ClientLink.Tracker) loss = ClientLink.Tracker.LossPercent;
        Vector3 p = rb != null ? rb.position : Vector3.zero; var sp = rb != null ? rb.linearVelocity.magnitude * 3.6f : 0f;
        var cs = CarSync.Metrics.FirstOrDefault();
        var l = PingHud.LastLabel; var i = PingHud.LastIcon;
        var ci = CultureInfo.InvariantCulture;
        _log.WriteLine(string.Join(",", new[]
        {
            t.ToString("0.0", ci), CarSync.LocalSetting ? "1" : "0", CarSync.HostFlag.ToString(ci), CarSync.Enabled ? "1" : "0", CarSync.ApplyEnabled ? "1" : "0",
            CarSync.PosesSent.ToString(ci), CarSync.PosesReceived.ToString(ci), CarSync.PosesRejected.ToString(ci), CarSync.TeleportsIgnored.ToString(ci),
            Plugin.CarSyncEnabled.Value ? "1" : "0", role, PingHud.LastText.Replace(",", ";"), PingHud.LastLevel.ToString(), PingHud.LastAnchor,
            l.x.ToString("0", ci), l.y.ToString("0", ci), l.width.ToString("0", ci), l.height.ToString("0", ci),
            i.x.ToString("0", ci), i.y.ToString("0", ci), i.width.ToString("0", ci), i.height.ToString("0", ci), PingHud.LastIconActive ? "1" : "0",
            Screen.width.ToString(ci), Screen.height.ToString(ci), F(ms), PingHud.TimedOut ? "1" : "0", F(loss), F(fus),
            ClientLink.NetemRttMs.ToString(ci), (ClientLink.NetemLoss * 100).ToString("0", ci),
            p.x.ToString("0.00", ci), p.y.ToString("0.00", ci), p.z.ToString("0.00", ci), sp.ToString("0.0", ci), Driving ? "1" : "0",
            CarSync.Metrics.Count > 0 ? cs.Key.ToString(ci) : "", cs.Value.mode ?? "", F(cs.Value.rtt), F(cs.Value.err), F(cs.Value.errDeg), F(cs.Value.poseAge),
            CarSync.Snaps.ToString(ci), CarSync.Blends.ToString(ci), CarSync.ModeSwitches.ToString(ci), ClientLink.HostPingsEchoed.ToString(ci),
            CarSync.SoftSnaps.ToString(ci), CarSync.CheckpointsSkipped.ToString(ci), CarSync.ContactPauses.ToString(ci),
            CarSync.LastAction.Count > 0 ? string.Join("|", CarSync.LastAction.Values) : "", CarSync.ContactBoxOverride.ToString(ci), CarSync.WinchPauses.ToString(ci), CarSync.HostJumps.ToString(ci),
        }));
    }

    static void Trace(float t, float now, Rigidbody rb)
    {
        if (_trace == null) return;
        var ci = CultureInfo.InvariantCulture;
        string running = "", tick = "";
        try { var r = OverlandNetworkRunner.Instance?.NetworkRunner; if (r != null) { running = r.IsRunning ? "1" : "0"; if (r.IsRunning) tick = r.Tick.Raw.ToString(ci); } } catch { }
        if (now >= _nextTraceScan) { _nextTraceScan = now + 1f; _tracePlayers = CarRegistry.Players; }
        Vector3 p = rb != null ? rb.position : Vector3.zero; var sp = rb != null ? rb.linearVelocity.magnitude * 3.6f : 0f;
        // host: the first remote car (the client under test)
        string rs = "", rx = "", ry = "", rz = "", rk = "", st = "", th = "", br = "", it = "";
        if (LanState.Active == LanRole.Host)
            foreach (var fp in _tracePlayers)
            {
                try
                {
                    if (fp == null || fp.Object == null || fp.Object.HasInputAuthority) continue;
                    var b = fp.rigidbody ?? fp.vehicleBase?.cachedRigidbody; if (b == null) continue;
                    var inp = fp.CurrentFrameInput;
                    rs = fp.Object.InputAuthority.PlayerId.ToString(ci);
                    rx = b.position.x.ToString("0.00", ci); ry = b.position.y.ToString("0.00", ci); rz = b.position.z.ToString("0.00", ci);
                    rk = (b.linearVelocity.magnitude * 3.6f).ToString("0.0", ci);
                    st = inp.Steering.ToString("0.00", ci); th = inp.Throttle.ToString("0.00", ci); br = inp.Brake.ToString("0.00", ci); it = fp.FrameInputTick.ToString(ci);
                    break;
                }
                catch { }
            }
        var cs = CarSync.Metrics.FirstOrDefault();
        long dOut = 0, dIn = 0;
        if (OutageSim.Installed) { dOut = OutageSim.DroppedOut; dIn = OutageSim.DroppedIn; }
        _trace.WriteLine(string.Join(",", new[]
        {
            t.ToString("0.000", ci), now.ToString("0.000", ci), OutageSim.Active ? "1" : OutageSim.Loss > 0 ? "L" + (OutageSim.Loss * 100).ToString("0", ci) : "0", LanState.Active.ToString(), running, tick, _tracePlayers.Length.ToString(ci),
            p.x.ToString("0.00", ci), p.y.ToString("0.00", ci), p.z.ToString("0.00", ci), sp.ToString("0.0", ci), OthLan.OwnCar.State,
            OthLan.OwnCar.Resets.ToString(ci), OthLan.OwnCar.Restores.ToString(ci), OthLan.OwnCar.Accepts.ToString(ci), OthLan.OwnCar.LastErr.ToString("0.000", ci), OthLan.OwnCar.LastErrDeg.ToString("0.0", ci),
            F(PingHud.SmoothedMs), PingHud.TimedOut ? "1" : "0", PingHud.LastText.Replace(",", ";"), CarSync.Enabled ? "1" : "0", CarSync.HostFlag.ToString(ci),
            rs, rx, ry, rz, rk, st, th, br, it,
            cs.Value.mode ?? "", F(cs.Value.err), CarSync.LastAction.Count > 0 ? string.Join("|", CarSync.LastAction.Values).Replace(",", ";") : "",
            CarSync.Snaps.ToString(ci), CarSync.Blends.ToString(ci), dOut.ToString(ci), dIn.ToString(ci),
        }));
    }

    static bool _finished;
    static void Finish(string why)
    {
        if (_finished) return;
        _finished = true; Driving = false;
        var sb = new StringBuilder();
        sb.AppendLine($"tag={Tag} mode={Mode} reason={why} realtime={Time.realtimeSinceStartup:0}s");
        sb.AppendLine($"label checks: {_passed} passed, {_failed} failed; saves suppressed {SavesSuppressed}; inputs injected {InputsInjected}; handbrake releases {HandbrakeReleases}; unstuck manoeuvres {Unstucks}");
        sb.AppendLine($"gate: tier={BuildGate.Tier} outcome={BuildGate.Outcome} fusion={BuildGate.FusionVersion} buildKey={BuildGate.BuildKey} hooks={FusionHooks.Applied} reasons={string.Join(" | ", BuildGate.Reasons)}");
        sb.AppendLine($"slots: role={LanState.Active} clientId={LanState.ClientId} seen=[{string.Join(",", SlotsSeen)}] rejoins={Rejoins} hostTable=[{LanState.Slots.Describe()}]");
        sb.AppendLine($"carsync: local={CarSync.LocalSetting} hostFlag={CarSync.HostFlag} sent={CarSync.PosesSent} recv={CarSync.PosesReceived} rejected={CarSync.PosesRejected} teleportFramesIgnored={CarSync.TeleportsIgnored}");
        sb.AppendLine($"carsync: snaps={CarSync.Snaps} snapLoopsBroken={CarSync.SnapLoopsBroken} softSnaps={CarSync.SoftSnaps} checkpointsSkipped={CarSync.CheckpointsSkipped} contactPauses={CarSync.ContactPauses} winchPauses={CarSync.WinchPauses} hostJumps={CarSync.HostJumps} teleports={CarSync.Teleports} rams={RamContacts} blends={CarSync.Blends} servos={CarSync.Servos} modeSwitches={CarSync.ModeSwitches} {string.Join(" ", CarSync.HostStats.Select(kv => $"slot{kv.Key}: {kv.Value}"))}");
        sb.AppendLine($"carsync v2: frozenClientPoses={CarSync.FrozenClientPoses} jumpsTowardClient={CarSync.JumpsTowardClient} lowConverges={CarSync.LowConverges} acceptsSent={CarSync.AcceptsSent} recoveries={CarSync.Recoveries} (quiet={CarSync.RecoverQuiet} converge={CarSync.RecoverConverge} snap={CarSync.RecoverSnap}) playbackOk={CarSync.PlaybackOk} playbackExtraps={CarSync.PlaybackExtraps} converge times=[{string.Join(",", CarSync.ConvergeTimes.Select(x => x.ToString("0.00", CultureInfo.InvariantCulture)))}]");
        sb.AppendLine($"netstats: {NetStats.Describe()}");
        sb.AppendLine($"remoteVisuals: {RemoteVisuals.Describe()} lightToggles={LightToggles} lightMismatches={LightMismatches}");
        sb.AppendLine($"fusion netem: delayed={OutageSim.Delayed} stalled={OutageSim.Stalled} lostOut={OutageSim.LostOut} lostIn={OutageSim.LostIn}");
        sb.AppendLine($"menu nav: fixes={CoopMenu.NavFixes} order={CoopMenu.NavInfo}");
        sb.AppendLine(OthLan.FreezeWatch.Describe());
        sb.AppendLine($"owncar: mode={OthLan.OwnCar.Configured} hooks={OthLan.OwnCar.HooksOk} {OthLan.OwnCar.Describe()}");
        sb.AppendLine($"coop: action={CoopAction ?? "-"} state={CoopMenu.State} opens={CoopMenu.Opens} joins={CoopMenu.Joins} hosts={CoopMenu.Hosts} discovery rounds={Discovery.Rounds} replies={Discovery.Replies} via=[{string.Join(",", Discovery.RepliesVia.Select(kv => kv.Key + ":" + kv.Value))}] answered={HostInfo.DiscoveryAnswers} lobby='{HostInfo.LobbyName}' cur={HostInfo.CurPlayers}");
        sb.AppendLine($"update: running={Plugin.Version} state={Updater.State} detail='{Updater.Detail}' latest={Updater.Latest?.Version ?? "-"} checks={Updater.Checks} started={Updater.Started} skipped='{Plugin.UpdateSkipped.Value}' promptFrame={Updater.PromptFrame}");
        sb.AppendLine($"optional hooks: {string.Join(" | ", OptionalHooks.Status.Select(kv => kv.Key + "=" + kv.Value))}");
        foreach (var c in Checks) sb.AppendLine(c);
        try { File.WriteAllText(Path.Combine(OutDir, $"{Tag}-summary.txt"), sb.ToString()); } catch { }
        try { _trace?.Flush(); } catch { }
        Plugin.L.LogWarning($"[Autopilot] finished ({why}); quitting.\n{sb}");
        try { _log?.Flush(); } catch { }
        Application.Quit();
    }
}
#else
/// <summary>Public build: the self-test autopilot is compiled out.</summary>
internal static class Autopilot
{
    public const bool Active = false;
    public static string Mode = null, Area = "R2A1_Desert";
    public static float Delay = 0;
    public static void Init() { }
    public static void Tick() { }
    public static void OnClientSlot(int slot) { }
    public static void OnAutoRejoin() { }
}
#endif

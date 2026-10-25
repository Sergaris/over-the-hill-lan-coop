using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP.Hook;
using Fusion;
using Fusion.Sockets;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace OthLan;

/// <summary>
/// Redirects Fusion 2.1.2's start-up from Photon Cloud to a raw UDP socket (NetSocketNative / nanosockets).
///
///  1. NetworkRunner.StartGame(StartGameArgs)  RVA 0x12DCDA0  -- if LAN is armed and GameMode matches, rewrite
///     args.GameMode := Single so Fusion takes StartGameModeSinglePlayer (no ConnectToCloud, local SessionInfo).
///  2. NetworkRunner.Initialize(InitArgs)       RVA 0x12BC310  -- rewrite SimulationMode (Host|Client), PlayerCount,
///     Address := Any(port), ConnectionToken; force Topology=ClientServer. Client: Connect(host) right after.
///  3. NetworkRunnerInitializeArgs.get_IsSinglePlayer RVA 0x1322F00 -- true while (2) runs, so Initialize picks the
///     non-cloud socket branch (NetSocketNull) instead of CreateCloudSocket (inlined; throws without CloudServices).
///  4. Simulation.NetworkInit(INetSocket, NetAddress) RVA 0x130D230 -- swap that NetSocketNull for NetSocketNative.
///
///  5. NetworkRunner Simulation.ICallbacks.get_LocalPlayerRef RVA 0x12CF560 -- Fusion 2 takes the local PlayerRef from
///     CloudServices; without a cloud room it is None ("adding player [Player:None]", "AssertException:
///     LocalPlayer.IsRealPlayer", host stuck loading; seen in v0.1.2). For the LAN runner: host = index 1 (what
///     Fusion's own single-player path returns), client = the slot encoded in its connect uniqueId.
///  6. NetworkRunner Simulation.ICallbacks.OnInternalConnectionAttempt RVA 0x12D53C0 -- forwards every UDP connect
///     attempt to CloudServices, which asserts "CloudServices should not be in Stage None" (the game's StartGameAsync
///     joined a lobby on this runner first) and shuts the runner down -> client NRE. LAN runner: no-op.
///  7. Simulation INetPeerGroupCallbacks.OnConnectionRequest RVA 0x13100F0 -- the server only accepts a uniqueId that
///     the cloud mapped to a PlayerRef, otherwise it answers Waiting forever. LAN host: register the mapping itself.
///
/// Native detours (not Harmony) because 1, 2 and 3 take large structs by hidden pointer: Il2CppInterop trampolines
/// box a copy, so edits would be lost (see Over-the-Hill-Unlimited SessionSizePatches note). All four functions are
/// unique (no COMDAT folding) per the RVA map of build 26062472.
/// </summary>
internal static unsafe class FusionHooks
{
    const int GM_Single = 1, GM_Host = 4, GM_Client = 5;
    const int SM_Host = 2, SM_Client = 4;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr StartGameFn(IntPtr runner, IntPtr args, IntPtr mi);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr InitializeFn(IntPtr runner, IntPtr args, IntPtr mi);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate byte IsSinglePlayerFn(IntPtr args, IntPtr mi);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void NetworkInitFn(IntPtr sim, IntPtr socket, IntPtr addr, IntPtr mi);

    static StartGameFn _startGameOrig, _startGameHook;
    static InitializeFn _initOrig, _initHook;
    static IsSinglePlayerFn _ispOrig, _ispHook;
    static NetworkInitFn _netInitOrig, _netInitHook;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int LocalPlayerRefFn(IntPtr runner, IntPtr mi);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void ConnAttemptFn(IntPtr runner, int attempt, int total, IntPtr shouldChange, IntPtr newAddress, IntPtr mi);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ConnRequestFn(IntPtr sim, IntPtr remoteAddress, IntPtr token, IntPtr uniqueId, IntPtr mi);
    static LocalPlayerRefFn _lprOrig, _lprHook;
    static ConnAttemptFn _caOrig, _caHook;
    static ConnRequestFn _crOrig, _crHook;
    const int Reply_Refuse = 1;
    static INativeDetour[] _detours;

    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);

    /// <summary>Native code address of every hooked (and directly called) Fusion method, keyed like the manifest.
    /// Filled by <see cref="Resolve"/> (by name through the interop MethodInfo*), consumed by the gate's code
    /// signatures and by <see cref="Apply"/>.</summary>
    public static readonly System.Collections.Generic.Dictionary<string, IntPtr> Pointers = new();
    public static bool Applied;

    const string K_Start = "Fusion.NetworkRunner::StartGame(StartGameArgs)";
    const string K_Init = "Fusion.NetworkRunner::Initialize(NetworkRunnerInitializeArgs)";
    const string K_Isp = "Fusion.NetworkRunnerInitializeArgs::get_IsSinglePlayer()";
    const string K_Net = "Fusion.Simulation::NetworkInit(INetSocket,NetAddress)";
    const string K_Connect = "Fusion.NetworkRunner::Connect(NetAddress,byte[],byte[])";
    const string K_Native = "Fusion.Sockets.NetSocketNative::.ctor(NetSocketFlags)";
    const string K_Lpr = "Fusion.NetworkRunner::Simulation.ICallbacks.get_LocalPlayerRef()";
    const string K_Ca = "Fusion.NetworkRunner::Simulation.ICallbacks.OnInternalConnectionAttempt(int,int,out bool,out NetAddress)";
    const string K_Cr = "Fusion.Simulation::INetPeerGroupCallbacks.OnConnectionRequest(NetAddress,byte[],byte[])";

    static IntPtr FromMethodInfo(IntPtr mi, string key, SupportedBuild build)
    {
        if (mi == IntPtr.Zero) throw new InvalidOperationException($"{key}: MethodInfo* is null");
        var fn = *(IntPtr*)mi; // Il2CppMethodInfo.methodPointer is the first field
        var rva = (long)fn - (long)GetModuleHandleW("GameAssembly.dll");
        Plugin.L.LogInfo($"[Hooks] {key} -> RVA 0x{rva:X}");
        // Known build: the address must be the one in the manifest. Unknown build: addresses move; CodeSig decides.
        if (build != null && build.Rva.TryGetValue(key, out var exp) && Convert.ToInt64(exp, 16) != rva)
            throw new InvalidOperationException($"RVA mismatch for {key}: expected {exp}, got 0x{rva:X}. Wrong build?");
        return fn;
    }

    static IntPtr ByMethod(Type t, string name, int paramCount, string key, SupportedBuild build)
    {
        var m = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                 .FirstOrDefault(x => x.Name == name && x.GetParameters().Length == paramCount)
                ?? throw new MissingMethodException(t.FullName, name);
        var f = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(m)
                ?? throw new MissingFieldException($"no MethodInfo field for {t.Name}.{name}");
        return FromMethodInfo((IntPtr)f.GetValue(null), key, build);
    }

    static IntPtr ByCtor(Type t, int paramCount, string key, SupportedBuild build)
    {
        var c = t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                 .FirstOrDefault(x => x.GetParameters().Length == paramCount && x.GetParameters()[0].ParameterType != typeof(IntPtr))
                ?? throw new MissingMethodException(t.FullName, ".ctor");
        // Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod calls GetGenericArguments(), which a
        // ConstructorInfo does not support (NotSupportedException, seen in-game 0.1.6 run12a). Use the generator's
        // naming instead: NativeMethodInfoPtr__ctor_<Visibility>_Void_<ParamType...>_<n>.
        var suffix = "_Void_" + string.Join("_", c.GetParameters().Select(p => p.ParameterType.Name));
        var fields = t.GetFields(BindingFlags.NonPublic | BindingFlags.Static)
                      .Where(x => x.FieldType == typeof(IntPtr) && x.Name.StartsWith("NativeMethodInfoPtr__ctor_", StringComparison.Ordinal)
                                  && System.Text.RegularExpressions.Regex.IsMatch(x.Name, "^NativeMethodInfoPtr__ctor_[A-Za-z]+" + System.Text.RegularExpressions.Regex.Escape(suffix) + "_[0-9]+$"))
                      .ToList();
        if (fields.Count != 1) throw new MissingFieldException($"{t.Name}..ctor{suffix}: {fields.Count} MethodInfo fields match");
        return FromMethodInfo((IntPtr)fields[0].GetValue(null), key, build);
    }

    /// <summary>For explicit interface implementations (no public interop method): read the generated MethodInfo* field.</summary>
    static IntPtr ByField(Type t, string field, string key, SupportedBuild build)
    {
        var f = t.GetField(field, BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingFieldException(t.FullName, field);
        return FromMethodInfo((IntPtr)f.GetValue(null), key, build);
    }

    /// <summary>Find every method by name. <paramref name="build"/> = the hash-matched manifest build, or null.</summary>
    public static void Resolve(SupportedBuild build)
    {
        Pointers.Clear();
        Pointers[K_Start] = ByMethod(typeof(NetworkRunner), "StartGame", 1, K_Start, build);
        Pointers[K_Init] = ByMethod(typeof(NetworkRunner), "Initialize", 1, K_Init, build);
        Pointers[K_Isp] = ByMethod(typeof(NetworkRunnerInitializeArgs), "get_IsSinglePlayer", 0, K_Isp, build);
        Pointers[K_Net] = ByMethod(typeof(Simulation), "NetworkInit", 2, K_Net, build);
        Pointers[K_Connect] = ByMethod(typeof(NetworkRunner), "Connect", 3, K_Connect, build);
        Pointers[K_Native] = ByCtor(typeof(NetSocketNative), 1, K_Native, build);
        Pointers[K_Lpr] = ByField(typeof(NetworkRunner), "NativeMethodInfoPtr_Fusion_Simulation_ICallbacks_get_LocalPlayerRef_Private_Virtual_Final_New_get_PlayerRef_0", K_Lpr, build);
        Pointers[K_Ca] = ByField(typeof(NetworkRunner), "NativeMethodInfoPtr_Fusion_Simulation_ICallbacks_OnInternalConnectionAttempt_Private_Virtual_Final_New_Void_Int32_Int32_byref_Boolean_byref_NetAddress_0", K_Ca, build);
        Pointers[K_Cr] = ByField(typeof(Simulation), "NativeMethodInfoPtr_Fusion_Sockets_INetPeerGroupCallbacks_OnConnectionRequest_Private_Virtual_Final_New_OnConnectionRequestReply_NetAddress_Il2CppStructArray_1_Byte_Il2CppStructArray_1_Byte_0", K_Cr, build);
        if (System.Runtime.CompilerServices.Unsafe.SizeOf<PlayerRef>() != 4)
            throw new InvalidOperationException($"interop PlayerRef size {System.Runtime.CompilerServices.Unsafe.SizeOf<PlayerRef>()} != 4");
    }

    /// <summary>Apply the seven detours (once). Only after the gate said Run (or the user chose Continue).</summary>
    public static void Apply()
    {
        if (Applied) return;
        if (!FusionLayout.Ready) throw new InvalidOperationException("Fusion struct layout not resolved");
        _startGameHook = StartGameDetour; _initHook = InitializeDetour; _ispHook = IsSinglePlayerDetour; _netInitHook = NetworkInitDetour;
        _lprHook = LocalPlayerRefDetour; _caHook = ConnAttemptDetour; _crHook = ConnRequestDetour;
        _detours = new[]
        {
            INativeDetour.CreateAndApply(Pointers[K_Start], _startGameHook, out _startGameOrig),
            INativeDetour.CreateAndApply(Pointers[K_Init], _initHook, out _initOrig),
            INativeDetour.CreateAndApply(Pointers[K_Isp], _ispHook, out _ispOrig),
            INativeDetour.CreateAndApply(Pointers[K_Net], _netInitHook, out _netInitOrig),
            INativeDetour.CreateAndApply(Pointers[K_Lpr], _lprHook, out _lprOrig),
            INativeDetour.CreateAndApply(Pointers[K_Ca], _caHook, out _caOrig),
            INativeDetour.CreateAndApply(Pointers[K_Cr], _crHook, out _crOrig),
        };
        Applied = true;
        Plugin.L.LogInfo($"[Hooks] {_detours.Length} native detours applied");
    }

    static IntPtr StartGameDetour(IntPtr runner, IntPtr args, IntPtr mi)
    {
        var redirected = LanRole.Off;
        try
        {
            if (!LanState.Disabled && LanState.Armed != LanRole.Off)
            {
                var gm = *(int*)(args + FusionLayout.SgaGameMode);
                if ((LanState.Armed == LanRole.Host && gm == GM_Host) || (LanState.Armed == LanRole.Client && gm == GM_Client))
                {
                    redirected = LanState.Armed;
                    var tok = *(IntPtr*)(args + FusionLayout.SgaConnectionToken);
                    LanState.Token = tok == IntPtr.Zero ? null : new Il2CppStructArray<byte>(tok).ToArray();
                    LanState.UniqueId = *(long*)(args + FusionLayout.SgaPlayerUniqueId);
                    LanState.Pending = redirected;
                    LanState.LanRunner = runner; LanState.LanRunnerRole = redirected; LanState.LanSim = IntPtr.Zero;
                    if (redirected == LanRole.Host) { LanState.SlotOwners.Clear(); LanState.Slots.Reset(LanState.EffectivePlayers); PublishHostInfo(args); }
                    else
                    {
                        LanState.ClientSlot = LanState.AssignedSlot >= 0 ? LanState.AssignedSlot : PickClientSlot();
                        LanState.SlotSource = LanState.AssignedSlot >= 0 ? "host" : "fallback";
                        Plugin.L.LogInfo($"[LAN] client slot {LanState.ClientSlot} ({LanState.SlotSource})");
                        Autopilot.OnClientSlot(LanState.ClientSlot);
                    }
                    LanState.StartGameSeenAt = UnityEngine.Time.realtimeSinceStartup;
                    JoinFlow.NoteStartSeen(redirected); // vanilla "Create Server" path: watch it too
                    *(int*)(args + FusionLayout.SgaGameMode) = GM_Single;
                    Plugin.L.LogInfo($"[LAN] StartGame(GameMode={gm}) redirected to direct UDP as {redirected}");
                }
                else
                    Plugin.L.LogInfo($"[LAN] StartGame(GameMode={gm}) not redirected (armed={LanState.Armed})");
            }
            if (redirected == LanRole.Off && runner == LanState.LanRunner) { LanState.LanRunner = IntPtr.Zero; LanState.LanRunnerRole = LanRole.Off; }
        }
        catch (Exception e) { Plugin.L.LogError($"[LAN] StartGame pre: {e}"); redirected = LanRole.Off; LanState.Pending = LanRole.Off; }

        var result = _startGameOrig(runner, args, mi);

        if (redirected != LanRole.Off)
        {
            try
            {
                new NetworkRunner(runner)._GameMode_k__BackingField = redirected == LanRole.Host ? GameMode.Host : GameMode.Client;
                LanState.Active = redirected;
                // 0.1.9: client entered a host's world — F7 only on explicit keypress afterward.
                if (redirected == LanRole.Client) Overlay.Hide();
            }
            catch (Exception e) { Plugin.L.LogError($"[LAN] StartGame post: {e}"); }
            finally { LanState.Pending = LanRole.Off; }
        }
        return result;
    }

    static IntPtr InitializeDetour(IntPtr runner, IntPtr args, IntPtr mi)
    {
        var role = LanState.Pending;
        if (role == LanRole.Off) return _initOrig(runner, args, mi);

        ushort port = 0;
        try
        {
            var n = LanState.EffectivePlayers;
            // Offsets resolved by name at start-up (FusionLayout); Nullable<T> = hasValue + value inside the field.
            *(byte*)(args + FusionLayout.IaPlayerCount + FusionLayout.IaPlayerCountHas) = 1;
            *(int*)(args + FusionLayout.IaPlayerCount + FusionLayout.IaPlayerCountVal) = n;
            *(byte*)(args + FusionLayout.IaSimMode + FusionLayout.IaSimModeHas) = 1;
            *(int*)(args + FusionLayout.IaSimMode + FusionLayout.IaSimModeVal) = role == LanRole.Host ? SM_Host : SM_Client;
            port = role == LanRole.Host ? (ushort)Plugin.Port.Value : (ushort)0;
            var any = NetAddress.Any(port);
            *(byte*)(args + FusionLayout.IaAddress + FusionLayout.IaAddressHas) = 1;
            System.Runtime.CompilerServices.Unsafe.Write((void*)(args + FusionLayout.IaAddress + FusionLayout.IaAddressVal), any); // Address = 0.0.0.0:port
            var cfgPtr = *(IntPtr*)(args + FusionLayout.IaConfig);
            if (cfgPtr != IntPtr.Zero)
            {
                var cfg = new NetworkProjectConfig(cfgPtr);
                cfg.Simulation.Topology = Topologies.ClientServer;
                cfg.Simulation.PlayerCount = n;
                try
                {
                    var net = cfg.Network; var nc = cfg.NetworkConditions;
                    Plugin.L.LogInfo($"[Diag] Fusion network: ConnectionTimeout={net?.ConnectionTimeout} s ConnectionShutdownTime={net?.ConnectionShutdownTime} s " +
                                     $"ReliableModes={net?.ReliableDataTransferModes} NetworkConditions.Enabled={nc?.Enabled}");
                }
                catch (Exception e) { Plugin.L.LogWarning($"[Diag] Fusion network config unreadable: {e.Message}"); }
            }
            LanState.ForceNullSocketPath = true;
            LanState.SwapSocket = true;
            Plugin.L.LogInfo($"[LAN] Initialize as {role}: players={n} bind=0.0.0.0:{port}");
        }
        catch (Exception e)
        {
            Plugin.L.LogError($"[LAN] Initialize pre: {e}");
        }

        IntPtr task;
        try { task = _initOrig(runner, args, mi); }
        finally { LanState.ForceNullSocketPath = false; LanState.SwapSocket = false; }

        if (role == LanRole.Client)
        {
            try
            {
                var addr = NetAddress.CreateFromIpPort(LanState.TargetIp, LanState.TargetPort);
                var token = LanState.Token == null ? null : new Il2CppStructArray<byte>(LanState.Token);
                // uniqueId byte0 = PlayerRef index we claim, byte1 = magic; the host maps it (ConnRequestDetour).
                var raw = BitConverter.GetBytes(LanState.UniqueId != 0 ? LanState.UniqueId : DateTime.UtcNow.Ticks);
                raw[0] = (byte)LanState.ClientSlot; raw[1] = LanState.UidMagic;
                var tag = SlotAllocator.Tag(LanState.ClientId);          // bytes 2..5: who we are (host checks the reservation)
                raw[2] = (byte)tag; raw[3] = (byte)(tag >> 8); raw[4] = (byte)(tag >> 16); raw[5] = (byte)(tag >> 24);
                var uid = new Il2CppStructArray<byte>(raw);
                new NetworkRunner(runner).Connect(addr, token, uid);
                LanState.Status("status.connectingSlot", $"{LanState.TargetIp}:{LanState.TargetPort}", LanState.ClientSlot);
                Plugin.L.LogInfo($"[LAN] {LanState.LastStatus.En()}");
            }
            catch (Exception e) { Plugin.L.LogError($"[LAN] Connect: {e}"); LanState.Status("status.connectFailed", e.Message); }
        }
        else LanState.Status("status.hosting", port);
        return task;
    }

    static byte IsSinglePlayerDetour(IntPtr args, IntPtr mi) =>
        LanState.ForceNullSocketPath ? (byte)1 : _ispOrig(args, mi);

    static void NetworkInitDetour(IntPtr sim, IntPtr socket, IntPtr addr, IntPtr mi)
    {
        if (LanState.SwapSocket)
        {
            try
            {
                var native = new NetSocketNative(NetSocketFlags.None);
                socket = native.Pointer;
                LanState.SwapSocket = false;
                Plugin.L.LogInfo("[LAN] Simulation socket = NetSocketNative (direct UDP, no relay)");
            }
            catch (Exception e) { Plugin.L.LogError($"[LAN] socket swap failed: {e}"); }
        }
        _netInitOrig(sim, socket, addr, mi);
    }

    static bool IsLanRunner(IntPtr runner) => runner != IntPtr.Zero && runner == LanState.LanRunner && LanState.LanRunnerRole != LanRole.Off;

    /// <summary>FALLBACK client slot (0.1.6: normally the host assigns it over UDP Port+1, see SlotAllocator): indices
    /// 0..PlayerCount-1 minus the host's index 1, derived from the last octet of this PC's Radmin IP; also the hint the
    /// client sends with its slot request. Two fallback clients that collide are refused by the host (retry rerolls).</summary>
    static int _slotRetry;
    internal static int PickClientSlot()
    {
        var n = LanState.EffectivePlayers;
        var slots = Enumerable.Range(0, n).Where(i => i != LanState.HostIndex).ToArray();
        int seed;
        var ip = NetInfo.RadminIp();
        if (ip != null && System.Net.IPAddress.TryParse(ip, out var a)) seed = a.GetAddressBytes()[3] + 7 * _slotRetry;
        else seed = Environment.TickCount;
        _slotRetry++;
        return slots[Math.Abs(seed) % slots.Length];
    }

    static int LocalPlayerRefDetour(IntPtr runner, IntPtr mi)
    {
        if (IsLanRunner(runner))
        {
            var idx = LanState.LanRunnerRole == LanRole.Host ? LanState.HostIndex : LanState.ClientSlot;
            return idx + 1; // PlayerRef raw = index + 1 (PlayerRef.FromIndex, RVA 0x12A9C70: lea eax,[rcx+1])
        }
        return _lprOrig(runner, mi);
    }

    static void ConnAttemptDetour(IntPtr runner, int attempt, int total, IntPtr shouldChange, IntPtr newAddress, IntPtr mi)
    {
        if (IsLanRunner(runner))
        {
            *(byte*)shouldChange = 0;
            for (int i = 0; i < FusionLayout.NetAddressSize; i++) ((byte*)newAddress)[i] = 0;
            if (attempt == 1 || attempt % 10 == 0) Plugin.L.LogInfo($"[LAN] connect attempt {attempt}/{total} to {LanState.TargetIp}:{LanState.TargetPort}");
            return;
        }
        _caOrig(runner, attempt, total, shouldChange, newAddress, mi);
    }

    static int ConnRequestDetour(IntPtr sim, IntPtr remoteAddress, IntPtr token, IntPtr uniqueId, IntPtr mi)
    {
        try
        {
            if (LanState.LanRunnerRole == LanRole.Host && LanState.LanRunner != IntPtr.Zero && uniqueId != IntPtr.Zero)
            {
                if (LanState.LanSim == IntPtr.Zero)
                    LanState.LanSim = new NetworkRunner(LanState.LanRunner)._simulation?.Pointer ?? IntPtr.Zero;
                if (sim == LanState.LanSim)
                {
                    var arr = new Il2CppStructArray<byte>(uniqueId);
                    var bytes = arr.ToArray();
                    if (bytes.Length >= 8)
                    {
                        var key = BitConverter.ToUInt64(bytes, 0);
                        var s = new Simulation(sim);
                        if (!s._uniqueIdPlayerRefMapping.ContainsKey(key))
                        {
                            var n = LanState.EffectivePlayers;
                            int slot = bytes[1] == LanState.UidMagic ? bytes[0] : -1;
                            if (slot < 0 || slot >= n || slot == LanState.HostIndex)
                            {
                                Plugin.L.LogWarning($"[LAN] refusing client: bad slot {slot} (different OTH-LAN version or PlayerCount?)");
                                return Reply_Refuse;
                            }
                            var tag = (uint)(bytes[2] | bytes[3] << 8 | bytes[4] << 16 | bytes[5] << 24);
                            if (!LanState.Slots.Admit(slot, tag, NetClock.Now, out var why))
                            {
                                Plugin.L.LogWarning($"[LAN] refusing client: {why} (tag {tag:x8}) [{LanState.Slots.Describe()}]");
                                return Reply_Refuse;
                            }
                            var runnerObj = new NetworkRunner(LanState.LanRunner);
                            if (LanState.SlotOwners.TryGetValue(slot, out var owner) && owner != key)
                            {
                                if (runnerObj.IsPlayerValid(PlayerRef.FromIndex(slot)))
                                {
                                    Plugin.L.LogWarning($"[LAN] refusing client: player slot {slot} is taken (it should retry)");
                                    return Reply_Refuse;
                                }
                                s._uniqueIdPlayerRefMapping.Remove(owner); // stale entry from a client that left
                            }
                            s.RegisterUniqueIdPlayerMapping(slot + 1, arr, PlayerRef.FromIndex(slot));
                            LanState.SlotOwners[slot] = key;
                            Plugin.L.LogInfo($"[LAN] client accepted into player slot {slot}");
                        }
                    }
                }
            }
        }
        catch (Exception e) { Plugin.L.LogError($"[LAN] connection request: {e}"); }
        return _crOrig(sim, remoteAddress, token, uniqueId, mi);
    }

    /// <summary>Host: copy StartGameArgs.SessionProperties (what vanilla puts into the Photon room) and the
    /// area they name, and serve them to LAN clients (HostInfo).</summary>
    static void PublishHostInfo(IntPtr args)
    {
        try
        {
            var props = new System.Collections.Generic.Dictionary<string, string>();
            var dictPtr = *(IntPtr*)(args + FusionLayout.SgaSessionProperties);
            if (dictPtr != IntPtr.Zero)
            {
                var d = new Il2CppSystem.Collections.Generic.Dictionary<string, SessionProperty>(dictPtr);
                foreach (var kv in d)
                {
                    var v = kv.Value;
                    if (v == null) continue;
                    if (v.IsString) props[kv.Key] = "s:" + (string)v;
                    else if (v.IsInt) props[kv.Key] = "i:" + (int)v;
                    else if (v.Isbool) props[kv.Key] = "b:" + ((bool)v ? "1" : "0");
                }
            }
            var areaNames = new System.Collections.Generic.HashSet<string>();
            foreach (var a in UnityEngine.Resources.FindObjectsOfTypeAll<AreaInfo>()) if (a != null) areaNames.Add(a.name);
            string area = null;
            foreach (var kv in props)
                if (kv.Value.StartsWith("s:") && areaNames.Contains(kv.Value[2..])) { area = kv.Value[2..]; break; }
            Plugin.L.LogInfo($"[LAN] host session props: {string.Join(", ", props)}");
            if (area == null) Plugin.L.LogWarning($"[LAN] host area not found in session props (areas: {string.Join(", ", areaNames)})");
            HostInfo.Publish(area, LanState.EffectivePlayers, props);
        }
        catch (Exception e) { Plugin.L.LogError($"[LAN] publish host info: {e}"); }
    }
}

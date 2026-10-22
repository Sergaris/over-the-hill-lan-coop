# OTH-LAN: architecture (v0.1.0, 2026-10-20)

Goal: two or more friends play Over The Hill (Steam app 2929250) co-op over **Radmin VPN** (or any LAN) using
direct UDP instead of Photon Cloud. The mod is **version-locked** to a game build and ships as a **single Windows
installer exe** plus a BepInEx plugin.

Status legend: **[built]** compiled here; **[tested-box]** executed on the Linux box; **[UNVERIFIED]** needs the
real game on Windows (host and friend PCs). Nothing in this document has run inside the game yet.

---

## 1. Components

| Component | Path | Status |
|---|---|---|
| Plugin `OthLan.dll` (net6.0, BepInEx 6 IL2CPP, compiled against generated interop) | `src/OthLan.Plugin/` | [built] 0 errors, 0 warnings |
| Installer `OTH-LAN-Setup.exe` (net8.0, win-x64, self-contained single file, ~69 MB, embeds BepInEx + unity-libs + plugin) | `src/OthLan.Installer/` → `out/installer/` | [built]; logic [tested-box] via a linux-x64 build against a fake game dir; the real win-x64 exe [tested-wine] 2026-10-20 under Wine 10.0 (verify, install, uninstall with byte-identical rollback, tampered-build refusal, no-Steam error). Real Windows, netsh and Steam discovery still UNVERIFIED |
| Version manifest | `manifest/supported-builds.json` | [built], embedded into plugin and installer |
| Interop generator (Cpp2IL + Il2CppInterop, same versions as BepInEx be.788) | `tools/InteropGen/` | [built], produced 205 interop DLLs in 44 s |
| RE helpers (RVA map, xref scanner, disassembler, outline) | `tools/*.py` | used for the analysis below |
| Reproducible build | `build.sh` | [tested-box] |

**Never committed or distributed:** `interop/`, `dump/` (derived from the game's GameAssembly/metadata),
game files. EULA §12 forbids sharing game files. BepInEx (LGPL-2.1) is redistributable. The `unity-libs` zip holds
Unity managed stubs hosted by BepInEx; it is embedded only for offline install. Build without it and the installer
downloads it from the official URL, sha256-verified.

## 2. Pinned stack

| Item | Pin | Evidence |
|---|---|---|
| Game | buildid **26062472**, Unity 6000.0.75f1, IL2CPP metadata v31.1 | Steam acf + local copy |
| Fusion inside the game | **2.1.2.0** (AssemblyVersion of Fusion.Runtime) | interop/Cpp2IL |
| BepInEx | **6.0.0-be.788+5b766a3**, Unity.IL2CPP win-x64, sha256 `f4cc496b…817a` | builds.bepinex.dev; OTH-Unlimited uses it with this game |
| Unity base libs | `6000.0.75.zip`, sha256 `53c15d57…5655` | unity.bepinex.dev |
| Game hashes (lock) | `GameAssembly.dll` `57da2280…b9b4`, `global-metadata.dat` `3908c1a9…c61f` | Steam depot == local copy |

## 3. How LAN works: Fusion internals and hooks

### 3.1 What the game does (Cpp2IL decompile + disassembly, RVAs of build 26062472)
Host: `StateServerBrowser.CreateRoom` (0x9A9250) → `StateStartGame(GameSetupContext)` → `LoadFusionScene` →
`OverlandNetworkRunner.StartGame` (0xCF6970) → `SetupNetworkRunner` (MoveNext 0xCFF6F0; awaits
`FusionRealtimeProxy.GetEnabledRegions` 0x1242590, a Photon name-server call) → `StartGameAsync` (MoveNext 0xD00D30):
region check / `ChangeLobbyRegion`, `GenerateRoomCode` 0xAE6110, `HashRoomCode`, `BuildCustomAppSetting` 0xCF5EA0,
`GetSessionProperties` 0x975BD0, `GetOverlandConnectionToken` 0xAE7090 → **`NetworkRunner.StartGame` call at 0xD01D06**.

Fusion: `NetworkRunner.StartGame` (0x12DCDA0) switches on GameMode. `Single` → `StartGameModeSinglePlayer` (0x12DE0D0,
state machine 0x131C490); everything else → `StartGameModeCloud` → `ConnectToCloud` (Photon). Both end in
`NetworkRunner.Initialize` (0x12BC310). In Initialize, `!args.IsSinglePlayer` requires `_cloudServices` (+0x208) or it
throws, and builds a `NetSocketHybrid` (relay/punch; `CreateCloudSocket` 0x12BBDF0 is **inlined**, 0 xrefs). With
`IsSinglePlayer` it uses `NetSocketNull`. `Simulation` ctor → `Simulation.NetworkInit(INetSocket, NetAddress)` 0x130D230
→ `NetPeer.Initialize`. **`NetSocketNative`** (plain UDP over the shipped `nanosockets.dll`, ctor 0x138B630, Bind
0x138B920) is present and complete in the build. That makes direct IP possible without any relay.

### 3.2 The four detours (`FusionHooks.cs`)
Native detours (`BepInEx.Unity.IL2CPP.Hook.INativeDetour`), not Harmony. StartGameArgs and NetworkRunnerInitializeArgs
are large structs passed by **hidden pointer** (Windows x64 IL2CPP ABI, checked at call sites), and Il2CppInterop
trampolines would edit a boxed copy. OTH-Unlimited's notes report the same issue. Every target has a unique address: no
COMDAT folding in the RVA map. At startup the plugin resolves each pointer via
`Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod` → `MethodInfo*->methodPointer` and compares
`ptr - GameAssembly base` with the manifest RVA. On mismatch it **does not hook**.

| # | Target (RVA) | Native signature | Action when LAN armed |
|---|---|---|---|
| 1 | `NetworkRunner.StartGame` 0x12DCDA0 | `Task(runner, StartGameArgs*, MethodInfo*)` | if GameMode (+0x0) is Host(4)/Client(5): save ConnectionToken (+0xD8) and uniqueId (+0xE0), set GameMode=Single(1) → single-player path (no cloud), afterwards restore `runner.GameMode` backing field (+0x1D8) |
| 2 | `NetworkRunner.Initialize` 0x12BC310 | `Task(runner, InitArgs*, MethodInfo*)` | set SimulationMode (+0x80/+0x84) = Host(2)/Client(4), PlayerCount (+0x78/+0x7C) = N, Address (+0x38/+0x40) = `NetAddress.Any(port)` (client port 0), Config (+0x98) `Simulation.Topology=ClientServer`, `PlayerCount=N`. After return, client calls `runner.Connect(NetAddress.CreateFromIpPort(ip,port), token, uid)` (0x12BAD40) |
| 3 | `NetworkRunnerInitializeArgs.get_IsSinglePlayer` 0x1322F00 | `bool(InitArgs*, MethodInfo*)` | returns true only while #2 runs → no-cloud socket branch. Only 2 callers, both inside Initialize (0x12BC8B9, 0x12BC976) |
| 4 | `Simulation.NetworkInit` 0x130D230 | `void(sim, INetSocket, NetAddress*, MethodInfo*)` | replace the `NetSocketNull` with `new NetSocketNative(None)` |

Hooks are inert unless the user arms LAN in the overlay, so vanilla online keeps working.

### 3.3 Cloud dependencies still in the game flow
- `GetEnabledRegions` (name-server) in `SetupNetworkRunner`, and lobby/`ChangeLobbyRegion` if regions differ. v0.1 keeps
  these and assumes **both PCs have internet** (Radmin needs it anyway). The Join button passes
  `ServerRegion = ConnectedRegion` so no region change is triggered. Fully offline play needs stubs (v0.2).
- Session list / join-by-code (`ContextUIServerJoinByCode.FindRoomByCode` 0x9D1BC0) are lobby-only. **Replaced** by
  join-by-IP.
- `SessionInfo.UpdateCustomProperties/IsOpen/IsVisible` are null-safe without cloud (`_runner?._cloudServices?…`).
- Host token check: "Player tried to connect without providing a valid request token". The client sends the token
  the game itself built (`GetOverlandConnectionToken`), captured in hook #1.
- **PlayerCount must be equal on all peers.** The game uses `Math.Min(MaxPlayers, 4)`; config default 4.

### 3.4 Plugin UX
F7 opens the window (IMGUI). **Host**: "Host LAN" (F8), then the normal *Create Server*. The window shows
`Radmin IP:port` to send to friends. **Join**: type `26.x.x.x:27015`, then "Join" (F9). This pushes the game's own
`StateStartGame(GameSetupContext{GameMode=Client, MaxPlayers=N, ServerRegion=current})`. F10 disarms. If IMGUI is
stripped the window disables itself and the hotkeys plus `BepInEx/config/dev.othlan.lan.cfg` still work.

## 4. Version gate (0.1.6)
- `supported-builds.json`: tested builds with `buildId`, sha256 of `GameAssembly.dll` and `global-metadata.dat`, hook
  RVAs, `codeSig` (per hooked method), `offsets` (struct fields used by the detours), `fusionVersion`, plus the pinned
  BepInEx/unity-libs URLs and hashes.
- Hook addresses always come from the interop `MethodInfo*` (resolved by name); the RVAs are only a cross-check.
- Struct offsets are resolved by name at runtime (`FusionLayout`: `il2cpp_class_get_field_from_name` +
  `il2cpp_field_get_offset`, -0x10 for value types; Nullable members on the field's inflated class). The detours use
  these values, never constants.
- `CodeSig`: Iced decodes each hooked function (length from the x64 `.pdata` entry, leaf functions up to the first
  ret/jmp), zeroes RIP-relative displacements and near branch/call targets, SHA-256 of the result. Relocated code gives
  the same signature; a changed field offset or instruction does not. `tests/SigTool` computes them offline.
- Tiers (`GateLogic`, `BuildGate`):
  1. files match a tested build: run;
  2. files differ, but Fusion 2.1.2.0, every code signature and every resolved offset equal a tested build: one-time
     RU/EN dialog "Game updated, not tested…" [Continue at own risk] / [Disable for now], remembered per GameAssembly
     hash in `Safety.UnverifiedBuildChoices`; F7 offers "enable anyway" after Disable;
  3. anything else: LAN off, F7 says "incompatible, wait for an update".
- `StrictBuildLock` is gone (no bypass). Host and client still compare builds: host info carries `build=` and
  `gahash=`; a different GameAssembly hash blocks the join with a message.
- **Installer**: warns on an untested build and installs anyway (GUI asks Yes/No); the plugin decides at start.

## 4a. Host-assigned slots (0.1.6)
- Before connecting, the client (after `OTHLAN?1` returned the same mod version) sends `OTHLAN?SLOT1 id=<clientId>
  hint=<ip-derived slot>` to UDP port+1; the host replies `OTHLAN SLOT1 slot=<n>` (-1 = full).
- `SlotAllocator` (pure, unit-tested): reservation per client id, 45 s timeout if the client never connects, released
  when Fusion reports the player gone (checked every second), id -> last slot memory so a reconnect gets the same slot.
- The connect token carries slot (byte 0), magic 0x4C (byte 1) and an FNV-1a tag of the client id (bytes 2..5); the host
  refuses a reserved or occupied slot with the wrong tag.
- Fallback: an old host or no reply -> the 0.1.5 IP-derived slot (the host still refuses duplicates).
- `Network.ClientId` (12 hex) is generated once per install.

## 5. Installer (`OTH-LAN-Setup.exe`)
1. Steam root from registry (`HKCU\Software\Valve\Steam\SteamPath`, HKLM fallbacks) → `steamapps/libraryfolders.vdf`
   "path" entries → `appmanifest_2929250.acf` (`installdir`, `buildid`, `StateFlags` = 4 expected). `--game DIR` overrides.
2. Shows Radmin VPN IP (26.0.0.0/8 or adapter "Radmin").
3. Verifies the build (sha256). Unknown build: warning (the plugin decides at game start).
4. Gets payload: embedded → next to exe → download; **sha256-verified** every time.
5. Plans every file (BepInEx zip entries, `BepInEx/unity-libs/6000.0.75.zip` if bundled (private build; the public build lets BepInEx download it),
   `BepInEx/plugins/OTH-LAN/OthLan.dll`). Backs up any existing file to `<game>/OTH-LAN/backup-<ts>/`, records
   created/replaced files and owned dirs in `<game>/OTH-LAN/install-state.json` (written *before* copying, so a crash
   can still roll back).
6. Firewall: `netsh advfirewall firewall add rule name="OTH-LAN UDP <port>" dir=in protocol=UDP localport=<port>
   program=<game exe>`.
7. Uninstall: deletes created files, restores backups, removes owned dirs wholesale (also interop/cache/logs that
   BepInEx generated), deletes the firewall rule, re-verifies game hashes. Re-running install upgrades by rolling back first.
8. Log file next to the exe. Requires admin (manifest), because the firewall rule and Program Files libraries need it.
   Refuses while the game is running.

Box test (linux-x64 build of the same source, fake game dir with real GameAssembly/metadata and a pre-existing
`winhttp.dll`): verify → OK. Install wrote 230 files (229 new, 1 replaced). Simulated BepInEx runtime files; uninstall
gave **sha256-identical tree to before**. Upgrade then uninstall gave identical. Tampered GameAssembly gave a refusal.
netsh is not testable on Linux.

## 6. Versioning
- Semver for the mod: MAJOR = protocol/flow change that breaks mixed versions; MINOR = new supported build or feature;
  PATCH = fixes. **Host and clients must run the same MINOR** (and the same game build; Fusion checks this too).
- Supported builds:

| OTH-LAN | Game buildid | Unity | Fusion | BepInEx | Status |
|---|---|---|---|---|---|
| 0.1.0 | 26062472 | 6000.0.75f1 | 2.1.2.0 | be.788 | compiled; in-game UNVERIFIED |

## 7. Update path when the game patches
1. Steam updates → new buildid. Plugin hash mismatch → LAN disabled automatically, game stays playable online.
2. Dev: copy the new `GameAssembly.dll` + `global-metadata.dat` (own copy), run `tools/InteropGen` → `interop/`,
   `tools/rvamap.py` → find the 4 hook methods by name, re-check they are not COMDAT-folded, re-check the struct
   offsets (`StartGameArgs` GameMode/ConnectionToken, `NetworkRunnerInitializeArgs` fields), and re-disassemble the 2
   `get_IsSinglePlayer` call sites.
3. Add a `builds[]` entry (keep old ones), bump MINOR, `./build.sh`, re-run the in-game test plan.
4. If Unity version changes: update the unity-libs pin. If BepInEx breaks: re-pin a newer be.* after testing.
5. Optional: Steam beta branch / depot download of the older build lets friends stay on a supported build. That is
   the user's own licensed copy, not redistribution.

## 8. Test plan (Windows; PC A = host, PC B = client)
Preconditions: same buildid on both (`OTH-LAN-Setup verify`), Radmin VPN network joined, both ping each other's 26.x IP.
1. **Install**: run installer on PC A → check log, `install-state.json`, firewall rule (`netsh advfirewall firewall
   show rule name="OTH-LAN UDP 27015"`).
2. **BepInEx boot**: start game, wait for interop generation (1-3 min). `BepInEx/LogOutput.log` must show
   `[BuildLock] OK`, four `[Hooks] … RVA 0x…` lines equal to the manifest, and `4 native detours applied`.
3. **Vanilla regression**: without arming, host/join online normally (hooks must be inert).
4. **IMGUI**: F7 window renders; otherwise confirm F8/F9/F10 work.
5. **Host LAN**: F8, Create Server. Expect `Initialize as Host… bind=0.0.0.0:27015`, `Simulation socket =
   NetSocketNative`, and the game world loads. `netstat -ano -p udp | findstr 27015` shows the game listening.
6. **Client LAN** (friend PC): install, F7, enter PC A's Radmin IP:27015, Join. Expect `connecting to …`, then the
   player spawns in host's world. Check both logs for token refusal / PlayerCount mismatch.
7. **Gameplay**: drive together 15+ min, scene/level change, a third player if available, client leave and rejoin,
   host quits (client should get a clean disconnect).
8. **Negative**: wrong IP (timeout message, no crash); different PlayerCount (expect refusal; document it); firewall
   rule removed (expect client timeout).
9. **Uninstall** on both: game files hash-identical to the manifest (`verify`), Steam *Verify integrity* passes.

## 9. Risks (estimates where marked)
| Risk | Likelihood (estimate) | Mitigation |
|---|---|---|
| Client path: Initialize in Client mode + `Connect` after single-player flow does not reach "connected", or StartGame task resolves early and the game proceeds without a connection | medium-high | fallback `Simulation.Client.Connect(ip,port,…)`; hook `StartGameModeSinglePlayer` MoveNext (0x131C490) instead; debug with in-game logs |
| Game code after StartGame expects cloud data (SessionInfo name/region, lobby state, player user IDs) | medium | stub the specific getters once seen in logs |
| IMGUI stripped in IL2CPP build | medium | hotkeys + config already present |
| `methodPointer` points to an adjustor thunk for the struct getter (#3) → RVA mismatch | low | the plugin refuses to hook (safe); switch to the manifest RVA + module base |
| NAT/firewall/Radmin MTU | low-medium | firewall rule; Radmin is a flat L2/L3 net |
| Game update | certain over time | version lock + update path (section 7) |
| EULA §10 (no RE/modding except liveries "or to the extent permitted by applicable law"); §14 Photon interference | legal, not technical | LAN mode does not touch Photon; no game files redistributed; private use among owners |
| BepInEx first-run needs internet for unity-libs | removed | libs pre-placed by installer |

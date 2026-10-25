# Car movement sync in Over The Hill (build 26062472) and what OTH-LAN changes

Sources: the IL2CPP dump on the box (`dump/csall/*.decompiled.cs`, Cpp2IL; method bodies are stubs, so behaviour
comes from type layouts, interfaces, the disassembly with `tools/disasm.py`, and Player.log from live LAN sessions).
"Confirmed" = seen in the dump, the disassembly or a log. "Unconfirmed" = inferred; 0.1.5 logs it at runtime (`[Diag]`).

## 1. Vanilla game

| Topic | Finding | Status |
|---|---|---|
| Topology | Fusion 2.1.2 client/server with host (`GameMode.Host`/`Client`). OTH-LAN forces `Topologies.ClientServer`. | confirmed |
| Car object | `FusionPlayer : NetworkBehaviour, IBeforeClientPredictionReset, IBeforeAllTicks, IBeforeTick` (Assembly-CSharp). Fields `vehicleBase` +0x68, `rigidbody` +0x70. Spawned 0xCF0EF0, BeforeTick 0xCF1A40, GetFrameInput 0xCF1C00, BeforeClientPredictionReset 0xCF1940. | confirmed |
| Who drives the car | Input: `VehicleInput : NetworkBehaviour, INetworkRunnerCallbacks` (Spawned 0xD0F790, Update 0xD11E20) fills `NetworkInputData` (64 bytes: Steering, Throttle, Brake, Clutch, gear, Buttons, camera pose/FOV). `FusionPlayer` stores it in the networked `CurrentFrameInput` / `FrameInputTick` / `PreviousFrameButtons`. | confirmed |
| Authority | The host spawns the cars, so the host has **State Authority**. The owning client has **Input Authority**, so the host simulates every car from that client's inputs. Live log: the client logged "Sending RuntimePlayer for PlayerRef [Player:2]"; host-owned NetworkRigidbodies 1143-1145 were "NOT simulated on the local client". | confirmed in logs; the authority flags themselves are logged per car by `[Diag]` |
| How the host gets client positions | **Inputs, not state.** No position field exists in `NetworkInputData`. Position comes from the host's own physics. | confirmed |
| Vehicle state | `VehicleBase : NetworkBehaviour, IAfterClientPredictionReset` (Vehicle Physics Pro based). Networked: m_time, local velocity/angular velocity/acceleration, last velocities, speed, RNG, and the `NetworkedVehicleSnapshot vehicleSnapshot` (314 words: engine, gearbox, wheels). FixedUpdateNetwork 0xB94CF0, VehiclePhysicsUpdate 0xB916E0, SaveSnapshot 0xB9A740, RestoreSnapshot 0xB9C050, AfterClientPredictionReset 0xBA06D0. | confirmed |
| Transform/rigidbody sync | Fusion Physics addon: `Fusion.Addons.Physics.NetworkRigidbody3D` (`NetworkRigidbody<,> : NetworkRigidbodyBase : NetworkTRSP`, implements IBefore/IAfterClientPredictionReset, IAfterTick). The developers modified it: "Prediction Correction Smoothing (Overland)", with `CorrectionSmoothingTime` +0x98 ("Physics always snaps; only rendering is smoothed"), `CorrectionSnapDistance` +0x9C and `CorrectionSnapAngle` +0xA0. | types confirmed; that the car prefab carries NetworkRigidbody3D is unconfirmed (`[Diag]` lists the components) |
| Physics runner | `OverlandNetworkRunner.runnerSimulatePhysics3D` (`RunnerSimulatePhysics3D`). Its `ClientPhysicsSimulation` field is one of Disabled, SyncTransforms, SimulateForward, SimulateAlways. The live log says the client mode is "predict local physics (ForwardOnly or Always simulate)". | confirmed (exact value logged by `[Diag]`) |
| Client prediction | Yes. The client simulates its own car forward; on every new server snapshot Fusion resets it (prediction reset), VehicleBase restores its snapshot, and the client re-simulates the stored inputs. `FusionPlayer.lastResimulatedFrames` exists. The visual correction is smoothed by CorrectionSmoothingTime. | structure confirmed; parameter values unconfirmed |
| Remote cars | NetworkTRSP interpolation between snapshots (Fusion default). No extrapolation code found. | partly confirmed |
| Forecast physics | Not found. "Forecast" in the game only refers to weather (`ForecastProfile`, Cozy). | confirmed |
| Tick and send rate | Not readable statically: NetworkProjectConfig is a binary-serialized asset (no `TickRateSelection` text in resources.assets or globalgamemanagers). 0.1.5 logs `runner.TickRate`, `DeltaTime`, `InputTransferMode`, `Time.fixedDeltaTime`. | **unconfirmed** |
| Checkpoints / resync | None found beyond Fusion's per-snapshot reconcile, `VehicleBase.Reposition()` (teleport) and host migration (`HostMigrationInfo` Position/Rotation). | confirmed absence in dump (bodies are stubs: best effort) |
| Game ping icon | `HUDManager.pingObject` +0x90 / `pingIcon` +0x98, `UpdatePingIcon` 0x9E4D20: ms = `Simulation.GetPlayerRtt(LocalPlayer)` x 1000. Icon tiers <50/<100/<150/<200/else; `PingIndicatorMode` Always / Over100 / Never. | confirmed (disassembly + constants) |

## 2. What OTH-LAN changes

Up to 0.1.4, nothing about movement: only transport (direct UDP instead of Photon relay), local PlayerRefs and area info.

0.1.5–0.1.9: **CarSync** side channel (`OTP2` poses) + OwnCar / OwnLocal / OwnRender / RemoteSmooth (see §3 historical and
`docs/NETCODE-v4-LOCAL-OWN-CAR.md`).

**0.2.x (current):** movement sync is rebuilt around an explicit car-state stream. Switch remains one host-owned
`[CarSync] Enabled` (F7). Defaults from `docs/DEV-0.2.0-PLAN.md` D-1/D-2/D-3:
`ClientPhysicsMode=Game` (T2.1 `SimulateForward` reverted by migration v9: own car bounced), `ContactAuthority=Owner`, `WorldWinchAuthority=Owner`, `ProxyBody=Kinematic`,
`OwnLocalClock` kept on (T2.4).

| Piece | Owner | Role |
|---|---|---|
| `CarWire` / `OTC3` | Logic | Pose + wheels + flags + redundant history (≤3 states) |
| `CarStreamSender` | both | Sample own car → UDP / host inject |
| `CarRelay` + `RelayLogic` | host (+ client recv) | Accept/relay OTC3; feed `StatePlayback` |
| `StatePlayback` | Logic | Jitter delay, Hermite, epoch snap, capped extrap |
| `HostFollower` | host | Physical proxy of each client car (servo / recovery); still also consumes legacy `OTP2` until T3.8 |
| `RemoteCarView` + `RemoteWheelVisuals` | observers | Draw remotes from stream; kinematic/servo body (T4.0) |
| `InteractionLogic` | both | Owner contact; HostShared for car winch (optional world winch) |
| `SyncProbe` / `tools/sync_compare.py` | both / offline | `[Sync] slot=… seq=… pos=(…)` ring dump for D-1 LIVE (§5.5) |

`OTP2` (`CarSyncLogic` encode/decode) still runs in parallel until **T3.8** (after LIVE §5.1). Do not treat `OTP2` as the
primary remote-view path in 0.2.x.

## 3. Historical CarSync design (0.1.5–0.1.9, `OTP2`)

Kept for archaeology and for the temporary dual-path period. New work should use §2 / OTC3.

- The client is authoritative for its own car. It sends its pose (pos, rot, vel, angVel, seq, Fusion tick) to the host on UDP Port+1 at `SendHz` (20), packet `OTP2` (69 bytes).
- The host applies it to that car's rigidbody. Because the host has State Authority, the result replicates to all other players through Fusion's normal interpolation; that is the smoothing "for others". The host never sends its own corrections.
- **RTT < 150 ms** (Low mode; hysteresis: High above 165, Low below 135): every frame the host blends toward the client pose, extrapolated to the host's current tick ((hostTick - poseTick) x DeltaTime, clamped to +-0.5 s; position by velocity, rotation by angular velocity), with time constant `BlendTau` 0.1 s. Errors above 8 m snap.
- **RTT > 150 ms** (High mode): between checkpoints the host simulates freely from the client's inputs. Every `CheckpointSeconds` (1.5 s) it force-snaps to the client's pose. On desync (more than `DesyncMeters` 3 m or `DesyncDegrees` 25 deg) it snaps immediately.
- Poses older than 1 s are ignored.

### Where Fusion fights this (documented, not solved yet)

1. On the owning client, Fusion's prediction reset still snaps the client's own car to the host snapshot every tick, then re-simulates. With CarSync the host snapshot follows the client, so the reset should land near the client's own pose. It is still a correction, and in High mode between checkpoints it is a real one. A "client never corrected" mode would need to suppress the reset for the input-authority car. **0.1.7: implemented (section 7), on by default.**
2. On the host, the pose is written in Unity `Update`, between Fusion ticks. The next tick simulates from it and Fusion captures the result. Order relative to `NetworkRunnerUpdaterDefault` is unconfirmed.
3. VehicleBase's own networked snapshot (wheel and engine state) is not touched; only the rigidbody is.

## 4. Ping indicator source (0.1.5, revised 2026-10-21 01:05)

The label uses ONLY our UDP echo over the real Radmin path, never Fusion or cloud figures.
- **Client:** 5 pings per second to `host_radmin_ip:27016`. The host replies to the packet's source port.
- **Host:** pings each client back on the endpoint learned from that client's pings; the client echoes. The label shows the worst client.
- No reply for more than 2 s: the label shows `local --` in red. Loss is the unanswered share of the last 20 pings.
- **Firewall:** only the HOST needs inbound UDP 27016 (the installer adds 27015,27016 when run as admin). Clients only receive replies on their own outbound socket.
- Fusion's RTT appears only as a comparison line in F7.

## 5. On/off switch and propagation (0.1.5, 2026-10-21 08:04)

- `[CarSync] Enabled` defaults to **true**. Config migration: `[Meta] ConfigVersion`. A cfg below version 2 gets
  `Enabled = true` (a warning is logged if it was false, because older builds wrote false as the default), then version 2.
  A user who switches it off afterwards stays off.
- **Host authoritative.** The host's setting is carried as a flags byte (bit0) appended to the 27016 ping packets
  (replies to client pings and the host's own pings; 19 bytes, old 18-byte peers still parse) and as `carsync=0/1` in the
  info reply. A client sends poses only while the latest host flag is 1 and less than 5 s old, so a client can never be
  half-on. Client F7 shows a greyed button `CarSync: ON/OFF (управляет хост)`.
- **Toggle:** F7 button on the host (or when not connected) calls `CarSync.UserToggle`, which writes the cfg (persisted) and
  takes effect at once; clients follow within about 0.2 s (5 pings/s). The autopilot's `--othlan-toggle` calls the same function.

## 6. Design review (2026-10-21)

### Fixed in this pass
1. **Extrapolation time.** Was RTT/2 + time since receipt. The client predicts *ahead* of the host, so its pose for tick T
   arrives roughly when the host is at tick T as well; RTT/2 overshot by about v x RTT/2 (2.8 m at 100 km/h, 200 ms).
   Now age = (hostTick - poseTick) x DeltaTime. No clock sync is needed: Fusion tick numbers are shared.
2. **Rotation not extrapolated** (only position). Now rotated by angular velocity x age.
3. **Sequence restart.** After a client reconnect its seq restarts at 1 and every pose was rejected as old forever.
   Now a lower seq is accepted after 2 s of silence. Out-of-order and duplicate packets are still dropped.
4. **Pose source.** Poses are accepted only from the endpoint that slot pings from (no stray or old process can move a car).
5. **Echo timeout.** RTT was replaced by Fusion's figure when the echo timed out (mode flapping); now the mode is held.
6. **Respawn / teleport / area change.** A gap over `TeleportMeters` (50 m) is not applied; Fusion's own teleport wins.
   Before, the host snapped a client car across a 1284 m spawn gap every frame.
7. **GC per frame.** Per-frame list, dictionary snapshot per car and per-packet arrays removed.
8. **Host flag** (section 5) instead of "must be enabled on host and clients".

### Open: needs a decision
| # | Issue | Recommendation |
|---|---|---|
| A | Fusion still resets the client's own car to the host snapshot each tick (prediction reset). | **Decided in 0.1.7:** restore-after-resim for the own car (section 7), default on, with safety fallbacks. |
| B | High mode snaps at every checkpoint even when the error is tiny; a snap overwrites velocity and contacts. | Skip the checkpoint snap below about 0.25 m / 3 deg. |
| C | Collisions: the client's car collides with other cars as the client sees them; the host then pulls it back to the client pose, so knocks given by others are undone and cars can push into each other. Inherent to client authority. | Accept for co-op driving; optionally pause blending for about 0.5 s after a car-car contact on the host. |
| D | Wheel, suspension, engine and gearbox state (VehicleBase snapshot) are not synced, only the rigidbody. Engine sound and RPM come from host simulation of the same inputs, so they match; wheel compression after a snap may twitch for one frame. | Leave. |
| E | Ping label is orange from 150 ms; mode switches High at 165 / Low at 135 (hysteresis). | Keep the hysteresis; document it (or switch at 150/140). |
| F | Poses are written in Unity Update between Fusion ticks, not in FixedUpdateNetwork, and snaps do not use NetworkRigidbody Teleport, so other players interpolate across a snap (a short slide instead of a jump). | Acceptable for small snaps; revisit if remote cars visibly slide. |
| G | Low mode also blends velocity and angular velocity. | Keep (otherwise the host drifts away again between poses). |
| H | Trust model: any peer that pings as slot X could claim slot X's endpoint. | Fine for a friends LAN over Radmin; no change. |

### Correct as is
- Host's own car is never touched. RTT used by the host is the host's own echo to that client (same path as the client's label).
- 3-4 players: everything is per slot. Mid-session join: the flag arrives with the first ping reply.
- Old versions: join refuses a mod mismatch; old `OTP1` and new `OTP2` packets ignore each other; old peers ignore the flags byte.
- CPU: one pass over cars per frame, no per-frame allocations left except Unity calls.

## 7. 0.1.7: outages and the client-authoritative own car

### Measured (self-test on one PC, in-process outage emulation of Fusion + side channel, 2026-10-22)
| Fact | Value |
|---|---|
| Fusion `ConnectionTimeout` / `ConnectionShutdownTime` | 30 s / 1 s (logged by `[Diag]`); `NetworkConditions.Enabled=False` |
| Client simulation without server packets | **freezes** after ~38-42 ticks (~0.85 s): own car and tick stop; on recovery the tick jumps forward and the car is reset to the host state |
| Host during a client outage | **repeats the client's last confirmed input** (FrameInputTick frozen): the car keeps driving (circles at 20-30 km/h in the test); error ~18 m after 12 s, ~48 m after 30 s |
| Disconnect | after 30 s; the car disappears on the host; the slot is kept 45 s (0.1.6 slot logic) |
| Vanilla rollback per prediction reset, normal driving | 0.005-0.03 m, ~5 resets/s; after outages 3.5 / 9.6 / 5.3 m |
| Contact box from colliders (Car_Protector) | half extents 0.91 x 2.12 x 1.99 m from 7 colliders (cfg fallback 1.1 / 2.4 / 1.2) |

### Host outage phases (per slot, g = time without a fresh pose; `[Outage]` cfg)
normal < 0.15 s; extrapolation 0.15-1.5 s (velocity lerp toward the last pose velocity with tau = 0.1 / confidence,
yaw only, rotation horizon 0.25 s, no pull toward a frozen point); from 1.5 s (`BrakeFromSeconds`, default 1.5 since
o1m/o2m) gentle braking (exp, ~1 s; label "⚠ нет связи N с" over the car for everyone: the host broadcasts `OTST` at 5 Hz).
With `BrakeFromSeconds = 5` the old physics-only phase 1.5-5 s returns. Measured link-back error (one PC, in-process
outage, client phase 2): BrakeFrom 5 → 7.1 / 14.3 / 14.3 m after 3 / 4.9 / 6 s; BrakeFrom 1.5 → 3.2 / 2.7 / 1.1 m (o2m),
1.3 / 4.3 / 1.8 m (o5); 12 s → 4.8-6.2 m at 80-92 deg (snap: the host copy still turns under the repeated input). In the air: no yaw
pull, no braking. Recovery on the first fresh pose: quiet (< 0.5 m and < 5 deg), converge over
T = clamp(0.3 + 0.08 err, 0.3, 1.5) s (no snaps meanwhile), snap 15-50 m (waits up to 2 s for a contact to end, then
converges), > 50 m teleport (left alone). Flipped (> 60 deg): converge over 0.5 s; never pulled to a flipped client pose
while the host car is upright and the error < 1 m.

### Client own car
- **Phase 2 (default on, `OwnCar.ClientAuthoritative`, F7):** `FusionPlayer.BeforeClientPredictionReset` (0xCF1940)
  captures the own rigidbody state; at the first forward `FusionPlayer.BeforeTick` (0xCF1A40) after the resimulation the
  state is put back and the NetworkRigidbody correction offset reset. Fusion still runs; only the visible result for the
  own car is ours. The host follows the client through CarSync. Active only when CarSync is on at the host
  (host flag 1) and both hooks pass the optional-hook gate (RVA on the known build, code signature otherwise).
- Safety: take the host pose for 1.5 s when it is > 25 m away, turned > 30 deg (reset upright, rescue), when the host
  sends `OTAC` (it moved the car itself: `CarSyncLogic.HostJumped`), or when > 8 m persists for 3 s; a non-finite or
  exploding own state (v > 150 m/s, w > 60 rad/s) falls back to phase 1 for 10 s.
- **Phase 1 (fallback):** after > 0.3 s without resets, 1.5 s of `CorrectionSmoothingTime` 0.5 s / `CorrectionSnapDistance` 20 m.
- Because the client freezes during an outage, the host's repeated input moves the host copy away from the frozen client;
  with phase 2 the host converges back to the client after the link returns. Measurements: docs/DEV-0.1.7-LOG.md.

### Frozen client tick after heavy loss (FreezeWatch)
- Self-test n1/n2/n3: 50 % loss per direction (Fusion datagrams dropped at `NetSocketNative.Send/Receive`) for 30 s →
  the client's `Runner.Tick` stops for good (also with `own=measure`, i.e. without the phase 2 restore), the host's
  `FrameInputTick` for that player stops too, Player.log: "could not add input … buffer was full"; no disconnect (packets
  keep flowing, the 30 s timeout never fires). 30 % sometimes does the same. Whether vanilla Fusion without our hooks
  does it over a real lossy link is not verified (clumsy needs admin).
- Mitigation (client, `Outage.AutoRejoinOnFreeze`, default on): tick unchanged ≥ 12 s while the side channel is healthy
  ≥ 5 s (no timeout, loss ≤ 15 %) → the game's QuitToMenuAsync, then the normal join to the same host (host info + slot
  request → the same slot). One attempt per minute, at most 3. Self-test n4: two freezes (after 50 % and after 30 %),
  both rejoined into slot 2, back in the world ≈ 22 s after detection.

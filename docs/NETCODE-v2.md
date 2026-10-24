# OTH-LAN netcode v2 (0.1.8): design

Status: design written 2026-10-22 before coding; sections marked **[measured]** are filled in from the self-test
(`docs/DEV-0.1.8-LOG.md`). Requirement (the reporter, 2026-10-22): the client is never snapped back or rubber-banded; its own
car is simulated locally with zero input lag and stays smooth through lag spikes and drops; the host follows the client;
remote cars may be slightly off, never one's own car. Ping compensation is ON by default and can be switched off in F7.

## 1. What went wrong in 0.1.7 (root causes, from the 15:12 logs and the Fusion 2.1.2 code)

1. **Input buffer starvation (bug #5, and part of #3).** Fusion's client keeps every input it has not seen a server
   snapshot for in `Simulation.Client._inputBuffer`. Its capacity is one second of ticks (50 at TickRate 50). When no
   snapshot arrives for ~0.7 s (client is ~0.2 s ahead at 200 ms RTT), the buffer is full and `PollInput` returns null:
   the own car gets **no input** for those ticks and the host gets **no new inputs** either. the reporter's Player.log has
   478 `could not add [Player:2] input ... because buffer was full` lines; they cluster in the last ~9 minutes
   (ticks 158 000–183 700), in bursts every 1–2 s. That is snapshot stalls of ≥ 0.7 s from host to client, repeating.
   Our echo ping (19-byte packets, 5/s) stayed "normal", so the ping label hid it. Every stall = throttle/steer dropped
   to zero for a moment + a large correction when snapshots resume = "very jerky control while ping is fine".
2. **Phase 2 restored the own car too late and only half (bug #3).** The pre-reset pose was put back in
   `FusionPlayer.BeforeTick` of the first forward tick. Fusion copies the "previous" state used for render
   interpolation **before** that (`StepSimulation`: copy Previous, then `InvokeTick`), so every snapshot made the render
   interpolate from the host-based resimulated pose to the client pose: a visible jump per snapshot. Only the rigidbody
   was restored; Vehicle Physics Pro's own state (wheel spin, suspension, engine, gearbox: `VehicleBase` networked
   snapshot) stayed host-based, so the forces did not match the body.
3. **Hard corrections by design.** Above 150 ms RTT (Witness's link is ~200 ms) CarSync ran the "High" mode: the host
   simulated freely and snapped every 1.5 s or at 3 m / 25°. The client then took the host pose on several rules
   (host turned > 30°, OTAC "host moved the car" from a jump heuristic, "> 8 m for 3 s"). The 15:12 BepInEx log shows
   11 "host moved the car" and 6 "host pose turned 31–84°" accepts: each is a rollback of the own car.

## 2. Design

### 2.1 One authority per car
Each car is owned by the machine of its driver. The host owns its own car; a client owns its own car. Fusion's
state authority (the host) is kept for everything else (world, winch, objects, damage, inventory).

### 2.2 Client: own car (ping compensation ON)
- **Input never starves.** On connect the client raises `_inputBuffer._capacity` to `InputBufferSeconds` (default 8 s)
  of ticks and enlarges `_inputArray` accordingly. Inputs keep flowing to the host through snapshot stalls and the own
  car keeps getting the player's input. Fusion only sends the newest 3–6 inputs per packet, so a larger buffer costs no
  bandwidth. If the client tick ever jumps backwards (time re-sync), the stale future inputs are cleared.
- **Own state survives every prediction reset.** `FusionPlayer.BeforeClientPredictionReset` (own car) captures the
  rigidbody (position, rotation, velocity, angular velocity) and a raw copy of the networked words of `VehicleBase` and
  `NetworkRigidbody3D`. Right after `Simulation.Client.RunClientSideResimulationLoop` returns (new detour) — before any
  forward tick and before Fusion copies the previous state for rendering — all of it is put back, `VehicleBase.RestoreSnapshot()`
  re-applies the VPP state, and the NetworkRigidbody correction offset is cleared. The old `BeforeTick` restore stays as
  a fallback check (counts "late fixes"). Result: Fusion never changes the own car's physics or its rendering.
- **When the client takes the host's pose (only these):** a game teleport on the host (respawn, rescue, jack, map
  change), signalled by OTAC from the host's teleport detector; a pose more than `AcceptMeters` (25 m) away; an invalid
  local physics state (NaN, > 150 m/s). Removed: the 30° rule, the "8 m for 3 s" rule, the High mode checkpoints.
- **Ping compensation OFF (F7, host decides):** vanilla Fusion (host-authoritative, corrections), but still with the
  larger input buffer.

### 2.3 Host: follows each client's car
- Poses (OTP2, 20 Hz, unchanged wire format + tick) are extrapolated to the host tick: `age = (hostTick - poseTick) * dt`,
  clamped to 0..0.5 s; position by velocity, rotation by angular velocity.
- **One continuous mode for every RTT** (no High/Low switch): every frame the host blends position/rotation toward the
  target with `BlendTau` (0.1 s) and sets velocity/angular velocity to the client's (others see the car move like the
  client drives it). Errors above `TeleportMeters` (50 m) are a game teleport: hands off.
- **Outage / stall:** up to 1.5 s without poses: extrapolate the last pose (confidence fades); after that the host copy
  brakes gently and waits. When poses come back: converge over `T = clamp(0.3 + 0.08·err, 0.3, 1.5)` s (no snap below
  15 m; above, snap outside contact). The client is never told to move.
- **Contact / winch:** as 0.1.7 (pause blending while touching another car or winching).
- **Game teleport detection (OTAC):** the host's own teleport of a client car is detected by a jump that the blend did not
  cause (as 0.1.7) **plus** a hook-free check against the client's pose: only a jump that moves the host copy *away*
  from the client pose by > 8 m counts. That removes the false OTACs seen at 200 ms.

### 2.4 Remote cars on a client
Fusion interpolates them between host snapshots. During a snapshot stall Fusion's interpolation runs out and they freeze,
then jump. v2 adds a visual-only layer (`RemoteSmooth`): when a remote car's rendered pose stops changing while it was
moving, it is extrapolated by its last velocity for up to 0.5 s (fading); when real data resumes the difference is
decayed over ~0.25 s instead of a jump. Only the render transform is touched, in `LateUpdate`, and restored before the
next Fusion update, so physics and collisions are unaffected. **Not implemented in 0.1.8** (see `docs/DEV-0.1.8-LOG.md`): the render hierarchy of the cars is not known well enough to
touch it safely; postponed.

### 2.5 Reconnect, map change, respawn
- On a new runner / reconnect / rejoin: all buffers (poses, captured states, convergence, input-buffer patch, time
  stats) are reset; phase "client" starts only after the spawn teleport (as 0.1.7).
- Map change = new Fusion session = new runner: same reset.
- Respawn on the client (spawn map) = host teleport = OTAC = take the host pose.

### 2.6 Protocol and versions
Version string 0.1.8; 0.1.7 peers are refused by the existing mod-version check with a localized message that names
both versions (host info `mod=` and discovery list). CarSync flag bit 0 now means "ping compensation" (client
authority + host follower) for the whole session.

### 2.7 Diagnostics (beyond the plan)
`NetStats`: snapshot gaps (count > 0.25 / 0.5 / 1 s, max), input-buffer-full events, prediction resets/restores,
own-car render smoothness (per-frame deviation from constant velocity: p50/p95/p99/max, frames > 0.1 m and > 0.5 m),
remote-car smoothness. Logged every 30 s in a session and shown in F7. The F7 ping line keeps the UDP echo only; a
"Fusion stalls" line shows what the echo cannot see.

## 3. Trade-offs
- **Cheating:** a client can drive its car anywhere. Acceptable for a friends' co-op LAN mod.
- **Collisions between cars** are resolved on each side separately: the client's car bumps into its *view* of others;
  the host's copy follows the client even through a contact (blending paused while touching). Two cars pushing each other
  may look different on the two screens for a moment.
- **Towing (winch)** with a client car: the host simulates the rope; CarSync pauses while a winch involves the car, so
  the host's physics is authoritative during a tow and the client... keeps its own car. A tow between two players can
  therefore feel weaker on the towed client than on the host. Documented; not changed in 0.1.8.
- **Host-side game effects on the client car** (rescue, jack, map change) need OTAC; if that packet is lost twice,
  the client keeps its pose until the next 25 m check.
- **Larger input buffer** = more inputs resimulated after a long stall (CPU), bounded by 8 s.
- **Remote car smoothing** hides stalls but shows extrapolated (possibly wrong) positions for up to 0.5 s.

## 4. Test plan
- Unit tests (`tests/LogicTests`): follower decision table, convergence time, OTAC classifier, input-capacity math,
  smoothness metric, version-mismatch message.
- Fault injection (self-test only): `--othlan-fnetem t:delayMs:jitterMs:stallEveryS:stallMs,...` delays Fusion datagrams
  in both directions on the client (queue in the `NetSocketNative` detours), plus the existing outage and loss.
- Runs on the test PC (two instances): before (0.1.7 a5 code) vs after (0.1.8) with 200 ms RTT + 30 ms jitter + 0.8 s stalls every 2 s;
  outages 1/3/6/12 s; 30 % loss; reconnect; Local Co-op 3 instances; menu navigation; mods toggle; join from the list.
- Numbers: own-car per-frame deviation (p99, frames > 0.1 m), restores/accepts, input-buffer-full count, host follow error.

## 5. Results [measured] (self-test, two instances on one PC, 2026-10-22)
Lag profile for both sides of the comparison: Fusion datagrams delayed 100 ms ± 30 ms each way, plus a 0.8 s stall
every 1.5 s between t=30 s and t=200 s; the mod's own channel +200 ms. "Hitch" = per frame, distance between the drawn
position and where the car would be at last frame's velocity (`Smoothness`), sampled after rendering.

| Client's own car on screen | 0.1.7 (bm1, same sampling) | 0.1.7 (bl1, Update sampling) | 0.1.8 (am1) | 0.1.8, compensation off (amv) |
|---|---|---|---|---|
| p95 hitch | 30.4 cm | 14.8 cm | 5.9 cm | 6.8 cm |
| p99 hitch | 126.6 cm | 36.7 cm | 17.1 cm | 18.8 cm |
| max hitch | 688 cm | 1095 cm | 148 cm | 192 cm |
| frames > 10 cm | 1566 | 1106 | 333 | 399 |
| frames > 50 cm | 443 | 69 | 38 | 38 |
| frames > 2 m | 64 | 6 | 0 | 0 |
| host-side view of that car, frames > 10 cm | 316 | 934 | 72 | — |
| inputs dropped by Fusion | 0 | 0 (6 with compensation off, bl0) | 0 | 0 |

bm1 = 0.1.7 netcode measured with the 0.1.8 sampling (after rendering): the fair comparison. bl1 = same code, older
sampling in Update. The remaining hitches are single frames that carry a resimulation (30–50 ms frames), not
rollbacks: they also occur with compensation off. Outage, loss and rejoin results: `docs/DEV-0.1.8-LOG.md`.

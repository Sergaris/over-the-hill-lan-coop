# Roadmap

Legend: **[fact]** — checked in code or logs; **[estimate]** — an assumption or a proposed number, tuned in tests.
Thresholds below are **[estimate]** unless stated otherwise.

## Status

- **0.2.8.1** — remote cars smoothed again (shared jitter pose); client slot 0 no longer assigned (empty world).
- **0.2.8** — Steam build 26489201 Supported; smoother remote cars and winch; host brake lights no longer flicker; camera stays steady.
- **0.2.7** — client: own-car re-simulation skipped when its result is discarded anyway (about 35–40% cheaper at high ping); follow camera on the car's draw clock (no shake on uneven frames).
- **0.2.6.3** — installer: verified BepInEx payload (0.2.6.1/0.2.6.2 embedded a truncated zip).
- **0.2.6.2** — remote headlights Off/Low/High on Steam build 26326013; single Continue/Disable dialog.
- **0.2.6.1** — Steam build 26326013 Supported; Canvas Unverified dialog with Continue/Disable; VehicleInput optional signature; known issue: remote headlights not yet on that build.

- **0.1.7** — ping colours and "no link" label, connection-loss compensation, client-authoritative own car, freeze
  watchdog with auto-rejoin, **Local Co-op** button and LAN lobby list. Verified with scripted runs of 2–4 game
  instances on one PC (outages 1–12 s, 50 % loss, rejoin, full lobby, version gate).
- **0.1.8** — all mod texts follow the game's language live (EN and RU complete, other languages fall back to EN);
  netcode v4: the client's own car runs on the local clock like single player, other players' cars come from Fusion's
  interpolation buffer, short freezes are smoothed (all on by default); update checker; five 0.1.7 reports fixed.
  Measured with scripted runs of 2–4 game instances on one PC with simulated ping and loss, **not yet on two real PCs**.

- **0.1.9** — remote lights/wheels/liveries under interpolation; pre-join Modio livery download dialog;
  `OTH-LAN-Launch.exe` for InteropManager unity-libs hang; host PosePlayback; F7 no longer auto-opens on join.

## Next (after 0.2.7)

Source: code reading of 0.2.7, not reproduced in-game in that review, except the accessory symptom (confirmed in a real two-player session on 2026-11-12). **[code]** means it is visible in the source; **[hypothesis]** means a likely cause, not proven in-game here. Do not ship a fix without a live check.

### Accessory jitter on the client — do this first

- **Symptom:** any accessories hung on a car (a coiled rope, decorative lamp, antenna, and the like) jitter heavily when viewed as a client. They look fine from the host. Confirmed in a real two-player session (2026-11-12).
- **Cause [code + hypothesis]:** the mod has no accessory handling (no rope / lamp / antenna / joint / cloth code). Those objects run on each PC's local physics or animation. On the client, another player's car root is written twice per frame with different poses:
  - `RemoteCarView.PrePhysicsStep` → `ApplyProxyBody` moves the kinematic body to `SampleAt(stepEnd, delayOverride: 0)` with `MovePosition` (the freshest, often extrapolated pose);
  - `DrawOne` in LateUpdate then overwrites the same root with `SampleAt(now)` and the jitter-buffer delay of 50–250 ms, via `root.SetPositionAndRotation`.
  - The comment around `RemoteCarView.cs:34` says so directly: visual LateUpdate overwrites the same root transform.
  At speed those two poses can be decimetres to metres apart, so anything hung on the car that is simulated or that follows frame-to-frame movement gets yanked between them every frame.
  The client's own car also jumps outside the physics step (`OwnRender.Apply`, `BeforeSim` / `OwnLocal`, `OwnCar.Restore`); accessories are not in the snapshot. On the host there is one pose per frame (`HostFollower` keeps the proxies as dynamic bodies) plus the game's normal render interpolation, which matches "looks fine from the host".
- **Accessory type [unverified]:** a rigidbody on a joint, Cloth, or a spring/jiggle script — in the game's assets, not in the mod.
- **Fix direction:** one root pose per frame (move the visual delay onto a child visual transform, or drive the body and the visual from the same sample); and/or teleport or freeze child Rigidbody/Joint objects with the root (same delta, or make them kinematic and drive them visually).
- **Live check:** the host hangs a rope / lamp / antenna; the client watches that car moving and at rest — no jitter, no worse than the host. No regression on the client's own car or on other cars (hitch, lights, wheels). Do not ship without a yes.

### StatePlayback race

- **Symptom [code, not caught in-game]:** `Push` runs on the receive thread (`CarRelay.OnClientPacket` / `OnPacket`, `HostFollower` paths marked as network thread), while `SampleAt` runs on the main thread, with no locks. Torn or reordered samples are possible, which would show up as rare one-frame pops on other players' cars. `CarRelay._lock` only protects the dictionary.
- **Fix direction:** `Push` into a concurrent queue that the main thread drains before `PrePhysicsStep` and `DrawOne`. Also audit `HostFollower.OnPosePacket`, `OwnCar.OnAccept`, `LinkLabels.OnStatus`.

### Slot rebinding via ping

- **Symptom [code]:** `HostPings.SawClient` rebinds a slot's endpoint from any valid ping that carries that slot number, which can bypass `RelayLogic.Filter`. The side channel has no authentication or encryption. Fine for a trusted LAN; not fine on the open internet.
- **Fix direction:** a session token handed out with the slot, binding the endpoint; HMAC on packets. Trusting a client for its own car pose stays a separate, deliberate compromise.

### CI and `OwnResimSkip` after a lag spike

- The `OwnResimSkip = false` workaround after a bounce / dead steering is still needed. The cheap-skip paths (`CheapPrevious` / `RestorePreviousOnly`, a stale capture, `Refused++` mid-loop) are a plausible risk; there is little test coverage of the spike itself. The "35–40% cheaper" figure is from a loopback changelog measurement, not a real network. The private repo has no CI.
- **Fix direction:** CI with `LogicTests` and `EchoLoopback` under netem at 200/400 ms and 5–20% loss, plus an `OwnResimSkip` lag-spike scenario. Also fewer empty `catch` blocks and fewer leftover experimental flags.

Order: accessory jitter first, then the receive-queue race, then the slot token and CI as needed. Fixing the double pose write will likely also remove some micro-jitter of other players' cars.

## 1. Still to verify in the real world

- A real two-player session over a network already works well as of 2026-11-12. Still to verify: 3–4 players
  over Radmin VPN / Hamachi / ZeroTier with real ping 150–300 ms.
- Whether LAN broadcast discovery reaches other PCs over each VPN (directed `26.255.255.255` on Radmin,
  `255.255.255.255`, Radmin's neighbour table); behaviour when Radmin falls back to a relay/TCP connection.
  **Add by IP** is the fallback.
- SmartScreen behaviour for the unsigned installer.
- A real full outage between two PCs (e.g. briefly disabling the VPN) instead of the in-process emulation.
- Changing the language through the game's Settings menu UI by hand (the scripted test switches the game's locale
  the same way the menu does).
- Gamepad navigation on the Local Co-op screen; 1440p/4K UI scale.

## 0.1.9: finish testing and the netcode (postponed from 0.1.8)

### To test
- Measurement runs cancelled before 0.1.8: 15 % and 30 % packet loss, 5 % loss with 1–2 s outages, 400 ms ping after
  the last drawing fix. Repeat every netcode case 2–3 times (today 1–2 runs each; p99 varies up to ×1.5 between runs).
- The same cases on 0.1.7 for a full comparison (remote view, 400 ms, loss); a solo drive on the same route as reference.
- 4 players with the new defaults: other players' cars as each observer sees them.
- Collisions and contact, winch and towing between players with the new modes (passed once each so far).
- Respawn: moves by the game shorter than 10 m slide smoothly instead of jumping; needs explicit respawn detection.
- Freeze watchdog and rejoin with the new modes; resimulation cost per frame on slow PCs.
- Skipped before the 0.1.8 build: a short run with the final defaults, the public DLL in the game, the update check on
  the final build, config migration from a real 0.1.7 config, the F7 off-confirmation and warning by hand.

### To implement
- Redundant client state stream with a jitter buffer on the host, Hermite playback and soft convergence, only if real
  data shows the need (host view today: p99 9–17 cm, 0–3 jumps over 50 cm, none over 2 m).
- Smoothing or extrapolation of other players' cars during long snapshot gaps (up to 2.3 s seen).
- Remove or rebuild the unused remote smoothing path (OTH cars have no InterpolationTarget).
- A safer fallback for F7 "compensation off" than vanilla Fusion, which can throw cars back by 5–16 m under loss.
- Re-check the Fusion internals the mod depends on after every game update; fallback paths.

### To verify live
- A real session on two PCs over a virtual LAN, then 3–4 real players.
- Installer: UAC elevation (install / uninstall / check), the self-contained setup in a real update, the real GitHub
  download hosts, SmartScreen.

## 2. Later

- More translations: the game ships 16 languages; the mod has EN and RU. A translation is one JSON file
  (`src/OthLan.Plugin/Lang/<code>.json`), checked by the unit tests for missing keys and placeholders.
- Contact box per car model (colliders are measured at runtime today; the config values are fallbacks).
- Better diagnostics in F7 for "host not found" cases (which discovery channels answered).

## 3. Design notes

### Connection-loss compensation (host side; g = time without a fresh client pose, poses arrive at 20 Hz)
| Phase | g | Host behaviour |
|---|---|---|
| Normal | < 150 ms | Last pose + extrapolation to the host tick. Partial loss up to ~30 % lands here. |
| Extrapolation | 0.15–1.5 s | Position from the last velocity up to 1.0 s, rotation from angular velocity only up to 0.25 s; confidence fades 1 → 0 over 1.5 s; no pulling towards a frozen point. |
| Braking | > 1.5 s | Velocity → 0 over ~1 s; the car stands with a "⚠ no link N s" label. The slot is kept for 45 s; a rejoin gets the same slot. |

Recovery thresholds: < 0.5 m and < 5° — nothing; 0.5–15 m — soft convergence; 15–50 m — snap outside contact;
> 50 m — game teleport, left alone. Details and measurements: `docs/CAR-SYNC.md`.

### Local Co-op discovery
- Request/response, no permanent beacons: the Local Co-op screen sends `OTHLAN?DISC1 nonce=<n> mod=<ver>` to UDP 27016
  every 2 s; hosts reply unicast with name, players, versions, area and CarSync state. A row disappears after 6 s
  without a reply.
- Targets: directed broadcast on the Radmin subnet from a socket bound to the Radmin IP, `255.255.255.255` and the
  directed broadcast of every active IPv4 adapter, connection history, the manual list, Radmin ARP neighbours.
- **No scanning of address ranges** — Radmin spreads addresses over 26.0.0.0/8 and scanning would look like a network
  scan.
- Hosts answer only while hosting; there are no hidden or password-protected lobbies.
- Discovery replies are parsed as fixed-format key/value lines with size and rate limits; no commands.

### UI
- The Local Co-op button is a runtime clone of the game's Multiplayer button and the screen reuses fonts and colours
  from the running game; **no game assets are copied into the repository or the mod**.
- F7 (IMGUI) stays as the fallback for manual IP join, diagnostics and the CarSync switch, in case a game update
  breaks the menu integration (the button then simply does not appear).

## 4. Ideas (backlog; difficulty is an estimate)
| Idea | Difficulty | Why |
|---|---|---|
| MudRunner-style cargo delivery | Fairly easy | Cargo as an object/trailer, load and unload points, jobs and rewards — mostly logic and UI on top of existing cars; cargo sync over LAN is extra work. |
| Getting out of the car on foot | Hard | The game has no on-foot character: model, animations, controller, camera, world interaction and LAN sync would all be new. |
| Hoverboard car | Medium | Different physics (no wheel grip) on top of the vehicle physics, own controls, CarSync compatibility. |

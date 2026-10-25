# Netcode v4: "the lagging player plays solo" (design + status, 0.1.8 → 0.2.x)

**Status 0.2.x:** OwnCar / OwnLocal / OwnRender remain; remotes and host proxies are driven primarily by the **OTC3**
stream (`CarWire` → `CarStreamSender` / `CarRelay` → `StatePlayback` → `RemoteCarView` / `HostFollower`). See
`docs/CAR-SYNC.md` §2 and `docs/DEV-0.2.0-PLAN.md`. Legacy `OTP2` still parallels until T3.8 (after LIVE §5.1).
`ClientPhysicsMode=Game` is the default again (migration v9); `SimulateForward` (v6) made the own car bounce and
ignore steering after prediction resets (0.2.3 steer A/B), so it is opt-in only.

Requirement (project owner, 23 Oct 11:54): the client must barely feel lag, as if playing single player. A smart simulation
layer carries the whole load: everyone else sees the high-ping player as if their connection were fine; the high-ping
player feels as if alone.

## 1. What actually breaks today (measured, test PC, per-tick trace)

| finding | evidence |
|---|---|
| Own car is **not** rolled back. The after-resim restore puts back our exact pre-reset state; the host's opinion (2-4 cm off) is discarded. | v3t fl1 trace: restore error == host-vs-us difference, restored pose == pre-reset pose |
| **Fusion 2.1 lead cap:** a client never predicts more than TickRate ticks (1 s) past the latest host snapshot (`Simulation.Update`: target tick = min(clock, LatestServerTick + TickRate)). At the cap Fusion stops ticking; physics is stepped only by Fusion ticks, so the car **stands still**, then the next snapshot runs 7-14 ticks in one frame: **lurch**. | fl1: all 93 freezes start exactly at tick − serverTick = 50; p6on (600 ms): lead p50 45, p90 49, max 50 → 643 freezes in 230 s, on-screen >50 cm hitches 286 |
| This is Fusion core, identical with ping compensation OFF. | fv0/RC (CarSync off) under stalls: same hitch counts; p6off run pending |
| At steady 200/400 ms ON and OFF are equal in simulation (Fusion-clock p99 ≤ 1.4 cm, 0 >50 cm); on-screen differences are frame pacing. | p2on/p2off/p4on/p4off |
| Every snapshot resimulates the whole physics scene for ~RTT worth of ticks (40 ticks at 600 ms): ~10 ms at 200 ms, much more at 600 ms → frame-time spikes. | trace R→S 10.2 ms at 200 ms, n=40 at 600 ms |

## 2. Target architecture and how it maps onto Fusion 2.1

### 2.1 Own car (lagging client) — single-player physics
1. **No corrections, ever** (done since 0.1.8 v2): resim result discarded, full state restored (rigidbody, VPP words,
   NRB words, previous-tick words). Host pose only on game moves (OTAC: respawn/rescue/jack) or > 25 m.
2. **Local clock (new, `OwnLocal`, `[Netcode] OwnLocalClock`, experimental):** the own car is stepped by wall-clock
   time. When Fusion is frozen at the lead cap, we step the car locally (VPP `FixedUpdateNetwork` + one physics step +
   NRB buffer) with the last input; Fusion's catch-up ticks beyond the real-time budget are undone for the own car.
   Result: the car never stops and never lurches, whatever the host link does.
3. **Render continuity (new, `OwnRender`, `[Netcode] OwnFreezeSmoothing`, default on):** safety net after the NRB
   render: extrapolate through freezes, carry and decay (τ 0.1 s) any residual < 2 m toward OUR simulated state
   (never toward the host), snap > 2 m (respawn).
4. Later: skip the physics part of resimulations entirely (they only matter for the own car, which discards them)
   once remote cars are interpolated (2.3) → `ClientPhysicsSimulation = SimulateForward` on clients; removes the
   40-tick resim CPU spikes.

### 2.2 Stream client → host (exists: CarSync side channel)
Today: pose + velocity + angular velocity + Fusion tick at `SendHz` 20, unreliable. Planned: 30-50 Hz, each packet
carries the last 3 states (redundancy survives loss/bursts), wall-clock stamp for jitter measurement, VPP inputs
(throttle/brake/steer) for dead reckoning.

### 2.3 How others see the lagging player
* **Host:** the physics proxy is driven by the velocity follower (v3 servo, done) toward a *jitter-buffered,
  time-shifted* target: playback delay = measured jitter p95 (adaptive), Hermite interpolation between buffered
  states, dead reckoning with last velocity/inputs when the buffer runs dry, gentle convergence when data resumes,
  teleport only on respawn events (TeleportOnGameMove, done).
* **Other clients:** see the host's proxy; on clients remote cars should be Fusion **interpolated** (not predicted):
  `Runner.SetIsSimulated(proxy, false)` + kinematic rigidbody → Fusion's adaptive interpolation buffer is exactly a
  time-shifted jitter-buffered playback. Removes remote-car corrections and the resim cost. Risk to check: VPP visuals
  (wheel spin/steer, sounds) on non-simulated proxies.

### 2.4 Collisions (accepted trade-off)
Each owner is authoritative for its own car. A client hits other cars as kinematic bodies at their interpolated
(slightly past) pose; the host's proxy of the client follows the client stream, contact pause while touching (done).
Contacts are therefore soft/approximate: no shared impulse; each owner's physics decides its own car's reaction.
Ramming a lagging player moves him only on his own screen by what his own physics computes. Documented, not fixed.

## 3. Status / measurements
See DEV-0.1.8-LOG.md. Suites: v3f (OwnRender), v3g (OwnLocal on: p6L, s6L, fl1L, fj3L, fx3L; plus p6on/s6on/s6off
references). Baseline: 0.1.8 OFF (CarSync off ≈ 0.1.7 OFF: vanilla Fusion prediction) under the same netem.

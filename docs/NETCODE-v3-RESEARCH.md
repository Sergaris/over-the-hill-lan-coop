# OTH-LAN netcode v3 (0.1.8): research, root cause of remote-car jumps, plan

Written 2026-10-23 (Europe/Helsinki). Sources were opened and read between 07:25 and 07:50 that morning. Measurements
come from the test-PC self-tests in section 3. Our own car is out of scope here: it stays as NETCODE-v2 describes
(simulated locally, never pulled back, no input lag). This document covers the **other players' cars**, both on the
host and on every client.

## 1. Sources and what each one means for OTH-LAN

The setup they apply to: Fusion 2.1.2 in host mode, the Physics addon with `SimulateAlways`, the game's
IL2CPP code, which we cannot change, and Vehicle Physics Pro cars.

| # | Source | Key point | Applies to us? |
|---|---|---|---|
| S1 | Photon, *Networked Physics* overview, <https://doc.photonengine.com/fusion/v2/manual/physics/physics-overview> | Fusion 2.1 offers three ways to network physics: Forecast (extrapolates remote bodies to local time), Forecast Disabled (proxies in remote time) and the Physics addon (predict and resimulate, the most expensive). | The game uses the addon. We cannot switch it to Forecast without changing its NetworkProjectConfig, so we take the *idea* of Forecast (remote cars extrapolated, locally smoothed) into a visual layer. |
| S2 | Photon, *Physics Addon 2.1*, <https://doc.photonengine.com/fusion/v2/addons/physics-addon-2.1> | `InterpolationTarget` is a child without colliders that Fusion moves in `Render()`. Physics never reads it. | This is the hook we use: we can change the drawn pose of a remote car without touching physics. Verified at runtime by the car hierarchy dump (section 3). |
| S3 | Photon, *Time Synchronization*, <https://doc.photonengine.com/fusion/v2/manual/advanced/time-synchronization> | The interpolation delay adapts to snapshot jitter (a percentile, "Max Late Snapshots", "Redundant Snapshots"). | Fusion already has an adaptive jitter buffer, so we do not build a second one. Decompiled `SimulationTimeSync` (SDK 2.0 on the box) shows what happens on a stall: once interpolation time passes the newest snapshot, it **clamps to it** (the car freezes, with no extrapolation). A gap of more than 1 s in server ticks calls `TimeSync.Reset`, and an offset error above the threshold calls `ResetInterpolationOffset`: in both cases the interpolation time jumps and the car **jumps**. This is the client-side jump mechanism. |
| S4 | Photon, *Advanced Forecast Vehicle Physics* sample, <https://doc.photonengine.com/fusion/v2/technical-samples/advanced-forecast-vehicle-physics> | Photon's own vehicle sample gives each player authority over their own car and switches authority over objects to the nearest player. | This confirms the v2 choice ("own car authoritative on its owner"). Authority switching of props is not possible from a mod in host mode. |
| S5 | Fiedler, *State Synchronization*, <https://gafferongames.com/post/state_synchronization/> | Apply authoritative state **hard** in the simulation. Smoothing at the simulation level makes the physics extrapolate from a "made-up" state, and quantisation or position forcing pushes bodies into penetration, which "fights against the simulation". Hide the pops **visually** with a position/rotation error offset that decays exponentially, slowly for small errors (0.95 per frame) and fast for big ones (0.85), blended between 25 cm and 1 m. Lower the max depenetration velocity. | Main design input. The host follower must not drag bodies through other objects. The visual smoothing is in `SmoothLogic.cs` with an adaptive time constant (0.25 s small, 0.1 s large). |
| S6 | Fiedler, *Snapshot Interpolation*, <https://gafferongames.com/post/snapshot_interpolation/> | An interpolation buffer of about 3x the send interval survives 2 lost packets. Extrapolation of rigid bodies "doesn't work very well" for long horizons. Hermite interpolation needs velocities. | Fusion already does this. We extrapolate only during a real stall, only up to 0.5 s, and fade it out. |
| S7 | Fiedler, *Networking for Physics Programmers*, GDC 2015, <https://www.gdcvault.com/play/1022195/Physics-for-Game-Programmers-Networking>, slides <https://www.gamedevs.org/uploads/networking-for-physics-programmers.pdf> | Priority accumulator: each object accumulates priority × dt, and the n highest are sent. Jitter buffer: hold packets about 100 ms. | Our pose channel carries one car per client at 20 Hz (69 B), so bandwidth is not the bottleneck: not needed now. Fusion's own snapshot scheduling is internal. |
| S8 | Murphy, *Believable Dead Reckoning for Networked Games*, Game Engine Gems 2 (2011), <http://www-inf.telecom-sudparis.eu/COURS/MultiplayerCourse/new_site/Supports/Documents/Misc/believableDeadReckoningForNetworkedGames.pdf> | Projective velocity blending: blend velocities, project from where we are and from the last known state, then lerp. It is less wiggly than Bézier or Hermite at irregular update rates. | We do not get raw remote states on a client (Fusion interpolates them), so full PVB is not possible on the client. Its principle (never jump, blend toward the new trajectory with continuous velocity) is what the bounded error decay does. On the host, the velocity servo (S10) is the physics-level equivalent. |
| S9 | Valve, *Source Multiplayer Networking*, <https://developer.valvesoftware.com/wiki/Source_Multiplayer_Networking> | Default interpolation 100 ms. When snapshots run out, extrapolate linearly for at most 0.25 s (`cl_extrapolate_amount`). | Same idea as our stall extrapolation (`RemoteExtrapolateSeconds` 0.5 s; cars move more steadily than FPS players). |
| S10 | Epic, Unreal physics replication error correction (`FRigidBodyErrorCorrection` in `UPhysicsSettings`), <https://dev.epicgames.com/documentation/en-us/unreal-engine/API/Runtime/Engine/UPhysicsSettings> (field reference: <https://unrealdocs.net/docs/physics-settings-in-the-unreal-engine-project-settings/>) | Replicated rigid bodies are corrected **through velocity**: `dx/dt = k (x_target - x)` (LinearVelocityCoefficient). `PositionLerp` "should be very low, if not 0; a higher value increases precision but may cause jerky movement". Hard snap only above `MaxLinearHardSnapDistance` or after error accumulation. | Basis of the host follower v3 (`CarSync.FollowByVelocity`). |
| S11 | Cone (Psyonix), *It IS Rocket Science*, GDC 2018, <https://gdcvault.com/play/1024972/It-IS-Rocket-Science-The> (video <https://www.youtube.com/watch?v=ueEmiDM94IE>). Cone on reddit: <https://www.reddit.com/r/RocketLeague/comments/8m1aed/> | Server-authoritative, fixed 120 Hz physics. The client predicts everything, including other cars, by extrapolating their last input. The input buffer on the server grows and shrinks with jitter, and up to 10 inputs per packet are sent. | RL predicts remote cars with their **inputs** at a 120 Hz custom physics tick, which is out of reach for a mod on VPP/PhysX (resimulating 4 VPP cars per snapshot is the cost S1 warns about). We already have redundant inputs in Fusion and an 8 s input buffer (v2). |
| S12 | Ford (Blizzard), *Overwatch Gameplay Architecture and Netcode*, GDC 2017, <https://gdcvault.com/play/1024001> | Prediction plus rollback for the local player, and the server adapts the client's send rate (it simulates faster or slower) when packets are lost. | Fusion does the time dilation itself (S3: `_simPID`, `_interpPID`). Nothing to add. |
| S13 | Aldridge (Bungie), *I Shot You First: Networking the Gameplay of Halo: Reach*, GDC 2011, <https://gdcvault.com/play/1014345/I-Shot-You-First-Networking> | Host-authoritative, with per-object, per-client priority (distance, visibility, speed) competing for bandwidth. | Relevant only if bandwidth becomes the limit (Radmin over Wi-Fi). Noted as future work (a lower pose rate for parked cars). |
| S14 | Unity, *Netcode for Entities: Prediction*, <https://docs.unity3d.com/Packages/com.unity.netcode@1.0/manual/prediction.html> | Interpolate most remote entities and predict only what the player interacts with. | Matches our split: own car predicted, others interpolated plus visual smoothing. |

### Techniques considered and rejected (with reason)

- **Input-based prediction of remote cars (RL style, S11).** We would have to resimulate VPP for every remote car on
  every snapshot. That is a huge CPU cost on the game's physics, and VPP's internal state is only partly networked.
  Not feasible safely from a mod.
- **Switching the game to Fusion Forecast physics (S1).** It is a NetworkProjectConfig change inside the game's asset,
  it is untested by the developers, and it would change the own-car path that v2 has just verified.
- **Our own jitter buffer for snapshots (S3, S5, S7).** Fusion already adapts its interpolation delay to jitter, and
  a second buffer would only add latency.
- **Hermite or cubic interpolation between snapshots (S6).** Fusion does the interpolation. Our layer works on its
  output, and Murphy (S8) reports PVB-style blending at least as good as splines at irregular rates.
- **Priority accumulator or delta compression (S7, S13).** Measured bandwidth is not a problem (one pose packet of
  69 B at 20 Hz per client; Fusion's own deltas are internal).

## 2. Ranked plan (by measured or expected effect on "a remote car jumps more than 2 m")

1. **Visual smoothing of remote cars, on clients AND the host** (`RemoteSmooth.cs` + `SmoothLogic.cs`), in LateUpdate
   after Fusion's Render (Fusion's default player loop: Render right after the Update scripts, from the decompiled
   `NetworkRunnerUpdaterDefault`):
   - dead reckoning only while host snapshots are actually stalled, and only while Fusion holds the car still, for
     at most 0.5 s;
   - every discontinuity above 10 cm per frame becomes an error offset that decays (adaptive time constant 0.25 s →
     0.1 s, as in S5), with the correction speed capped at 6 m/s + 0.5·v;
   - teleports above 15 m are shown at once;
   - steady motion passes through untouched, so no latency is added.
2. **Host follower by velocity** (`CarSync.FollowByVelocity`, S10/S5): the host's copy of a client car is steered
   through its velocity and angular velocity toward the client's extrapolated pose, with a small direct rotation
   term and a position term only below 0.5 m. No more per-frame position writes that can push the body into other
   bodies.
3. **Metrics per observer** (`NetStats` + `NetTrace`):
   - p50/p95/p99/max hitch of the drawn remote cars, with >0.5 m / >2 m counts;
   - the same for Fusion's raw pose ("raw"), so smoothing is A/B-measured in every run;
   - 30 s windows;
   - one log line per remote hitch > 0.5 m with context (snapshot age, CarSync action, error);
   - self-test CSVs: `{tag}-remote.csv` (every remote car, every frame) and `{tag}-follow.csv` (host follower input
     and output per car per frame).
4. Future work (not needed by the data so far): pose priority for parked cars (S13), and lowering the depenetration
   velocity of host copies (S5) if contact jumps remain.

## 3. Root cause of the remote-car jumps (measured)

Status 2026-10-23 10:40 (Europe/Helsinki). Test-PC runs v3c (09:51) and v3d (10:06-10:39), dense per-frame traces
(`{tag}-remote.csv`, `{tag}-follow.csv`), compared with the 0.1.8 RC runs from 22 Oct (fc4 = after9; fl1, fn1, fv0 =
the RC suite).

### 3.1 Clients: predicted proxies + the game's 2 m snap (confirmed)

- `[Diag]`: `ClientPhysicsSimulation=SimulateAlways`. A client simulates every car, the other players' too, in its own
  predicted timeframe. In the trace the client's tick is about 15-20 ticks (0.3-0.4 s) ahead of the server tick of
  the newest snapshot.
- `[NetTrace]` car dump: `interpTarget=-`, and the rigidbody is on the root. Fusion's InterpolationTarget path does not
  exist on this prefab, so `RemoteSmooth` was a no-op (logged "no separate InterpolationTarget, not smoothed",
  noTarget=3 per client). The InterpolationTarget risk was real.
- v3c fc4, client2, 91.59 s: C1's car goes from +5 m/s to 0 within one frame and is drawn 2.2 m back (hitch 2.61 m).
  The host's own view of the same car at the same server ticks (6763-6769) is smooth: max hitch 0.18 m, the car
  brakes from 5.2 to 1.7 m/s. The client predicted the braking car 0.4 s ahead at full speed. The correction was
  more than the game's `NetworkRigidbody3D.CorrectionSnapDistance` = 2 m (an Overland modification: "corrections
  larger than this snap instead of being smoothed"), so it was drawn in one frame.
- Fix (v3d, `RemoteSmooth.TuneProxy`): only for the other players' cars on a client, smoothing 0.1 → 0.25 s, snap 2 →
  10 m, 30 → 90°. The game's code smooths only the rendering ("physics always snaps"), so the simulation does not
  change. Result: 0 remote jumps > 2 m on all observers in five runs, worst 100 cm (fc4p client3, once).
- The snapshot-gap/TimeSync hypothesis from the 08:00 draft is **not** the main cause. Gaps up to 2.3 s still happen,
  with no jumps above 2 m after the fix, because the proxy is predicted through the gap and the correction is now
  smoothed.

### 3.2 Host: position blend vs physics (partly confirmed)

- Same build v3d, fc4 (velocity servo) vs fc4p (old position blend): host-view remote cars, > 50 cm: 0 vs 7; max
  22 vs 77 cm. Follow trace: body jumps > 0.5 m not explained by velocity: 0 (servo) vs 3 (blend; 0.53-0.6 m, err
  1.6-1.7 m, no contact flag).
- The 2-4 m periodic spikes of the 22 Oct run did not come back in fc4p (0 > 2 m with blend). The fc4p run had
  2103 contact frames (cars touching), the servo runs 0. The collision part of the hypothesis is therefore not
  tested under the servo.

### 3.3 Before/after (per instance; own = own car on screen p99 cm / max cm / >50 cm; own Fusion clock p99 / max / >50 cm / >2 m; remote p99 / max / >50 cm / >2 m)

RC = 0.1.8 RC (22 Oct), c = v3c (servo, no proxy tuning), d = v3d (servo + proxy tuning), d-fc4p = v3d with the old
position blend. RC remote p99 was not recorded ("-").

| run | instance | own on screen | own (Fusion clock) | remote cars |
|---|---|---|---|---|
| fc4 | client | 11.3 / 35 / 0 | 10.4 / 35 / 0 / 0 | - / 297 / 8 / 6 |
| fc4 | client2 | 10.3 / 116 / 3 | 3.6 / 17 / 0 / 0 | - / 165 / 4 / 0 |
| fc4 | client3 | 7.2 / 29 / 0 | 1.7 / 12 / 0 / 0 | - / 256 / 8 / 2 |
| fc4 | host | 0.0 / 4 / 0 | 0.0 / 1 / 0 / 0 | - / 281 / 22 / 2 |
| c-fc4 | client | 13.6 / 88 / 2 | 11.3 / 64 / 2 / 0 | 9.7 / 84 / 1 / 0 |
| c-fc4 | client2 | 11.2 / 50 / 1 | 4.7 / 20 / 0 / 0 | 10.5 / 261 / 4 / 1 |
| c-fc4 | client3 | 7.2 / 18 / 0 | 4.7 / 10 / 0 / 0 | 5.9 / 39 / 0 / 0 |
| c-fc4 | host | 0.0 / 4 / 0 | 0.0 / 1 / 0 / 0 | 8.5 / 81 / 2 / 0 |
| d-fc4 | client | 17.1 / 42 / 0 | 15.1 / 43 / 0 / 0 | 5.5 / 20 / 0 / 0 |
| d-fc4 | client2 | 6.1 / 19 / 0 | 2.0 / 12 / 0 / 0 | 7.8 / 67 / 1 / 0 |
| d-fc4 | client3 | 7.0 / 16 / 0 | 1.5 / 15 / 0 / 0 | 5.7 / 31 / 0 / 0 |
| d-fc4 | host | 0.0 / 6 / 0 | 0.0 / 1 / 0 / 0 | 8.7 / 22 / 0 / 0 |
| d-fc4p | client | 6.3 / 24 / 0 | 3.4 / 12 / 0 / 0 | 4.2 / 14 / 0 / 0 |
| d-fc4p | client2 | 3.0 / 10 / 0 | 2.4 / 10 / 0 / 0 | 3.9 / 28 / 0 / 0 |
| d-fc4p | client3 | 3.7 / 37 / 0 | 2.0 / 14 / 0 / 0 | 3.2 / 100 / 1 / 0 |
| d-fc4p | host | 0.0 / 4 / 0 | 0.0 / 2 / 0 / 0 | 7.4 / 77 / 7 / 0 |
| rc-fl1 | client | 19.3 / 564 / 27 | 9.9 / 828 / 53 / 5 | - / 0 / 0 / 0 |
| rc-fl1 | host | 0.0 / 3 / 0 | 0.0 / 0 / 0 / 0 | - / 72 / 1 / 0 |
| d-fl1 | client | 20.3 / 209 / 17 | 6.3 / 209 / 36 / 1 | 0.0 / 0 / 0 / 0 |
| d-fl1 | host | 0.0 / 3 / 0 | 0.0 / 0 / 0 / 0 | 9.6 / 35 / 0 / 0 |
| rc-fn1 | client | 14.0 / 47 / 0 | 0.4 / 5 / 0 / 0 | - / 0 / 0 / 0 |
| rc-fn1 | host | 0.0 / 3 / 0 | 0.0 / 0 / 0 / 0 | - / 50 / 1 / 0 |
| d-fn1 | client | 17.4 / 60 / 4 | 1.1 / 8 / 0 / 0 | 0.0 / 0 / 0 / 0 |
| d-fn1 | host | 0.0 / 3 / 0 | 0.0 / 0 / 0 / 0 | 10.1 / 43 / 0 / 0 |
| rc-fv0 | client | 19.7 / 205 / 34 | 9.5 / 205 / 50 / 1 | - / 0 / 0 / 0 |
| rc-fv0 | host | 0.0 / 3 / 0 | 0.0 / 0 / 0 / 0 | - / 66 / 1 / 0 |
| d-fv0 | client | 18.8 / 178 / 32 | 13.1 / 245 / 55 / 1 | 0.0 / 0 / 0 / 0 |
| d-fv0 | host | 0.0 / 3 / 0 | 0.0 / 0 / 0 / 0 | 8.8 / 37 / 0 / 0 |

Remote cars: RC fc4 had 10 jumps > 2 m (max 297 cm); v3d fc4 + fc4p had 0 (max 100 cm); fl1/fn1/fv0 0 before and after.
Host view: max 50-72 cm → 35-43 cm.

Own car: no change in own-car code. The numbers move with the run: fc4 client p99 15.1 (servo) vs 3.4 (fc4p) in the
same build. fv0 has CarSync off and the host car parked, so v3 changes nothing for that client's own car there, yet it
gives 9.5 → 13.1 cm (Fusion clock). Most own hitches > 30 cm in d-fc4 have resets+0/restores+0, i.e. terrain, not
network. fl1 (1.5 s freezes): own on screen max 564 → 209 cm, >50 cm 27 → 17, > 2 m (Fusion clock) 5 → 1.

### 3.4 Offline replay (box, before the test-PC data)

`tools/replay` fed the 22 Oct host trace (about 13 Hz) through `RemoteSmoother`: p99 287 → 66 cm, > 2 m 35 → 0. The
in-game smoother cannot attach to this prefab (3.1), so this number is not what ships. The fix that ships is the
proxy tuning.

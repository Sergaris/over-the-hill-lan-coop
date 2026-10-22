# Changelog

All notable changes. Versions follow `MAJOR.MINOR.PATCH`; host and clients must run the same version.

## Unreleased (i18n)

- **Mod UI follows the game's language.** Every user-visible text (F7 window, Local Co-op button and screen, ping
  label, "no link" car labels, dialogs, status lines) comes from string tables (`src/OthLan.Plugin/Lang/*.json`).
  The mod watches the game's selected locale and re-renders immediately, without a restart. English and Russian are
  complete; the other game languages fall back to English. The BepInEx log stays in English.
- The Local Co-op button and screen keep their own texts when the game's localization system re-applies the cloned
  menu button's original text after a language change.
- Installer: English or Russian by the Windows display language, `--lang en|ru` to override.
- Build: `-p:Interop=<dir>` and `-p:BepInExCore=<dir>` point the build at your own interop / BepInEx copies.

## 0.1.7

Final regression (scripted multi-instance runs on one PC): outages, packet loss, rejoin, 4-player Local Co-op, version
gate — PASS. Not verified: two PCs over Radmin with real players, SmartScreen behaviour.

### Ping
- Colours: green ≤ 200 ms, **yellow** 200–300 ms (was orange from 150), red > 300 ms. Config keys
  `PingIndicator.YellowAboveMs` (200) and `RedAboveMs` (300).
- Config migration v3: the old defaults 150/250 become 200/300; values you changed by hand are kept.
- "no link N s" (red) after 0.6 s without an echo reply, instead of "local --" after 2 s; back to normal after 3 replies
  in a row. Right after joining, "local --" (waiting) is shown for up to 10 s while the host is still loading.
  Your own label is still hidden while F7 is open.

### Connection loss (host side, per player; thresholds in config `[Outage]`)
- < 0.15 s: as before. 0.15–1.5 s: extrapolation from the last velocity with decaying confidence, without pulling the
  car back to a frozen point. From 1.5 s: gentle braking (~1 s); everyone else sees "⚠ no link N s" above the car.
  The player slot is kept for 45 s, as in 0.1.6.
- Braking starts at 1.5 s rather than 5 s (`BrakeFromSeconds`), based on measurements: during an outage the client's
  own simulation stops after ~0.85 s while the host kept replaying its last input and drove away; position error on
  reconnect dropped from 6–14 m to 1–5 m. `BrakeFromSeconds = 5` restores the 1.5–5 s "physics only" phase.
- **Freeze after heavy loss:** if the client's network tick has been stalled for ≥ 12 s while the mod's own channel to
  the host is healthy again (≥ 5 s without timeout, loss ≤ 15 %), the mod returns to the menu and rejoins the same host
  (same slot); at most once a minute, up to 3 times. Config `[Outage] AutoRejoinOnFreeze` (on). Found by the self-test
  at 30–50 % loss.
- Recovery: < 0.5 m and < 5° — nothing; 0.5–15 m — soft convergence over T = clamp(0.3 + 0.08·err, 0.3, 1.5) s without
  snapping; 15–50 m — snap, but not while cars are in contact (waits up to 2 s); > 50 m — a game teleport, left alone.
- In the air rotation is neither extrapolated nor braked; an upside-down car converges over 0.5 s and is not pulled to
  the client's flipped pose if the host has it on its wheels.
- The contact box is measured from the car's colliders at runtime; the config values are fallbacks.

### Client's own car
- **Client-authoritative mode is on by default** (`OwnCar.ClientAuthoritative = true`, F7 button): the network layer
  no longer rolls the client's own car back to the host snapshot; the host follows the client (CarSync). Safeguards:
  accept the host pose if it is > 25 m away, rotated > 30°, the host reports that it moved the car itself (jack,
  respawn, teleport — `OTAC` packet), or a > 8 m divergence persists for 3 s; an invalid physics state falls back to
  the soft mode for 10 s.
- After a connection loss the "host pose rotated > 30°" rule is off for 4 s (`OwnCar.PostOutageSeconds`), because the
  host copy kept turning under the replayed steering input.
- Host: if snapping a client car does not stick (6 in a row < 0.25 s apart), the client is told (`OTAC`) to take the
  host pose and the host leaves the car alone for 1 s.
- Soft mode (fallback when client-authoritative mode is off or inactive): after an outage > 0.3 s, the network
  corrections are softened for 1.5 s (0.5 s smoothing, snap from 20 m).

### Local Co-op in the main menu
- A **Local Co-op** button below Multiplayer (a clone of the game's button with its font and style; no game assets
  are copied into the mod).
- A screen listing every OTH-LAN lobby on the network: host name, players now/max, coloured ping, mod version with a
  compatibility mark, **Join**; a full lobby is refused with a clear message.
- Discovery every 2 s: directed broadcast on the Radmin subnet from the Radmin socket, `255.255.255.255` and a directed
  broadcast on every adapter, connection history, **Add by IP**, config `JoinAddress`, Radmin ARP neighbours,
  127.0.0.1. Protocol `OTHLAN?DISC1`.
- **Become host**: the only setting is the number of players, 2–4 (default 4), then the game's own region and area
  selection.
- The host's `OTHLAN 1` reply gains `name=`, `cur=`, `max=`. Lobby name: config `LocalCoop.HostName`, else the Steam
  name, else the PC name.
- If the game's menu changes, the button simply does not appear; F7 works as before.

### Other
- Optional hooks (client prediction reset) are verified by RVA and code signature; on an unknown build without a match
  client-authoritative mode stays off and everything else works.

## 0.1.6 and earlier

Direct-IP LAN mode (F7/F8/F9/F10), host-assigned player slots with rejoin, version gate with code-signature checks
for untested game builds, ping label, CarSync smoothing, installer with full uninstall.

---

*По-русски:* список изменений по версиям. Главное в i18n: все тексты мода следуют языку игры и переключаются сразу;
русский и английский полные, остальные языки игры показываются по-английски.

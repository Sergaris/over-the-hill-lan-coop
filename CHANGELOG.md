# Changelog

All notable changes. Versions follow `MAJOR.MINOR.PATCH`; host and clients must run the same version.

## 0.1.9.3

Both players need 0.1.9.3. From 0.1.8.1+ the in-game updater offers this release; from 0.1.8 or older install by hand once.

- Pre-join: enable menu runner AllowMods before Modio asks (RequestTempMod no-ops while ModsAllowed is false).
- Do not restart AddTempModAsync on a timer (can abort installs); refresh only. Join without mods still available.

## 0.1.9.3 — изменения

У обоих игроков должна быть 0.1.9.3. С 0.1.8.1+ обновление предложит меню; с 0.1.8 и раньше — вручную один раз.

- Перед Modio на menu-runner включается AllowMods (иначе RequestTempMod молчит).
- AddTempModAsync не перезапускается по таймеру; только Refresh. «Войти без модов» по-прежнему есть.
## 0.1.9.2

Both players need 0.1.9.2. From 0.1.8.1 / 0.1.9 / 0.1.9.1 the in-game updater offers this release; from 0.1.8 or older install by hand once.

- Pre-join Modio download calls AddTempModAsync (RequestTempMod alone often no-ops after boot). Registry change hook + diagnostics.
- Gate button Join without mods: join with this client's AllowMods off if Modio stalls.

## 0.1.9.2 — изменения

У обоих игроков должна быть 0.1.9.2. С 0.1.8.1 / 0.1.9 / 0.1.9.1 обновление предложит меню; с 0.1.8 и раньше — вручную один раз.

- Скачивание Modio вызывает AddTempModAsync (один RequestTempMod после boot часто молчит). Хук реестра + диагностика.
- Кнопка «Войти без модов»: вход с AllowMods=false у клиента, если Modio завис.
## 0.1.9.1

Both players need 0.1.9.1. From 0.1.8.1 / 0.1.9 the in-game updater offers this release; from 0.1.8 or older install by hand once.

- Pre-join Modio gate waits for Modio init/auth (terms if needed), retries download, refreshes the livery registry;
  clearer status. Dialog is an in-game Canvas (no F7). Variant ids show as unsigned. Enter = Download, Esc = Cancel.
- Update check: if GitHub cannot be reached, a main-menu notice appears (Retry / Later) instead of failing silently.

## 0.1.9.1 — изменения

У обоих игроков должна быть 0.1.9.1. С 0.1.8.1 / 0.1.9 обновление предложит меню; с 0.1.8 и раньше — установите вручную один раз.

- Гейт Modio перед входом ждёт init/auth (условия при необходимости), повторяет скачивание, обновляет реестр
  раскрасок; понятнее статусы. Диалог — Canvas в игре (без F7). Варианты как unsigned. Enter = Скачать, Esc = Отмена.
- Проверка обновлений: если GitHub недоступен, уведомление в главном меню (Повторить / Позже), а не тихий сбой.
## 0.1.9

Both players need 0.1.9. From 0.1.8.1 the in-game updater offers this release; from 0.1.8 or older install by hand once.

- Other players' headlights, brake/reverse lights and wheel spin/steer work again when remote interpolation is on.
  Host Modio liveries re-apply on clients. Pre-join dialog lists missing Modio liveries with Download / Cancel.
- `OTH-LAN-Launch.exe`: if BepInEx hangs more than 10 s downloading Unity base libraries, fetches `6000.0.75.zip`
  from the release into `BepInEx/unity-libs/` and restarts.
- F7 (client): live toggles for remote interpolation, own-car freeze smoothing, and export diagnostics. F7 no longer
  auto-opens when joining a host.
- Own-car local clock is cheaper and pauses during car contact / winch. Remote cars keep moving through short snapshot
  gaps. Host pose playback (jitter + Hermite) on by default; SendHz 20 → 30.
- Version is three-part `0.1.9` so both 0.1.8 and 0.1.8.1 updaters can see it.

## 0.1.9 — изменения

У обоих игроков должна быть 0.1.9. С 0.1.8.1 обновление предложит меню; с 0.1.8 и раньше — установите вручную один раз.

- Снова работают фары, стоп/задний ход и вращение колёс чужих машин при интерполяции. Мод-раскраска хоста снова
  применяется на клиентах. Перед входом — диалог недостающих Modio-раскрасок со Скачать / Отмена.
- `OTH-LAN-Launch.exe`: если BepInEx больше 10 с висит на скачивании Unity base libraries, качает `6000.0.75.zip`
  из релиза в `BepInEx/unity-libs/` и перезапускает игру.
- F7 (клиент): живые переключатели интерполяции чужих, сглаживания заморозки и экспорт диагностики. F7 больше не
  открывается само при входе к хосту.
- Локальные часы своей машины дешевле и на паузе при контакте / лебёдке. Чужие машины едут через короткие разрывы
  снапшотов. На хосте playback поз (jitter + Hermite) по умолчанию; SendHz 20 → 30.
- Версия из трёх чисел `0.1.9`, чтобы её видели обновлялки и 0.1.8, и 0.1.8.1.
## 0.1.8.1

Both players need 0.1.8.1. From 0.1.8 or older, update by hand once (download the zip, run OTH-LAN-Setup.exe → Install).

- Game update of 23 October 2026 (Steam build 26136884) is supported. 0.1.8 and 0.1.7 show "incompatible" on it and
  keep LAN off.
- F7 on the client: the "Own car without host rollback" switch is back, marked experimental. It turns
  `[Netcode] OwnLocalClock` on or off for this PC only and works at once, even mid-race. If your own car twitches or
  jumps, turn it off here.
- Update check: four-part versions (0.1.8.1) are understood: 0.1.8 < 0.1.8.1 < 0.1.9. 0.1.8 cannot read them, which is
  why the one manual update is needed.

## 0.1.8.1 — изменения

У обоих игроков должна быть 0.1.8.1. С 0.1.8 и более ранних версий обновитесь вручную один раз (скачайте zip, запустите OTH-LAN-Setup.exe → «Установить»).

- Поддерживается обновление игры от 23 октября 2026 (сборка Steam 26136884). 0.1.8 и 0.1.7 показывают на ней
  «несовместимо» и не включают LAN.
- F7 у клиента: вернулся переключатель «Своя машина без отката к хосту», с пометкой «экспериментально». Он
  включает и выключает `[Netcode] OwnLocalClock` только на этом ПК и действует сразу, даже посреди заезда. Если ваша
  машина дёргается или прыгает, выключите его здесь.
- Проверка обновлений: понимает версии из четырёх чисел (0.1.8.1): 0.1.8 < 0.1.8.1 < 0.1.9. 0.1.8 их не понимает,
  поэтому и нужно одно ручное обновление.

## 0.1.8

Both players need 0.1.8: a 0.1.7 game is refused with a message that names both versions.

### Network code (ping compensation)
- High ping and lag spikes: your own car now runs on your PC's clock, like in single player. Fusion (the game's
  network engine) stops simulating when the host's data is more than 1 s old, then catches up in one frame; at
  600 ms ping that happened several times a second (car stops, then lurches). Your car now keeps driving with your
  current controls while Fusion waits, and Fusion's catch-up is not applied to it a second time. Measured on one PC
  at 600 ms ping, own-car jumps over 50 cm in about 3 minutes: 0.1.7 with compensation off 165, on 305, now 1-2;
  with 5 % packet loss at 400 ms: 1, and no jump over 2 m. Config: `[Netcode] OwnLocalClock`, `OwnFreezeSmoothing`
  (both on).
- Other players' cars on a laggy PC are shown smoothly from Fusion's interpolation buffer instead of being predicted
  and corrected: at 600 ms ping jumps over 50 cm 237 → 11, over 2 m 2 → 0. Collisions with them are approximate.
  Config: `[Netcode] RemoteInterpolate` (on).
- F7: turning ping compensation off needs a second click, and F7 warns while it is off: without it the game's own
  netcode can throw cars back by metres when packets are lost.
- Your own car is no longer pulled back to the host. After every server correction the mod puts back the full state of
  your car: body, wheels, suspension, engine and gearbox (Vehicle Physics Pro state), before the game draws a frame.
- The client's input buffer holds 8 s instead of 1 s. Throttle and steering no longer drop to zero when the host's
  updates pause for a moment (the "could not add input, buffer was full" stutter).
- The host follows each client's car in one continuous mode at any ping. The old fast/slow switch with snaps every
  1.5 s at high ping is gone.
- The client takes the host's position only when the game moved the car on purpose (respawn, rescue, jack, map change)
  or when it is more than 25 m away. The old 30° rule and the false "host moved the car" signals at 200 ms are gone.
- After a connection loss the host waits 3 s (was 1.5 s) before braking the client's car.
- If the client's game freezes (after heavy packet loss) the host no longer snaps to its frozen position over and over
  and no longer tells the client to take the host's position. It treats the frozen data as a connection loss.
- Reconnecting right after a crash or a link loss works at once: the host drops the player's old connection instead
  of leaving the new one without data until the old one times out (up to 30 s).
- "Ping compensation" (F7, on by default) replaces the separate "own car without rollback" switch. Old configs are
  migrated to on.
- Other players' cars no longer jump by 2-3 m on your screen. Your game runs their cars a little ahead of the host.
  When another driver brakes or turns, the game used to correct anything above 2 m in one frame. Now such corrections
  are faded out over 0.25 s, and only jumps above 10 m (respawns, rescues) are shown at once. This changes only what
  you see: collisions use the real position. Config: `[Netcode] RemoteProxySmoothingSeconds`, `RemoteProxySnapMeters`,
  `RemoteProxySnapDegrees`.
- When the game moves a client's car (respawn, rescue, jack, reset upright), the host marks it as a teleport, so
  everyone sees it jump at once instead of sliding there. Config: `[CarSync] TeleportOnGameMove`.
- The host follows a client's car by steering its speed toward the client's position instead of moving it there
  each frame. Before, it could push the car into another car, an object or the ground, and the physics then pushed
  it out in one step, a jump everyone saw. Config: `[CarSync] FollowByVelocity` (on), `Servo*`.

### Menu and joining
- The Local Co-op button in the main menu works with arrow keys and a gamepad. The buttons on the Local Co-op screen
  respond to arrows, gamepad and Enter.
- "Join" no longer fails with "The host is on map R1A3_Ridge and you do not have it". Maps are found by the game's own
  lookup, as in a normal session.
- New "Mods" button on the Local Co-op screen: the host chooses whether the session allows mods (on by default). The
  "Mods are disabled in this session" message no longer appears by mistake.
- Several game copies on one PC (Local Co-op with 2-4 players on one machine) no longer make OTH-LAN fail to load
  in one of them ("dev.othlan.lan.cfg is being used by another process"): the settings file is written once at
  start, with retries.

### Language
- **Mod UI follows the game's language.** Every user-visible text (F7 window, Local Co-op button and screen, ping
  label, "no link" car labels, dialogs, status lines) comes from string tables (`src/OthLan.Plugin/Lang/*.json`).
  The mod watches the game's selected locale and re-renders immediately, without a restart. English and Russian are
  complete; the other game languages fall back to English. The BepInEx log stays in English.
- The Local Co-op button and screen keep their own texts when the game's localization system re-applies the cloned
  menu button's original text after a language change.
- Installer: English or Russian by the Windows display language, `--lang en|ru` to override.
- Build: `-p:Interop=<dir>` and `-p:BepInExCore=<dir>` point the build at your own interop / BepInEx copies.

### Updates
- The mod checks GitHub once per game start for a newer OTH-LAN (latest release of this repository, 5 s timeout,
  silent when offline). If there is one, the main menu shows "OTH-LAN X.Y.Z is available" with the short release
  notes and Install / Later / Skip this version (mouse, arrow keys, gamepad).
- Install downloads the release zip, checks its sha256 against SHA256SUMS.txt of the same release and stops on any
  mismatch. The new plugin is copied in after you quit the game ("Quit and update"); settings stay as they are, the
  old DLL is kept in `OTH-LAN/backup-update-*`. No administrator rights are needed.
- F7: "Check for updates" (on by default) and "Check now". Config: `[Update] CheckForUpdates`.
- This works from 0.1.8 on: 0.1.7 has no update check, so install 0.1.8 by hand once.
- The installer asks for administrator rights only for install and uninstall (it no longer requires them to start).

### Diagnostics
- F7 shows network statistics: Fusion stalls, inputs dropped, corrections. The ping label shows only the real UDP echo.
- The BepInEx log gets a `[NetStats]` line every 30 s with these numbers.

## 0.1.8 — изменения

Обоим игрокам нужна 0.1.8: игра с 0.1.7 не пускается, в сообщении указаны обе версии.

### Сетевой код (компенсация пинга)
- Высокий пинг и подвисания: своя машина теперь идёт по часам твоего ПК, как в одиночной игре. Fusion (сетевой
  движок игры) перестаёт считать физику, когда данным хоста больше 1 с, а потом догоняет за один кадр; на пинге
  600 мс это случалось несколько раз в секунду (машина замирает, потом дёргается вперёд). Теперь машина едет дальше
  с твоим текущим управлением, пока Fusion ждёт, и его догоняющие шаги к ней второй раз не применяются. Замер на
  одном ПК, пинг 600 мс, рывков своей машины больше 50 см примерно за 3 минуты: 0.1.7 с выключенной компенсацией 165,
  с включённой 305, теперь 1-2; при 5 % потерь на 400 мс: 1 и ни одного больше 2 м. Настройки: `[Netcode] OwnLocalClock`,
  `OwnFreezeSmoothing` (обе включены).
- Машины других игроков на лагающем ПК показываются плавно из буфера интерполяции Fusion, а не предсказываются с
  поправками: на пинге 600 мс рывков больше 50 см 237 → 11, больше 2 м 2 → 0. Столкновения с ними приблизительные.
  Настройка: `[Netcode] RemoteInterpolate` (включена).
- F7: чтобы выключить компенсацию пинга, нужно нажать второй раз, и пока она выключена, F7 предупреждает: без неё
  собственный сетевой код игры при потерях пакетов может откидывать машины на метры.
- Своя машина больше не откатывается к хосту. После каждой поправки сервера мод возвращает полное состояние машины:
  кузов, колёса, подвеску, двигатель и коробку (состояние Vehicle Physics Pro), до того как игра нарисует кадр.
- Буфер ввода клиента вмещает 8 с вместо 1 с. Газ и руль больше не обнуляются, когда обновления от хоста ненадолго
  замирают (рывки «could not add input, buffer was full»).
- Хост ведёт машину каждого клиента в одном непрерывном режиме при любом пинге. Старое переключение быстрого и
  медленного режима со snap каждые 1,5 с на высоком пинге убрано.
- Клиент берёт позицию хоста, только когда игра сама переставила машину (возрождение, спасение, домкрат, смена карты)
  или когда расхождение больше 25 м. Старое правило 30° и ложные сигналы «хост переставил машину» на 200 мс убраны.
- После обрыва связи хост ждёт 3 с (было 1,5 с), прежде чем тормозить машину клиента.
- Если игра клиента зависла (после сильной потери пакетов), хост больше не прыгает раз за разом к её застывшей позиции
  и не велит клиенту взять позицию хоста. Застывшие данные считаются обрывом связи.
- Повторный вход сразу после вылета или обрыва работает сразу: хост закрывает старое подключение игрока, а не
  оставляет новое без данных, пока старое не отвалится по таймауту (до 30 с).
- «Компенсация пинга» (F7, по умолчанию включена) заменяет отдельный переключатель «своя машина без отката». Старые
  настройки переводятся во «вкл.».
- Машины других игроков больше не прыгают у вас на 2-3 м. Ваша игра просчитывает их машины чуть вперёд хоста.
  Когда другой водитель тормозит или поворачивает, раньше игра исправляла всё больше 2 м за один кадр. Теперь такие
  поправки плавно исчезают за 0,25 с, а сразу видны только скачки больше 10 м (возрождение, эвакуация). Меняется
  только картинка: столкновения считаются по настоящей позиции. Настройки: `[Netcode] RemoteProxySmoothingSeconds`,
  `RemoteProxySnapMeters`, `RemoteProxySnapDegrees`.
- Когда игра переносит машину клиента (возрождение, эвакуация, домкрат, постановка на колёса), хост помечает это как
  телепорт, и все видят перенос сразу, а не плавный проезд. Настройка: `[CarSync] TeleportOnGameMove`.
- Хост ведёт машину клиента, направляя её скорость к позиции клиента, а не переставляя машину туда каждый кадр.
  Раньше он мог вдавить машину в другую машину, предмет или землю, и физика выталкивала её за один шаг: прыжок,
  который видели все. Настройки: `[CarSync] FollowByVelocity` (вкл.), `Servo*`.

### Меню и вход
- Кнопка «Локальный кооп» в главном меню работает со стрелками и геймпадом. Кнопки экрана «Локальный кооп»
  отвечают на стрелки, геймпад и Enter.
- «Войти» больше не падает с «Хост на карте R1A3_Ridge, а у вас её нет». Карты ищутся штатным поиском игры, как в
  обычной сессии.
- Новая кнопка «Моды» на экране «Локальный кооп»: хост выбирает, разрешены ли моды в сессии (по умолчанию да).
  Сообщение «В этой сессии моды отключены» больше не появляется по ошибке.
- Несколько копий игры на одном ПК (локальный кооп на 2-4 игрока на одной машине) больше не приводят к тому, что
  OTH-LAN не загружается в одной из них («dev.othlan.lan.cfg занят другим процессом»): файл настроек пишется один
  раз при запуске, с повторами.

### Язык
- **Интерфейс мода следует языку игры.** Все видимые тексты (окно F7, кнопка и экран «Локальный кооп», метка
  пинга, метки «нет связи» над машинами, диалоги, строки статуса) берутся из таблиц строк (`src/OthLan.Plugin/Lang/*.json`).
  Мод следит за выбранным в игре языком и сразу перерисовывает тексты, без перезапуска. Английский и русский
  готовы полностью; остальные языки игры показываются по-английски. Лог BepInEx остаётся на английском.
- Кнопка и экран «Локальный кооп» сохраняют свои тексты, когда система локализации игры после смены языка
  возвращает скопированной кнопке меню её исходный текст.
- Установщик: английский или русский по языку Windows, `--lang en|ru` — выбрать вручную.
- Сборка: `-p:Interop=<dir>` и `-p:BepInExCore=<dir>` указывают сборке ваши копии interop / BepInEx.

### Обновления
- Мод раз за запуск игры проверяет на GitHub, нет ли новой OTH-LAN (последний релиз этого репозитория, таймаут 5 с,
  без интернета молча пропускается). Если есть, в главном меню появляется «Доступна OTH-LAN X.Y.Z» с кратким списком
  изменений и кнопками «Установить» / «Позже» / «Пропустить эту версию» (мышь, стрелки, геймпад).
- «Установить» скачивает zip релиза, сверяет его sha256 с SHA256SUMS.txt того же релиза и при любом расхождении
  останавливается. Новый плагин копируется после выхода из игры («Выйти и обновить»); настройки не меняются, старая
  DLL остаётся в `OTH-LAN/backup-update-*`. Права администратора не нужны.
- F7: «Проверять обновления» (по умолчанию вкл.) и «Проверить сейчас». В конфиге: `[Update] CheckForUpdates`.
- Это работает начиная с 0.1.8: в 0.1.7 проверки обновлений нет, поэтому 0.1.8 один раз нужно установить вручную.
- Установщик просит права администратора только для установки и удаления (для запуска они больше не нужны).

### Диагностика
- F7 показывает сетевую статистику: замирания Fusion, потерянный ввод, поправки. Метка пинга показывает только
  настоящий UDP-эхо.
- В лог BepInEx раз в 30 с пишется строка `[NetStats]` с этими числами.

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





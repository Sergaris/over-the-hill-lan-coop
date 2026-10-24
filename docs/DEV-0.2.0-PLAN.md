# OTH-LAN 0.2.0 — план переработки сетевого кода и UI (для исполнителей)

> **Приватный документ.** Не публикуется. Перед упаковкой любого релиза добавить `docs/DEV-0.2.0-PLAN.md` в список
> удаляемых файлов скрипта упаковки (`AGENT_HOME/oth-lan-release/scripts/package-*.ps1`, массив рядом с
> `docs/HANDOFF.md`, `docs/ROADMAP.md`).
>
> Автор плана: архитектор (2026-10-24), по коду `main` = v0.1.9.3 и по логам живой сессии клиента 0.1.9.2
> (4 игрока, Radmin, пинг ~130 мс). Исполнители — более слабые модели. Каждая задача ниже самодостаточна:
> цель, файлы, контракт, шаги, тесты, критерий готовности, запреты. **Не перепрыгивайте этапы и гейты.**

---

## 0. Правила для исполнителя (обязательно, до первой правки)

1. Прочитать `AGENTS.md`, `docs/HANDOFF.md` §0, §3, §4, §5, §10. Этот план главнее `docs/ROADMAP.md` по сетевому коду
   (R1–R5/U1–U6 там — история; всё нужное перенесено сюда).
2. Ветка: `dev/0.2.0` от `main` (тег `v0.1.9.3`). Одна задача = один коммит (или серия коммитов с одним префиксом
   `0.2.0 Tx.y:`). Без push, merge, тегов, релизов и публикаций без явного «да» владельца.
3. Перед кодом — «гейт фундамента» из user rules: какое знание добавляешь и кто им уже владеет (назвать модуль и
   функцию, либо «владельца нет, искал по словам …»), куда ляжет, через какой контракт, какие красные флаги. Вердикт
   писать в отчёт задачи.
4. Один владелец знания. Если задача говорит «перенести X в модуль Y» — копии в старом месте **удаляются в том же
   коммите**. Новая формула/порог/формат рядом со старой копией — брак.
5. Чистая логика (без Unity/Il2Cpp типов) — в отдельный `*Logic.cs`/`*Wire.cs`, подключить в
   `tests/LogicTests/LogicTests.csproj` (`<Compile Include=…>`), тесты — в `tests/LogicTests/Program.cs` в стиле
   существующих (`Check(...)`).
6. После каждой задачи (Windows, корень репо, `$env:DOTNET_ROLL_FORWARD='Major'`):
   - `cd tests/LogicTests; dotnet run -c Release` — все проверки зелёные;
   - `dotnet build src/OthLan.Plugin -c Release` и `dotnet build src/OthLan.Plugin -c Release -p:OthLanPublic=true` — 0 ошибок,
     без новых предупреждений.
7. Каждый новый ключ конфига: `Config.Bind` в `Plugin.cs` с английским описанием (что делает, дефолт, когда менять).
   Каждая новая строка UI: ключ в **обоих** `Lang/en.json` и `Lang/ru.json`.
8. Логи: префикс модуля в квадратных скобках (`[RemoteView] …`), без спама: периодические — не чаще 1 раза в 30 с
   (через `NetStats`), события — с лимитом.
9. Задачи с пометкой **LIVE** нельзя закрыть без проверки в игре владельцем (или на тестовом ПК по HANDOFF §7).
   Исполнитель готовит сборку, чек-лист и ожидаемые строки лога; закрывает задачу только по присланным логам.
10. Никогда: коммитить `interop/`, `dump/`, `third_party/`, zip-архивы с логами, IP, ники, Steam ID; менять поведение
    по умолчанию без данных; «чинить» тест под код.

---

## 1. Диагноз (что сломано и почему)

Источники: `[NetStats]`/`[OwnCar]`/`[RemoteSmooth]` из `LogOutput.log` сессии клиента 2026-10-24 19:17–19:59,
`netstats.txt` из трёх F7-архивов 17:44 / 17:48 / 18:29, Unity `Player-prev.log`, чтение кода v0.1.9.3.

| # | Симптом владельца | Доказательство | Корневая причина | Задачи |
|---|---|---|---|---|
| D1 | Колёса чужих машин не крутятся (я клиент) | `remoteVisuals wheelFrames=466122 errors=0`, но колёса стоят; у всех чужих `v=0.0m/s` | `RemoteVisuals.ApplyWheels`: если `NetworkedVehicleStatus.WheelsAngularVelocity` непустой, ставится `spun=true` и прибавляется его значение. На несимулируемом прокси список есть, но значения 0 → запасной путь (скорость кузова/Δpose) **никогда не выполняется**. Плюс `wt.localRotation = Euler(spin, steer, 0)` затирает исходный поворот колеса; подвески нет вовсе | T1.1, T3.6 |
| D2 | Другие видят, как моя машина проваливается под землю после неаккуратного падения; лечится только телепортом. Хост видит нормально | Хост не выполняет `RemoteSmooth` (только клиенты); на клиенте был кадр `remote hitch 2228cm render-rb=2212cm` (нарисовано в 22 м от тела) | `RemoteSmoother.Step` (`SmoothLogic.cs`) + `RemoteSmooth.LateTick`: «голод» (`starved`) определяется как «поза у Fusion не менялась 0,1 с» (`RemoteStarvedSeconds`), а не как «нет снапшотов хоста». Машина, резко остановившаяся после падения: (1) пока не «голодает», каждый кадр «скачок» = `V·dt` вниз уходит в offset (ветка absorb, скорость не обновляется); (2) затем 0,5 с экстраполяции **вниз** со скоростью падения; (3) состояние `Hold` держится бесконечно, пока Fusion не сдвинет машину; таймаута нет. Вертикаль экстраполируется без ограничений | T1.2 |
| D3 | Сильно дёргает, когда я клиент | архивы 17:44–18:29: `frameMs p99=83`, `snapshot gaps >1s=62 max=9.6s`, `localSteps=9911`; сессия 19:17: `p99=24.7`, gaps >1s=3, localSteps=2353. Рывки своей машины `hitch 30–79cm (sim clock 0–10cm) dt=40–50ms` | Пики времени кадра, а не сетевые коррекции: (а) на каждый снапшот клиент пересимулирует **всю** физическую сцену (`ClientPhysicsSimulation` игры, по умолчанию физика в каждом тике пересимуляции); (б) `OwnLocal.LocalStep` вызывает `Physics.Simulate` всей сцены поверх тиков Fusion при остановках; (в) `OwnLocal.Frame`/`Debt` считаются в `Overlay.Update`, порядок относительно Fusion не гарантирован. Чем хуже сеть (паузы снапшотов), тем больше локальных шагов → больше пиков | T0.4, T2.1–T2.4 |
| D4 | Чужие машины дёргаются | raw Fusion remote `>50cm=2864`, паузы снапшотов до 9,6 с | Чужая машина клиента для третьего игрока = физический прокси хоста (сервопривод к позам клиента 30 Гц) → Fusion-снапшоты хоста (большие пакеты, паузы) → интерполяция Fusion → `RemoteSmooth`. Две пересылки, сервопривод и паузы снапшотов складываются | Этап 3 |
| D5 | При таранах машины очень лагают | `suspend OwnLocal/phase2 (Contact)` десятки раз за сессию, сразу `resume` | На контакте клиент отдаёт машину хосту (коррекции Fusion при 130 мс видны как откаты), через 1 с забирает обратно. Чужие машины на клиенте — kinematic-тела, которые двигаются телепортом трансформа (в столкновении у них нулевая скорость) и находятся «в прошлом» на задержку интерполяции | T3.7, T4.0–T4.4 |
| D6 | Рывки при работе лебёдкой | 46 suspend/resume (`Winch`) за сессию; 12 × `NullReferenceException at VehicleWinchSystem.FixedUpdateNetwork` внутри `RunClientSideResimulationLoop` | `CarSync.WinchInvolved` считает «вовлечённой» **любую** натянутую лебёдку, даже за дерево → клиент на всё время лебёдки отдаёт машину хосту (коррекции). Исключение лебёдки — в пересимуляции клиента (своё или ванильное — не проверено) | T1.4, T4.2 |
| D7 | Диагностика бесполезна | 3 архива F7 содержат только `netstats.txt` + cfg | `Diagnostics.ExportZip`: `CreateEntryFromFile` не может открыть файлы, которые игра держит на запись; ошибка проглатывается `catch { }`. `LogOutput.log` перезаписывается при каждом запуске — логи 17:44–18:29 потеряны. Строка `[Diag] runner: … ClientPhysicsSimulation=…` **ни разу** не записалась: исключение Fusion приходит не как `InvalidOperationException`, ловится общим `catch`, повтора нет | T0.2, T0.3 |
| D8 | Геймпад не может выбрать кнопки в окне недостающих модов | — (код) | `ModLiveryGate`: нет `EventSystem.SetSelectedGameObject`, кнопки — клоны `MultiplayerButton` с навигацией главного меню, главное меню под диалогом активно; A всегда = «Скачать», переключения между тремя кнопками нет | T1.3 |

Вывод: точечные баги (D1, D2, D7, D8) чинятся быстро и независимо (этап 1). D3–D6 — следствие архитектуры: клиент
борется с предсказанием Fusion, а чужие машины видны через цепочку «поза → сервопривод хоста → снапшот Fusion →
интерполяция → сглаживание». Её и меняем (этапы 2–4).

---

## 2. Целевая архитектура 0.2.0

### 2.1 Принципы

1. **Машину считает её владелец.** Своя машина клиента — на клиенте (как сейчас, phase 2), машина хоста — на хосте.
   Исключение — **явный** общий режим `HostShared` (буксировка между игроками), см. этап 4.
2. **Состояние машины идёт своим потоком, а не через Fusion-снапшоты.** Владелец шлёт `CarState` (поза, скорости,
   визуал колёс, фары, эпоха телепорта) 30 Гц с избыточностью; хост **пересылает** поток каждого клиента остальным и
   шлёт свою машину всем. Fusion-репликация машин остаётся только запасным путём (поток устарел > 0,5 с).
3. **Наблюдатель рисует чужую машину из буфера воспроизведения** на временной шкале отправителя (адаптивная задержка,
   Эрмит, ограниченная экстраполяция без «ныряния» вниз, явный снап по эпохе телепорта). Один модуль владеет «где
   нарисовать чужую машину».
4. **Клиент не пересимулирует физику сцены.** `ClientPhysicsSimulation = SimulateForward` (документация Photon:
   в тиках пересимуляции только `SyncTransforms`, `Physics.Simulate` — только в прямых тиках). Свою машину
   пересимуляция всё равно не меняет (phase 2 откатывает результат) — платить за неё незачем.
5. **Один порядок кадра.** Всё, что зависит от того, «отработал ли уже Fusion в этом кадре», вызывается из одной точки
   после Fusion (detour `NetworkRigidbody.Render`, он уже есть в `OwnRender`).
6. **Метрики раньше изменений.** Каждый этап закрывается цифрами `[NetStats]`, а не ощущением.

### 2.2 Поток данных (после этапа 3)

```
Владелец (клиент A или хост)
  OwnCar/OwnClock: своя физика ─► CarStateSampler (поза, v, ω, колёса, фары, эпоха) ─► CarStreamSender 30 Гц, ×2–3 состояния
        │ UDP Port+1 (клиент → хост)                    │ хост: своя машина сразу в CarRelay
        ▼                                               ▼
Хост: CarRelay ── пересылает байты потока A всем, кроме A; шлёт поток машины хоста всем
      HostFollower ── физический прокси A на хосте (как сейчас CarSync) — для столкновений/игры хоста и как запас Fusion
        │
        ▼
Наблюдатель (любой клиент, и хост для отрисовки колёс клиентов — опционально)
  ClientLink.Rx ─► StatePlayback[slot] (буфер, шкала отправителя, задержка) ─► RemoteCarView (поза корня + kinematic тело)
                                                                          └► RemoteWheelVisuals (колёса, руль, подвеска, фары)
  поток устарел > 0,5 с ─► запасной путь: поза Fusion + исправленный RemoteSmoother
```

### 2.3 Карта модулей (итог 0.2.0)

| Модуль (файл) | Ответственность одной фразой | Публичный контракт | Откуда берётся |
|---|---|---|---|
| `CarRegistry.cs` | Список машин сессии и чья какая | `IReadOnlyList<CarEntry> Cars`, `CarEntry Own`, `bool TryGetBySlot(int, out CarEntry)`; `CarEntry{FusionPlayer Fp; int Slot; bool IsOwn; Rigidbody Rb; NetworkRigidbody3D Nrb}` | новый; убирает 5–7 копий `FindObjectsOfType<FusionPlayer>()` раз в секунду (`RemoteSmooth`, `RemoteVisuals`, `NetStats`, `CarSync`, `LinkLabels`, `OwnCar`, `Diagnostics`) |
| `NetClock.cs` | Единые монотонные часы сетевого кода | `static double Now` | новый; заменяет `ClientLink.Now`, `HostPings.Now`, `OwnCar.Now` в новом коде (старые — прокси на него) |
| `CarWire.cs` (Logic) | Формат пакета `OTC3` | `Encode(in CarStatePacket, byte[]) → int`, `TryDecode(ReadOnlySpan<byte>, out CarStatePacket)` | новый; `OTP2` из `SyncLogic.cs` удаляется в T3.8 |
| `StatePlayback.cs` (Logic) | Воспроизведение потока на шкале отправителя | `Push(in CarStateSample, double arrivalLocal)`, `PlaybackResult SampleAt(double nowLocal)` | эволюция `PosePlayback.cs` (переименовать, не копировать) |
| `CarStreamSender.cs` | Отправка своей машины | `Tick()` из `NetFrame`; `BumpEpoch(string why)` | из `CarSync.ClientSend` |
| `CarRelay.cs` | Пересылка потоков на хосте | `OnPacket(byte[] d, int len, int fromSlot)` (сетевой поток) | новый, на `HostPings.Broadcast`-подобной рассылке |
| `HostFollower.cs` | Физический прокси клиента на хосте | `Tick()`; вход — `StatePlayback` слота | из `CarSync.HostApplyCars` (223 строки → методы < 50 строк) |
| `RemoteCarView.cs` | Где нарисовать чужую машину и где её kinematic-тело | `AfterFusionRender()`, `PrePhysicsStep(double stepEndLocal)`; `ViewState StateOf(slot)` | вместо `RemoteSmooth.LateTick` (root-gap путь удалить) |
| `RemoteWheelVisuals.cs` | Колёса/руль/подвеска/фары/ливрея чужой машины | `Apply(CarEntry, in VisualSample)` | эволюция `RemoteVisuals.cs` (переименовать) |
| `InteractionLogic.cs` (Logic) | Режим авторитета машины при контакте/лебёдке | `Mode Step(in InteractionInput, double now)`; `Mode {Owner, HostShared}` | эволюция `SuspendLogic.cs` |
| `OwnClock.cs` | Шаги и альфа отрисовки своей машины в кадре | `FrameAfterFusion(double now)` | из `OwnLocal.Frame` + расчёт альфы `OwnRender.Apply` |
| `NetFrame.cs` | Порядок вызовов сетевого кода в кадре | см. 2.4 | из `Overlay.Update/LateUpdate` |
| `UiNav.cs` | Фокус и навигация геймпада для наших Canvas-окон | `Select(GameObject)`, `LinkRow(Selectable[])`, `LinkColumn(Selectable[])`, `KeepFocus(Selectable[] allowed, Selectable fallback)` | из `CoopMenu.Select/Nav` (копии удалить) |
| `UiModal.cs` | Не больше одного модального окна мода | `bool TryOpen(string owner)`, `Close(string owner)`, `string Current` | новый |
| `JoinFlow.cs` | Подключение: запрос хоста, гейт модов, старт, сторож | то, что сейчас `Overlay.Join/PollQuery/Watchdog/Recover` | вынос из `Overlay.cs` (621 строка) |
| `SessionLogs.cs` | Сохранение и экспорт логов | `SnapshotNow(string why)`, `ExportZip(string why) → path` | из `Diagnostics.ExportZip` |

Не трогаем без нужды: транспорт Fusion (`FusionHooks`), слоты, обнаружение лобби, обновлятор (кроме UI в T6.7).

### 2.4 Порядок кадра (`NetFrame`)

| Фаза | Где вызывается | Что |
|---|---|---|
| Update | `Overlay.Update` → `NetFrame.Update()` | сессия, UI, `CarRegistry.Tick` (скан раз в 1 с), `CarStreamSender.Tick`, `HostFollower.Tick` (хост), `NetStats.Tick` |
| AfterFusion | первый вызов detour `NetworkRigidbody.Render` в кадре (`OwnRender.RenderDetour` уже ловит его) → `NetFrame.AfterFusion()` | `OwnClock.FrameAfterFusion`, `RemoteCarView.AfterFusionRender` |
| LateUpdate | `Overlay.LateUpdate` → `NetFrame.Late()` | `RemoteWheelVisuals` (после того, как корень поставлен), запасной `RemoteCarView`, если хук Render недоступен |
| PrePhysics | перед каждым шагом физики: `OwnCar.BeforeTickDetour` (прямой тик) и `OwnLocal.LocalStep` перед `Physics.Simulate` | `RemoteCarView.PrePhysicsStep` |
| Repaint | `Overlay.OnGUI` | `NetStats` сэмпл (как сейчас) |

Если detour `Render` не установлен (другая сборка игры), `NetFrame` вызывает AfterFusion из LateUpdate и пишет
одно предупреждение.

---

## 3. Этапы и гейты

| Этап | Суть | Гейт перехода |
|---|---|---|
| 0 | Инструменты: логи не теряются, видно, где тратится кадр | T0.1–T0.5 готовы; владелец прислал хотя бы одну сессию клиента с новыми логами |
| 1 | Точечные исправления D1, D2, D7, D8, лебёдка за дерево (данные) | LogicTests зелёные; **LIVE**: колёса крутятся, под землю не проваливается, геймпад в окне модов работает |
| 2 | Клиент: без пересимуляции физики, один порядок кадра | **LIVE**: `frameMs p99` клиента ≤ 30 мс в 4-игроковой сессии; своя машина не хуже |
| 3 | Поток состояния + ретрансляция + новый вид чужих машин | **LIVE**: метрики §5.1 на наблюдателе |
| 4 | Взаимодействия: контакт, лебёдка, буксировка; инвариант синхронности | **LIVE**: сценарии §5.2, включая синхронность T4.4 |
| 5 | Переезд ответственностей (без изменения поведения) | метрики не хуже этапа 4; размеры файлов/функций §5.4 |
| 6 | UI и QoL | **LIVE** с геймпадом |
| 7 | Релиз 0.2.0 | только после «да» владельца |

Этап 1 отдельно **не выпускается** (D-2 в §7): всё копится в `dev/0.2.0` до релиза 0.2.0.

---

### Этап 0. Инструменты

**T0.1 Ветка.** `git switch -c dev/0.2.0 v0.1.9.3`. Версию не менять до этапа 7 (join сверяет версию мода; dev-сборки
тестируются у всех игроков одновременно).

**T0.2 `SessionLogs`: экспорт и хранение логов** (D7)
- Файлы: новый `SessionLogs.cs`; `Diagnostics.cs` (удалить `ExportZip`, оставить вызов-обёртку на одну задачу, затем
  перевести вызывающих: F7-кнопка в `Overlay.cs`, `Autopilot.cs`).
- Копирование открытого файла: `new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)`
  → `CopyTo` в `zip.CreateEntry(name).Open()`. Ошибку **логировать** (`[Logs] skip <name>: <msg>`), не глотать.
- Что класть: `BepInEx/LogOutput.log`, `BepInEx/ErrorLog.log`, `Player.log` и `Player-prev.log` из
  `Application.persistentDataPath`, cfg, `netstats.txt` (как сейчас), `session.txt` (версия мода, сборка игры, роль,
  слот, число игроков, время начала/конца сессии, текущие значения ключей `[Netcode]`/`[CarSync]`/`[Outage]`/`[OwnCar]`).
- Хранение: `SnapshotNow(why)` копирует `LogOutput.log` в `BepInEx/OthLan-logs/LogOutput-<yyyyMMdd-HHmmss>-<why>.log`
  (тем же способом с `FileShare.ReadWrite`), держит последние 10 файлов. Вызывать: конец сессии (там, где `NetStats`
  пишет `session end`), `Application.quitting`, экспорт. Причина: BepInEx перезаписывает `LogOutput.log` при запуске
  раньше, чем грузится мод.
- Тесты: Unity-зависимо, юнит-тестов нет; логика ротации (выбор удаляемых по имени/времени) — в `SessionLogsLogic`
  в LogicTests (11 файлов → удалить 1 самый старый; посторонние файлы не трогать).
- Готово: экспорт во время игры содержит все 4 лога ненулевого размера (проверка: собрать DLL, владелец жмёт F7 →
  Export, присылает архив) — **LIVE**.

**T0.3 `[Diag]` пишет режим физики** (D7)
- `Diagnostics.Tick`: любое исключение, текст которого содержит `RuntimeConfig`, → сбросить `_loggedRunner`, повторить
  через 3 с, до 20 раз. Остальные — как сейчас.
- Добавить в строку `RunnerSimulatePhysics3D:` значения `Physics.simulationMode`, `Time.fixedDeltaTime`, `r.TickRate`.
- Готово: в логе клиента есть строка `[Diag] RunnerSimulatePhysics3D: ClientPhysicsSimulation=…` — **LIVE**.

**T0.4 Стоимость кадра по стадиям** (D3)
- Файлы: `NetStats.cs` (+ новый маленький `FrameCost` в нём же или `FrameCostLogic.cs`).
- `Stopwatch`-накопители на кадр: `resim` (обернуть `_rsOrig` в `OwnCar.ResimDetour`), `localStep`
  (`OwnLocal.LocalStep`), `remoteView` (`RemoteSmooth.LateTick`, потом `RemoteCarView`), `wheels`
  (`RemoteVisuals.LateTick`), `hostFollow` (`CarSync.HostApplyCars`). Раз в 30 с: p50/p99/max на стадию и число
  кадров, где стадия > 8 мс.
- Строка лога: `[NetStats] cost resim p50=… p99=… max=… >8ms=… | localStep … | …`; то же в `netstats.txt`.
- Готово: строка есть; сумма стадий в кадре 50+ мс видна по `>8ms`.

**T0.5 Скорость чужих машин в метрике**
- `NetStats` строка `remote hitch … v=` берёт `rb.linearVelocity` (у kinematic = 0). Брать скорость из Δpose
  отрисованного корня между сэмплами. Чисто диагностическая правка.

---

### Этап 1. Точечные исправления

**T1.1 Колёса чужих машин крутятся** (D1)
- Файл: `RemoteVisuals.cs` (владелец визуала чужих машин).
- Правило выбора скорости колеса (вынести в Logic-функцию `WheelSpinLogic.RateRadS(...)` для тестов):
  1. `bodyV` = `ForwardSpeedMs` (как сейчас: `VehicleSpeed` → `vehicleBase.speed` → Δpose → rb).
  2. `netAng[i]` из `WheelsAngularVelocity` использовать **только если** за последнюю 1 с хоть одно значение по модулю
     > 0,5 рад/с (флаг на машину, обновляется каждый кадр). Иначе — `bodyV / radius`.
  3. Если `|bodyV| > 2 м/с`, а `|netAng[i]·radius|` < 20 % от `|bodyV|` — тоже `bodyV / radius`.
- Поворот: при первом `Ensure` запомнить `rest[i] = wt.localRotation`; писать
  `wt.localRotation = Quaternion.Euler(0, steerDeg, 0) * rest[i] * Quaternion.Euler(spinDeg, 0, 0)` **только после**
  проверки конвенции (см. ниже). При `RemoteVisuals.Reset`/`RestoreWtu` вернуть `rest[i]`.
- Проверка конвенции (**LIVE**, 10 минут, тестовый ПК или владелец): временный флаг `[Debug] WheelProbe=true` пишет для
  своей машины раз в 0,5 с `wt.localRotation.eulerAngles` при езде прямо (руль 0) и при повороте на месте. Ожидание:
  при езде меняется только X, при повороте — только Y. Если иначе — записать фактические оси в §8.2 этого документа и
  поменять формулу; флаг удалить в том же коммите.
- Тесты (LogicTests): нулевой `netAng` + `bodyV=10`, `r=0.4` → 25 рад/с; живой `netAng=24` → 24; `netAng=1`,
  `bodyV=10` → 25 (правило 3); стоим (`bodyV=0`, `netAng=0`) → 0.
- Готово (**LIVE**): клиент смотрит на хоста и другого клиента — колёса крутятся, вперёд/назад правильно, руль
  поворачивает передние; `[RemoteVisuals]` без ошибок.

**T1.2 Чужая машина не уходит под землю** (D2)
- Файлы: `SmoothLogic.cs` (`RemoteSmoother`, владелец визуального сглаживания), `RemoteSmooth.cs`, `NetStats.cs`.
- Изменения в `RemoteSmoother` (каждое — отдельный тест):
  1. «Голод» = **нет новых снапшотов хоста** дольше `RemoteStarvedSeconds`. В `NetStats` уже есть время последнего
     нового `LatestServerTick` (`_serverTickAt`); вывести `public static double ServerTickAge`. В `RemoteSmooth`:
     `starved = client && NetStats.ServerTickAge > starveS`. Удалить пер-машинный `LastFusionMoveAt` как сигнал голода.
  2. Остановка — не разрыв: если поза Fusion не сдвинулась (`moved < FrozenEps`) и не голод → `V = 0`, ожидаемая
     позиция = `R` (без `V·dt`), ничего не поглощать.
  3. Экстраполяция при голоде — **только по горизонтали** (`Rx`, `Rz`); `Ry` не меняется. (Та же конвенция, что у
     хоста в outage: «в воздухе Y решает физика».)
  4. Пол: нарисованная `y` никогда не ниже `fy − MaxBelowMeters` (новый `Cfg.MaxBelowMeters = 0.25`): если ниже —
     `Oy` обрезается.
  5. Таймаут `Hold`: в `Hold` дольше `HoldMaxSeconds` (1,0) → снап к позе Fusion (`O=0`, `R=f`, `Snaps++`).
  6. Потолок смещения: `OffsetLength > 3 м` дольше 0,5 с → снап.
- Поворот в `RemoteSmooth.LateTick` (`Hold`/`Extrapolate` ветки): тот же таймаут `HoldMaxSeconds` → `Qo = identity`.
- Метрика: `NetStats` считает кадры, где нарисованная `y` чужой машины ниже позы Fusion более чем на 0,3 м
  (`remoteBelow=N`), в строку 30 с и `netstats.txt`.
- Тесты (LogicTests, `RemoteSmoother`):
  - падение: 30 кадров `y` уменьшается на 0,15/кадр, потом `y` постоянна 200 кадров, `starved=false` → все
    нарисованные `y ≥ y_земли − 0,25`, к 20-му кадру после остановки смещение < 1 см;
  - то же с `starved=true` на 60 кадров → `y` не ниже `y_земли − 0,25`; горизонталь экстраполируется ≤ 0,5 с;
  - стоящая машина 10 с без голода → состояние `Follow`, смещение 0;
  - `Hold` 1,2 с → снап, смещение 0;
  - старые тесты `RemoteSmoother` — обновить ожидания только там, где менялось правило 1–3, и в отчёте перечислить.
- Готово (**LIVE**): падение с обрыва/переворот на глазах у двух наблюдателей-клиентов — машина не уходит под
  текстуры, `remoteBelow=0`.

**T1.3 Окно недостающих модов управляется геймпадом** (D8; также фундамент UI для этапа 6)
- Файлы: новый `UiNav.cs`, новый `UiModal.cs`, `CoopMenu.cs` (удалить свои `Select`/часть `Nav`, вызывать `UiNav`),
  `ModLiveryGate.cs`, `Updater.cs` (только проверка `UiModal`).
- `UiNav` (статический, Canvas/EventSystem — эталон поведения `CoopMenu`):
  - `Select(GameObject go)` → `EventSystem.current?.SetSelectedGameObject(go)`;
  - `LinkRow(Selectable[] row)` — `Navigation.Mode.Explicit`, left/right по кругу, up/down = null;
  - `LinkColumn(...)` — то же вертикально; `CoopMenu.Nav` переписать через них (поведение не менять);
  - `KeepFocus(Selectable[] allowed, Selectable fallback)` — если выбранный объект `null` или не из `allowed` →
    `Select(fallback)`; вызывать каждый кадр, пока окно открыто.
- `UiModal`: `TryOpen("modgate")` / `Close("modgate")`; `Updater` не показывает свой промпт, пока
  `UiModal.Current != null` (отложить до закрытия), и наоборот (гейт модов ждёт закрытия промпта обновления — он
  выключает `EventSystem`).
- `ModLiveryGate`:
  - при открытии: запомнить `EventSystem.current.currentSelectedGameObject`; `LinkRow([Download, JoinWithout, Cancel])`;
    `Select(Download)` (если скачивание невозможно, например Modio не инициализирован, — `JoinWithout`);
  - на время окна выключить ввод главного меню так же, как `CoopMenu`: `ContextUIMainMenu.buttonsContainer.SetActive(false)`
    (найти объект тем же способом, что `CoopMenu`), вернуть при закрытии; если окно открыто не из главного меню
    (`buttonsContainer` не найден) — не падать;
  - фон-затемнение (`dim`) — `raycastTarget = true` (мышь не кликает сквозь);
  - в `Tick`: убрать собственную обработку A/Enter/Space (EventSystem сам вызывает `Submit` на выбранной кнопке —
    иначе двойное срабатывание); оставить B/Escape → `Cancel()`; каждый кадр `KeepFocus`;
  - при закрытии: вернуть меню, `Select(запомненный объект)`, если он активен, иначе кнопку «Локальный кооп»
    (`CoopMenu` хранит её в `_button` — дать публичный геттер).
- Тесты: Unity-зависимо. В `Autopilot` уже есть `ModGateTick` с `joinwithout/skip` — добавить шаг «нажать вправо,
  потом A» через `ExecuteEvents` (как `Autopilot` делает в других местах) и проверить, что сработал «Войти без модов».
- Готово (**LIVE**, геймпад): при входе к хосту с модами фокус сразу на «Скачать»; вправо/влево ходят по трём кнопкам
  с подсветкой; A нажимает выбранную; B = «Отмена»; пункты главного меню под окном не реагируют; клавиатура и мышь
  работают как раньше.

**T1.4 Лебёдка: данные для решения** (D6)
- Ничего не менять в поведении. Добавить в `[OwnCar] suspend … (Winch)` причину: `world` (трос за дерево/землю/объект)
  или `car:<slot>` (трос к машине игрока) — по `VehicleWinchSystem` (`WinchedCarPlayerRef`, валиден ли он) в
  `CarSync.WinchInvolved` (вернуть enum вместо bool; вызывающих обновить).
- Найти через reflect-утилиту (`AGENT_HOME/oth-lan-release/reflect-mod`, запуск `R.exe VehicleWinchSystem`) поля
  якоря лебёдки; записать в §8.3 этого документа.
- A/B **LIVE**: клиент с `[CarSync] Enabled=false` на хосте (ваниль) пользуется лебёдкой 2 минуты — есть ли
  `NullReferenceException … VehicleWinchSystem.FixedUpdateNetwork` в `Player.log`. Результат записать в §8.3.

---

### Этап 2. Клиент без пересимуляции физики и с одним порядком кадра

**T2.1 Режим физики клиента `SimulateForward`** (D3, главный)
- Файлы: `FusionHooks.cs` или `OwnCar.cs` (там, где клиентский раннер стартует; найти по `IsClient` +
  `RunnerSimulatePhysics3D`), `Plugin.cs`.
- Новый ключ `[Netcode] ClientPhysicsMode = SimulateForward` (варианты `Game` — не трогать, `SimulateForward`).
  Только чистый клиент: `OverlandNetworkRunner.Instance.RunnerSimulatePhysics3D.ClientPhysicsSimulation =
  ClientPhysicsSimulation.SimulateForward` сразу после подключения (и повторно, если игра пересоздаёт раннер).
  Лог: `[Netcode] client physics: <было> -> <стало>`.
- Риски проверить **LIVE** и записать: своя машина едет как раньше; фазу 2 не ломает (в `OwnCar` счётчики
  `restores` и откаты не растут); физические объекты мира рядом (бревна, грузы, если они сетевые) не дёргаются
  сильнее; лебёдка работает; исключения в `Player.log` не появились.
- Готово (**LIVE**): в одинаковом сценарии (4 игрока, 10 минут, клиент) `cost resim p99` падает минимум вдвое,
  `frameMs p99` ≤ 30 мс; своя машина на экране `p99` и `>50cm` не хуже прежнего.

**T2.2 `CarRegistry`**
- Новый `CarRegistry.cs`: скан `FindObjectsOfType<FusionPlayer>()` раз в 1 с (и сразу при смене раннера), записи
  `CarEntry` (см. 2.3), слот = индекс `InputAuthority` (использовать ту же функцию сопоставления, что `CarSync`;
  найти по `PlayerRef.FromIndex` / `AsIndex`).
- Перевести на него **всех** потребителей (список в 2.3) и удалить их собственные сканы в том же коммите.
- Готово: `rg "FindObjectsOfType<FusionPlayer>"` находит одно место; поведение не изменилось (LogicTests, сборки,
  короткий **LIVE**-заход).

**T2.3 `NetFrame` и `OwnClock`: один порядок кадра** (D3)
- Новый `NetFrame.cs` (статический) по таблице 2.4; `Overlay.Update/LateUpdate` вызывают только его и UI.
- `OwnRender.RenderDetour`: при **первом** вызове в кадре (`Time.frameCount` изменился) — `NetFrame.AfterFusion()`.
- `OwnClock.FrameAfterFusion` = нынешний `OwnLocal.Frame` (Debt, локальные шаги ≤ 2, «не шагать в кадре, где был тик
  Fusion») + расчёт альфы для `OwnRender.Apply`. `OwnLocal.Frame` из `Overlay`/`OwnCar.Tick` убрать.
- Если хук `Render` недоступен — вызывать из `LateUpdate` (лог один раз).
- Готово (**LIVE**): `hitch` своей машины с `sim clock < 5cm` (рывки только отрисовки) — минимум в 3 раза реже, чем
  в сессии 2026-10-24 19:17 (там 77 строк `hitch` за 40 минут).

**T2.4 Решение по `OwnLocalClock`**
- После T2.1–T2.3 сравнить 2 сессии **LIVE** (`OwnLocalClock` on/off через F7), по 10 минут, одинаковый маршрут.
- Если с `off` своя машина на экране не хуже (p99, `>50cm`, `>2m`), а `cost localStep` у `on` > 0 — выключить по
  умолчанию (миграция cfg, см. T7.2) и записать решение в §8.4. Иначе оставить. Код не удалять в 0.2.0.

---

### Этап 3. Поток состояния, ретрансляция, новый вид чужих машин

**T3.0 Спайк: откуда брать визуал колёс у владельца** (только чтение + **LIVE**-проба)
- reflect: `R.exe VehicleBase,WheelState,VPWheelCollider,WheelTransformUpdater` (+ `VehiclePhysics` сборка из
  `interop/`, если типы там). Найти: угол поворота колеса (rad), угловую скорость (rad/s), угол руля (deg), ход/сжатие
  подвески (м) для колеса `i`; и transform, который `WheelTransformUpdater.GetTargetWheel()` копирует на видимое колесо.
- Проба (**LIVE**, флаг `[Debug] WheelProbe`): на своей машине раз в 0,5 с печатать найденные значения и
  `GetTargetWheel().localRotation/localPosition`. Убедиться: угол руля совпадает с поворотом передних колёс, угловая
  скорость ≈ v/r.
- Итог записать в §8.2: имена полей, единицы, какой transform писать на наблюдателе (`GetTargetWheel()` при включённом
  WTU — предпочтительно; иначе видимое колесо при выключенном WTU, как сейчас).

**T3.1 Формат `OTC3`** (`CarWire.cs`, Logic, владелец формата)
- Заголовок (little-endian): `magic u32 = 'OTC3'`, `ver u8 = 1`, `slot u8`, `epoch u8`, `count u8` (1..3 состояний,
  новейшее первым).
- Состояние: `seq u32`; `sendTime f64` (секунды `NetClock.Now` отправителя в момент снятия); `pos 3×f32`;
  `rot 4×f32` (нормализованный кватернион); `vel 3×f32`; `angVel 3×f32`; `flags u8` (bit0 airborne, bit1 headlights,
  bit2 brake, bit3 reverse, bit4 handbrake, bit5 engineOn); `steerDeg i16 ×0.01`; `wheelCount u8` (≤ 6); на колесо:
  `rollDeg u16 (0..65535 → 0..360)`, `spinRadS i16 ×0.05`, `suspM i16 ×0.001`.
- Размер ≈ 102 Б на состояние при 4 колёсах; 3 состояния + заголовок ≈ 314 Б < 1200 (проверка в `Encode`).
- `TryDecode`: проверка magic/ver/длины/`count`/`wheelCount`/конечности чисел; мусор → `false`.
- Тесты: кругооборот с граничными значениями (руль ±45°, спин ±1600 рад/с обрезается, 6 колёс), обрезанный пакет,
  неверный magic, NaN → отказ.

**T3.2 `CarStreamSender` (владелец отправки своей машины)**
- Снимает состояние своей машины (`CarRegistry.Own`): поза и скорости — `rb`; визуал — по §8.2; фары — по
  `LightsController`; airborne — нет контакта колёс (по найденному в T3.0 полю или `NetworkedVehicleStatus.AllWheelsAirborne`).
- Частота `[CarSync] SendHz` (30); в пакет — 3 последних состояния (`[CarSync] Redundancy = 3`).
- Эпоха телепорта: `epoch++` когда игра двигает машину сама — это уже распознаётся: `OwnCar` «host moved the car» /
  «spawn move» / `AcceptMeters`, и у хоста `HostFollowLogic.GameMovedCar`. Плюс правило «скачок позы > 5 м за один
  шаг физики». Вызов `CarStreamSender.BumpEpoch(why)` из этих мест; лог `[Stream] epoch N (why)`.
- Клиент шлёт хосту (`ClientLink.Send`); хост — свою машину сразу в `CarRelay` (всем клиентам).
- `OTP2`-отправка и `OTC3` идут параллельно до T3.8 (хост ещё ведёт прокси по `OTP2` до T3.4).

**T3.3 `CarRelay` (хост)**
- Приём в существующем сетевом потоке хоста (где сейчас разбирается `OTP2`, `CarSync.OnPosePacket`): пакет `OTC3` от
  endpoint'а, совпадающего с `HostPings.EndpointOf(slot)` → (1) переслать **те же байты** всем остальным клиентам
  (`HostPings`-список, последние 10 с), (2) отдать в `StatePlayback` слота для `HostFollower`.
- Пакеты с чужим slot/endpoint — отбрасывать (как сейчас для поз).
- Счётчики: принято/переслано/отброшено на слот → `NetStats` строка `relay …`.
- Тест логики фильтра (Logic): endpoint совпал/не совпал, slot хоста, свой slot.

**T3.4 `StatePlayback` (Logic, владелец «воспроизведения потока»)** — переименовать и переписать `PosePlayback.cs`
- Вход: `Push(sample, arrivalLocal)`; сэмпл со своим `sendTime`. Дедупликация по `seq`, вставка вне порядка.
- Шкала: `off = min(arrival − sendTime)` по окну 2 с (минимум устойчив к всплескам задержки).
  `jit = p95(arrival − sendTime − off)` по последним 64 сэмплам. Задержка `D = clamp(jit + 0.5/SendHz, MinDelay 0.05,
  MaxDelay 0.25)`, сглаженная EWMA 0,1. Плейхед `play = nowLocal − off − D`.
- Между двумя сэмплами: Эрмит по позиции (касательные = v × span), slerp по кватерниону, линейно — скорости, руль,
  подвеска; угол колеса — интегрированием `spinRadS` от ближайшего сэмпла и мягкой подтяжкой фазы к пришедшему
  `rollDeg` (не более 90°/с).
- Плейхед впереди новейшего сэмпла на `Δ`: `Δ ≤ 0.25 с` → экстраполяция: x,z по скорости; y — только если `airborne`,
  и не ниже `y_newest − 0.5`; поворот — только рыскание по `angVel.y`. `Δ > 0.25` → держать новейший, `Stale=true`.
- Смена `epoch` в новейшем сэмпле → выбросить сэмплы старой эпохи, результат с `Snap=true` один раз.
- Результат: `PlaybackResult{Ok, Snap, Stale, Extrapolated, DelayS, pose, vel, visuals}`.
- Тесты: равномерный поток 30 Гц с джиттером ±20 мс → ошибка позиции < 2 см при 20 м/с; потеря 30 % → без
  скачков > 10 см; поток обрывается → экстраполяция ≤ 0,25 с, затем hold, `Stale`; падение затем остановка → `y` никогда
  не ниже минимума сэмплов − 0,05; смена эпохи → `Snap` ровно один раз; перестановка пакетов; дубли.
- `HostFollower` (CarSync) на этом этапе берёт цель из `StatePlayback` (новейшее «настоящее» состояние,
  `SampleAt(now)` с `D=0` — для этого параметр `delayOverride`); его tick-age экстраполяцию удалить в T3.8, когда она
  станет не нужна (сейчас `PosePlayback` держит новейший сэмпл, а `CarSync` экстраполирует по тикам — не дублировать).

**T3.5 `RemoteCarView` (владелец «где нарисовать чужую машину»)**
- Для каждой чужой машины (`CarRegistry`, не своя): если у слота есть свежий поток (`!Stale`, возраст < 0,5 с) —
  источник `Stream`, иначе `Fusion` (поза, которую поставил `NetworkRigidbody.Render`).
- `AfterFusionRender()`: поза корня = результат `StatePlayback.SampleAt(NetClock.Now)`; при переключении источника —
  смещение затухает за 0,3 с (функция затухания — `RemoteSmoother.Decay`/`TauFor`, сделать их публичными
  статическими; второй копии не писать). При `Snap` — сразу.
- Запасной путь `Fusion` — исправленный в T1.2 `RemoteSmoother`. `RemoteSmooth.LateTick` root-gap ветку удалить;
  `TuneProxy` (несимулируемый + kinematic) оставить, перенести в `RemoteCarView` как `EnsureProxyMode`.
- `PrePhysicsStep(stepEndLocal)` (T3.7) — двигает kinematic-тело.
- Метрики `NetStats`: на машину — источник, `delay`, `stale`-кадры, `extrapolated`, `snaps`, `remoteBelow`.
- Готово (**LIVE**): §5.1.

**T3.6 `RemoteWheelVisuals` из потока**
- Переименовать `RemoteVisuals.cs` → `RemoteWheelVisuals.cs` (одна ответственность — визуал чужой машины).
- Источник — `PlaybackResult.visuals` при `Stream`; при `Fusion` — правило T1.1.
- Колёса — в transform по §8.2; фары/тормоз/задний ход — из `flags` потока (при `Fusion` — как сейчас из
  `CurrentFrameInput`); ливрея — как сейчас.
- Готово (**LIVE**): колёса, руль, подвеска, фары чужих машин совпадают с тем, что видит владелец (видео/скриншоты
  парами с двух ПК).

**T3.7 Kinematic-тела чужих машин двигаются со скоростью** (D5)
- Где: перед `Physics.Simulate` на клиенте — (а) в прямом тике Fusion: в начале `OwnCar.BeforeTickDetour` (вызывается
  перед тиком своей машины), (б) в `OwnLocal.LocalStep` перед `Physics.Simulate`.
- Что: для каждой чужой машины с источником `Stream`: `rb.MovePosition(p)` и `rb.MoveRotation(q)`, где `p,q` —
  `SampleAt(момент конца шага)`. Тогда в контакте у тела есть скорость, а не телепорт.
- Риск: `NetworkRigidbody.Render` на прокси пишет transform каждый кадр; проверить, что после него `RemoteCarView`
  перезаписывает, и физика берёт позу из `MovePosition` (лог одного кадра: `rb.position` до/после шага).
- Готово (**LIVE**): таран чужой машины — своя машина не «прилипает» и не отлетает рывком; `cost` не вырос > 1 мс.

**T3.8 Удаление старого пути**
- После приёмки §5.1: удалить `OTP2` (`CarSyncLogic.Encode/TryDecode` поз, магия, `PoseSize`), отправку `OTP2`, приём
  `OTP2` на хосте; `HostFollower` — только `StatePlayback`. Удалить мёртвые ключи (`RemoteSmoothing`,
  `HighPingMs`, `HysteresisMs`, `CheckpointSeconds`, `Checkpoint*`, `AcceptDegrees`, `StuckMeters`, `StuckSeconds` —
  сначала `rg` по каждому: используется ли). В отчёте — список удалённого с `файл:строка`.

---

### Этап 4. Взаимодействия: контакт, лебёдка, буксировка

**Решение владельца (D-1 в §7):** контакт машин **не** передаёт авторитет хосту. Каждый владелец сам решает реакцию
своей машины на тела других (после T3.7 у них правильные скорости). Это убирает откаты на таранах (D5). Хосту
авторитет передаётся только для **буксировки/лебёдки между двумя машинами игроков**.

**Обязательное условие владельца — инвариант синхронности:** у всех игроков состояние каждой машины одно и то же.
Единственная правда о машине S — её владелец; хост, наблюдатели и Fusion-объект S только воспроизводят поток S
(`StatePlayback`), никто не досчитывает S своей физикой «насовсем». Следствия, которые исполнитель обязан соблюсти:
- хост-прокси клиента и kinematic/servo-тела чужих машин у клиентов **никогда** не остаются в позе, отличной от потока
  дольше коррекции T4.0 (≤ 0,3 с): столкновение может их толкнуть, но поток возвращает их к правде владельца;
- реакция на удар у пострадавшего появляется **только** через его собственную симуляцию (его ПК столкнул его машину с
  телом тарана) и дальше расходится всем через его поток; никакой «дорисовки удара» у наблюдателей;
- проверяется метриками §5.2 (`extrapErr`, `proxyPull`) и сравнением позы S у всех игроков в один момент (T4.4).

**T4.0 Тело чужой машины в физике (владелец — `RemoteCarView`)**
- Проблема: в контакте моя машина сталкивается с телом чужой машины. Если тело kinematic и стоит «в прошлом» на
  задержку воспроизведения (`delay` 50–250 мс), удар приходится в пустоту или в стену бесконечной массы.
- Поза тела для физики = `StatePlayback.SampleAt(stepEndLocal, delayOverride: 0)` (экстраполяция к «сейчас», ≤ 0,15 с,
  правила экстраполяции T3.4); поза для отрисовки — как в T3.5 (с задержкой). Это два разных вызова одного владельца,
  а не два алгоритма.
- Ключ `[Netcode] ProxyBody = Kinematic` (`Kinematic`/`Servo`):
  - `Kinematic` — `MovePosition/MoveRotation` (T3.7); масса бесконечная, удар как о стену;
  - `Servo` — тело динамическое с настоящей массой машины, каждый физический шаг `velocity`/`angularVelocity` =
    скорость потока + `(цель − поза) / ServoSeconds` (`ServoSeconds` = 0,1), ограничение коррекции 10 м/с; столкновение
    честно обменивается импульсом, а сервопривод за ≤ 0,3 с возвращает тело к правде владельца (`proxyPull`).
- Порядок: сначала `Kinematic` (проще), **LIVE** A/B на таране 30–60 км/ч; если удар ощущается «стеной» или машины
  слипаются — `Servo` по умолчанию. Решение и цифры — в отчёт и §8.4.
- Тесты (Logic): расчёт servo-скорости — цель совпадает → скорость потока; отклонение 1 м при `ServoSeconds` 0,1 →
  +10 м/с к цели, ограничение срабатывает; экстраполяция к «сейчас» при `Stale` → поза hold, скорость 0.

**T4.1 `InteractionLogic` (Logic, эволюция `SuspendLogic`)**
- Вход на кадр: `contactLocal`, `contactHost`, `winch ∈ {None, World, Car(slot)}` локально и от хоста, `now`.
- Режимы: `Owner`, `HostShared`. Переход в `HostShared`: `winch == Car` (у себя или от хоста) или
  (`ContactAuthority == Host` и контакт). Выход: условий нет `ResumeSeconds` (≥ 1,5 с) **и** в режиме пробыли
  ≥ `MinDwellSeconds` (2 с). Это убирает «suspend/resume» десятки раз.
- Новые ключи: `[Netcode] ContactAuthority = Owner` (`Owner`/`Host`), `[Netcode] WorldWinchAuthority = Owner`
  (`Owner`/`Host`), `[Netcode] SharedMinDwellSeconds = 2`.
- Тесты: контакт 0,2 с при `Owner` → режим не меняется; трос к машине 5 с → `HostShared` 5 + 1,5 с; дребезг троса
  10 раз за 3 с → один вход, один выход; `WorldWinchAuthority=Host` + трос за дерево → `HostShared`.

**T4.2 Применение режима**
- Клиент (`OwnCar`): `HostShared` = то, что сейчас делает suspend (phase 2 пауза, `OwnLocal` выключен, мягкая
  коррекция фазы 1). `Owner` = обычный путь.
- Хост (`HostFollower`): в `HostShared` не ведёт прокси клиента (ваниль); в `Owner` ведёт всегда, контакт-пауза
  (`ContactPause`, `ContactHoldSeconds`) при `ContactAuthority=Owner` **не применяется** (иначе хост решает контакт
  сам — снова две правды). Флаги хоста (`FlagSuspendContact/Winch`) отправлять по новому правилу.
- Лебёдка за дерево при `WorldWinchAuthority=Owner`: машина остаётся у владельца; хост ведёт прокси по потоку.
  **LIVE**-проверка: подъём на лебёдке на склон — у владельца плавно, у наблюдателей без рывков; если хост-прокси
  «рвёт трос» или дёргается — записать и временно поставить `Host` по умолчанию (решение владельца).
- Готово (**LIVE**): §5.2.

**T4.3 Исключение лебёдки в пересимуляции** — по данным T1.4: если ваниль тоже бросает — ничего не делать, записать;
если только с модом — найти, какое наше восстановление (`OwnCar.Restore` слов VPP) оставляет лебёдку в
несогласованном состоянии, и не восстанавливать машину, пока `winch != None`.

**T4.4 Проверка синхронности (инвариант D-1)**
- `[NetStats]` на каждом ПК раз в 5 с пишет строку `[Sync] slot=S seq=N pos=(x,y,z)` для каждой машины: поза, которую
  этот ПК показывает для состояния с номером `seq` потока S (у владельца — своя поза в момент отправки `seq`).
- Скрипт в `tools/` (не в плагине): берёт диагностику всех игроков (T6.4 «Диагностика у всех»), сопоставляет строки по
  `(S, seq)` и печатает расхождение позы между ПК: p50/p99/max.
- Готово (**LIVE**, протокол §5.5): расхождение p99 ≤ 0,3 м, max ≤ 1 м (кроме снапов по эпохе), в том числе в окне 2 с
  после каждого тарана.

---

### Этап 5. Переезд ответственностей (поведение не меняется)

Порядок строго по одному пункту на коммит, после каждого — LogicTests, обе сборки, короткий **LIVE**-заход.

1. `HostFollower.cs` ← хостовая часть `CarSync.cs` (`HostApplyCars` разбить на методы < 50 строк: выбор цели,
   outage, recovery, follow, snap). `CarSync.cs` после этапа — только флаги сессии и контакт-бокс (`BoxOf`).
2. Контакт-бокс: один владелец (`CarSync.BoxOf` по коллайдерам); `OwnCar.UpdateSuspend` перестаёт строить свой бокс
   из cfg и вызывает его.
3. `JoinFlow.cs` ← `Overlay.Join/PollQuery/Watchdog/Recover/BeginWatch`; `Overlay.cs` — только окно F7 и вызов `NetFrame`.
4. `NetClock` в старом коде: `ClientLink.Now`, `HostPings.Now`, `OwnCar.Now` → `NetClock.Now` (свойства-прокси удалить).
5. `Updater.cs`: HTTP/установка → `UpdateJob.cs`, окно → T6.7.

---

### Этап 6. UI и удобство (QoL)

Приоритеты: P0 — в 0.2.0 обязательно, P1 — желательно, P2 — если останется время.

**T6.1 (P0) Окно модов: управление геймпадом** — сделано в T1.3; здесь только перенос на `UiModal` и полировка.

**T6.2 (P0) Прогресс подключения без F7**
- Сейчас без F7 игрок не видит `status.querying/joining/connectingSlot/connected`, ошибки видны только в F7.
- Новый `JoinToast` (Canvas, низ экрана, sortingOrder 700, без перехвата ввода): показывает текущий
  `LanState.LastStatus` во время подключения; после `status.connected` — 3 с и скрыть. Ошибки
  (`recover.*`, `status.hostSilent/modMismatch/gameMismatch/full/noArea/noSuchArea/joinNotStarted`) — модальное окно
  через `UiModal` + `UiNav`: текст, кнопка «OK» (A), подсказка «Подробности — F7».
- Владелец текста — `LanState` (добавить событие `StatusChanged`); `JoinToast` только рисует.

**T6.3 (P1) Окно модов: понятнее**
- Имена модов вместо id (если `ModManager`/Modio кэш даёт имя — найти через reflect; иначе id), состояние каждого
  (в очереди / качается N % / установлен / ошибка), кнопка «Повторить» при ошибке или таймауте.

**T6.4 (P1) Панель кооп-сессии с геймпада**
- Кнопка «Локальный кооп» в меню паузы игры (найти класс меню паузы через reflect, по образцу
  `ContextUIMainMenu`/`CoopMenu` — клон кнопки, явная навигация через `UiNav`).
- Панель (Canvas): игроки (ник из игры, пинг, потери, статус: ok / общий режим / нет связи N с — данные уже есть у
  `HostPings`/`LinkLabels`; хост рассылает сводку раз в 1 с новым маленьким пакетом `OTPL`), действия:
  - **«Пересинхронизировать мою машину»** — `CarStreamSender.BumpEpoch("user")` + хост делает `nrb.Teleport` прокси
    к позе клиента: ручной выход из любой рассинхронизации без телепорта по карте;
  - **«Сохранить диагностику»** (T0.2) с тостом «сохранено: <имя файла>»;
  - **«Диагностика у всех»** (хост): пакет `OTDX` всем → каждый мод делает `ExportZip("host request")` и показывает
    тост «отправьте файл хосту»; так приходят логи наблюдателей (D2 нельзя было разобрать именно без них);
  - «Покинуть сессию» — штатный выход игры.
- F7 остаётся для расширенных переключателей.

**T6.5 (P1) Уведомления сессии** — тосты через `JoinToast`: «хост вышел», «связь с хостом потеряна N с», «повторное
подключение…» (`FreezeWatch`), «игрок X подключился/вышел».

**T6.6 (P2) F7 с геймпада** — не делать IMGUI-навигацию; всё нужное игроку — в T6.4.

**T6.7 (P2) Промпт обновления на Canvas** — через `UiModal`/`UiNav`, тот же вид, что окно модов; удалить IMGUI-фокус
`Updater._focus` и выключение `EventSystem`.

Для всех: тексты EN+RU, проверка на 1280×720 и 2560×1440, только геймпад / только клавиатура / только мышь.

---

### Этап 7. Релиз 0.2.0 (только после «да» владельца)

- **T7.1 Версия:** `0.2.0` (три части) в `Plugin.cs`, `OthLan.Plugin.csproj`, csproj установщика и Launch,
  UA в `Launch/Program.cs`, `modVersion` манифеста, строка в LogicTests (Hang) — `rg "0\.1\.9\.3"` должен стать пустым
  в исходниках.
- **T7.2 Миграция cfg:** `[Meta] ConfigVersion` +1; новые дефолты (`ClientPhysicsMode`, `ContactAuthority`,
  `WorldWinchAuthority`, `RelayCarState`, решение T2.4) применить один раз с логом, как делали прошлые миграции.
- **T7.3 Документы:** CHANGELOG EN+RU, `docs/release-notes-v0.2.0.md`, `docs/CAR-SYNC.md` §2–3 и
  `docs/NETCODE-v4-LOCAL-OWN-CAR.md` (новая схема), `docs/HANDOFF.md` §3.3–3.4.
- **T7.4 Упаковка:** по HANDOFF §8; в скрипт упаковки добавить в список удаления `docs/DEV-0.2.0-PLAN.md`; leak-скан 0.
- Публикация, тег, релиз — только по «да».

---

## 4. Что НЕ делать

- Не возвращать машины клиентам через предсказание Fusion «потому что так проще» — это возвращает D3/D5.
- Не включать `RemoteInterpolate=false` по умолчанию как «фикс колёс» — колёса чинятся визуалом (T1.1/T3.6).
- Не писать второе сглаживание/экстраполяцию «рядом» — владельцы: `StatePlayback` (поток) и `RemoteSmoother`
  (запасной путь), затухание — `RemoteSmoother.Decay/TauFor`.
- Не трогать сетевые поля `NetworkedVehicleStatus`/`FusionPlayer` на прокси (запись в сетевые поля чужого объекта).
- Не вызывать `Physics.Simulate` где-либо, кроме `OwnLocal.LocalStep`.
- Не удалять `OwnLocal`/`OwnRender` в 0.2.0 (только решение «по умолчанию выкл», T2.4).
- Не менять транспорт Fusion, слоты, обнаружение лобби, установщик.
- Не делать IMGUI-окна новыми; всё новое — Canvas + `UiNav` + `UiModal`.

---

## 5. Приёмка (цифры из `[NetStats]`, сессия ≥ 10 минут, 3–4 игрока, Radmin)

### 5.1 Вид чужих машин (на каждом наблюдателе-клиенте)
- remote на экране: p99 ≤ 10 см, `>50cm` ≤ 1 в минуту, `>2m` = 0 (кроме снапов по эпохе);
- `remoteBelow` = 0;
- источник `Stream` ≥ 98 % кадров при паузах снапшотов Fusion (`snapshot gaps` не влияют на remote-метрику);
- колёса: при скорости > 2 м/с отрисованная угловая скорость в пределах 10 % от v/r (новая метрика `wheelErr` из
  `RemoteWheelVisuals`); руль и подвеска видны; фары совпадают с владельцем ≤ 0,5 с.

### 5.2 Взаимодействия
- таран на 30–60 км/ч: у обоих водителей своя машина без скачков > 50 см; у третьего наблюдателя — без «прохода
  сквозь» и без снапов > 2 м; пострадавшую машину реально толкает (видно у всех одинаково);
- синхронность (T4.4): расхождение позы одной машины между любыми двумя ПК p99 ≤ 0,3 м, max ≤ 1 м, включая 2 с после
  тарана; `extrapErr` p99 ≤ 0,3 м; `proxyPull` (время возврата тела к потоку после толчка) ≤ 0,3 с;
- лебёдка за дерево (`WorldWinchAuthority=Owner`): 0 переходов режима; плавный подъём у владельца и наблюдателей;
- буксировка игрок↔игрок: один вход в `HostShared`, один выход; после выхода расхождение хост/клиент < 0,5 м за 1 с.

### 5.3 Своя машина и кадр (клиент)
- на экране: p99 ≤ 12 см, `>50cm` ≤ 1 в минуту (кроме телепортов игры);
- `frameMs p99` ≤ 30 мс, `>50ms` ≤ 1 в минуту; `cost resim p99` ≤ 3 мс.

### 5.4 Код
- нет функции > 80 строк в изменённых файлах (цель < 50); `CarSync.cs` < 400 строк; `Overlay.cs` < 400 строк;
- `rg "FindObjectsOfType<FusionPlayer>"` — 1 место; `rg "Physics.Simulate"` — 1 место;
- LogicTests зелёные, новые тесты для каждого Logic-модуля этапа.

### 5.5 Протокол живого теста (владелец + друзья)
Сценарий 10 минут, все на одной dev-сборке: колонна 2 минуты → падение с уступа и переворот → тараны → лебёдка за
дерево → буксировка игрока → респаун одного игрока → фары вкл/выкл в темноте. В конце хост жмёт «Диагностика у всех»
(T6.4; до него — каждый F7 → Export). Архивы от **всех** игроков — исполнителю.

---

## 6. Риски и откат

| Риск | Признак | Откат |
|---|---|---|
| `SimulateForward` ломает физику объектов мира на клиенте | дёргаются сетевые физические объекты, исключения | `[Netcode] ClientPhysicsMode=Game` |
| Поток `OTC3` теряется/блокируется (фаервол на 27016 у хоста — уже нужен) | `relay` счётчики 0, источник `Fusion` | `[Netcode] RelayCarState=false` → запасной путь (исправленный `RemoteSmoother`) |
| Конвенция осей колёс неверна | колёса крутятся «боком» | §8.2 по данным T1.1/T3.0 |
| `ContactAuthority=Owner` даёт «мягкие» столкновения | владелец недоволен ощущением тарана | сначала `ProxyBody=Servo` (T4.0); только если не помогло — `ContactAuthority=Host` (старое поведение, но с гистерезисом T4.1) |
| Тела чужих машин после удара расходятся с потоком | T4.4 > 0,3 м, `proxyPull` растёт | уменьшить `ServoSeconds`; `ProxyBody=Kinematic` |
| Лебёдка за дерево у владельца рвёт трос на хосте | хост-прокси дёргается | `WorldWinchAuthority=Host` |
| Обновление игры меняет поля/RVA | `[Hooks]`/`BuildGate` | HANDOFF §6 |

---

## 7. Решения владельца (приняты 2026-10-24, исполнитель не пересматривает)

- **D-1. Принято:** контакт машин без передачи хосту (`ContactAuthority=Owner`) **при условии**, что у всех игроков
  состояние машин друг друга синхронизировано — инвариант синхронности этапа 4, T4.0, T4.4, приёмка §5.2.
- **D-2. Принято:** этап 1 отдельно не выпускать, всё копится до 0.2.0.
- **D-3. Принято:** T6.4 (панель кооп-сессии в меню паузы, «Пересинхронизировать машину», «Диагностика у всех») —
  в 0.2.0.

---

## 8. Справочник для исполнителя (заполняется по ходу)

### 8.1 Где что сейчас (v0.1.9.3)
- Чужие машины на клиенте: `RemoteSmooth.TuneProxy` (несимулируемые + kinematic), `RemoteSmooth.LateTick` (root-gap),
  `RemoteVisuals.LateTick` (свет/колёса/ливрея), вызываются из `Overlay.LateUpdate`.
- Своя машина клиента: `OwnCar` (phase 2: захват перед reset, восстановление после пересимуляции), `OwnLocal`
  (локальные шаги, `Physics.Simulate`), `OwnRender` (detour `NetworkRigidbody.Render`, сглаживание отрисовки).
- Хост: `CarSync.HostApplyCars` (сервопривод к позе клиента, outage, recovery, контакт/лебёдка-пауза),
  `PosePlayback` (буфер по времени прихода), `HostFollowLogic.GameMovedCar` + `nrb.Teleport`.
- Побочный канал: клиент `ClientLink` (UDP на host:27016, приём в потоке `Rx`), хост `HostInfo`/`HostPings`
  (`SendTo`, `Broadcast`, `EndpointOf(slot)`), пакеты: пинг, `OTP2` (поза), `OTAC` (принять позу хоста), `OTST`
  (статус связи).
- Порог/формулы: `SyncLogic.cs` (`CarSyncLogic`, `OutageLogic`), `SmoothLogic.cs` (`RemoteSmoother`),
  `SuspendLogic.cs`, `NetLogic.cs` (`Smoothness`, метрики).

### 8.2 Колёса: конвенция и источники — *заполнить в T1.1/T3.0*
- Ось вращения колеса (local): **X** (roll). Ось руля: **Y**. Подвеска: не пишется в T1.1 (этап 3).
- Поля: `NetworkedVehicleStatus.WheelsAngularVelocity` (рад/с, на прокси часто нули), запасной путь `bodyV/radius` через `WheelSpinLogic`.
- Transform: `WheelTransformUpdater.wheelTransform.localRotation = Euler(0,steer,0) * rest * Euler(spin,0,0)`.
- Probe LIVE отложен владельцем до после релиза 0.2.0.

### 8.3 Лебёдка — *заполнить в T1.4*
- Поля якоря (`VehicleWinchSystem`): `WinchIsAttached`, `WinchedCarPlayerRef` (игрок), `_WinchAnchorPoint`,
  `_WinchedMovableObjectRigidbody` / `ObjectAttached` (мир), `_WinchAttachPointInCar`.
- Классификация: `WinchKindLogic` + `CarSync.WinchKindOf` → `world` | `car:<slot>` в логе suspend.
- Ваниль бросает NRE в пересимуляции: **не проверено** (LIVE после релиза).

### 8.4 Решения по данным — *заполнять*
- T2.4 `OwnLocalClock` по умолчанию: **оставить `true`** (как в 0.1.9.x). LIVE A/B (on/off, 10 мин) отложен
  владельцем до после релиза 0.2.0; без цифр `cost localStep` решение «выключить» не принимаем. Код не удалён.

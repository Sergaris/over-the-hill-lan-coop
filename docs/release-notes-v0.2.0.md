## About

**Unofficial** LAN / virtual-LAN co-op mod for Over The Hill. Not affiliated with, endorsed by, or supported by Funselektor Labs or Exit Games (Photon). Contains no game files: every player needs their own copy of the game from Steam.

## О моде

**Неофициальный** мод для кооператива в Over The Hill по локальной или виртуальной сети. Не связан с Funselektor Labs и Exit Games (Photon), не одобрен и не поддерживается ими. Файлов игры нет: каждому игроку нужна своя копия игры из Steam.

---

## Download

Under **Assets** below, click **`OTH-LAN-0.2.0.zip`** (not "Source code").

| File | What it is | Size | SHA-256 |
|---|---|---|---|
| `OTH-LAN-0.2.0.zip` | Installer (recommended) | *(fill at package)* | *(fill)* |
| `OTH-LAN-0.2.0-manual.zip` | Manual install without the installer | *(fill)* | *(fill)* |

Plugin `OthLan.dll` sha256 *(fill after public build)*. All checksums are also in `SHA256SUMS.txt`. Optional asset `6000.0.75.zip` is the offline Unity base libraries pack used by `OTH-LAN-Launch.exe` if BepInEx hangs on first download.

## Скачать

В блоке **Assets** ниже нажмите **`OTH-LAN-0.2.0.zip`** (не «Source code»).

| Файл | Что это | Размер | SHA-256 |
|---|---|---|---|
| `OTH-LAN-0.2.0.zip` | Установщик (рекомендуется) | *(заполнить при сборке)* | *(заполнить)* |
| `OTH-LAN-0.2.0-manual.zip` | Ручная установка без установщика | *(заполнить)* | *(заполнить)* |

Плагин `OthLan.dll` sha256 *(заполнить после публичной сборки)*. Все контрольные суммы есть также в `SHA256SUMS.txt`. Опциональный файл `6000.0.75.zip` — офлайн-пакет Unity base libraries для `OTH-LAN-Launch.exe`, если BepInEx зависает при первой загрузке.

---

## Install

1. Unpack `OTH-LAN-0.2.0.zip` (right-click → *Extract All…*).
2. Close the game, run `OTH-LAN-Setup.exe`, press **Install** (the installer is not code-signed: on "Windows protected your PC" click *More info* → *Run anyway*).
3. Start Over The Hill from Steam (the first start takes 1–3 minutes longer and needs internet once). If that first start hangs on downloading Unity libraries, run `OTH-LAN-Launch.exe` from the same folder instead (it fetches the offline pack and restarts).
4. Everyone joins the same LAN or virtual LAN (Radmin VPN, Hamachi, ZeroTier). Host: **Main menu → Local Co-op → Become host**. Others: **Main menu → Local Co-op**, pick the host, **Join** (or **Add by IP** with `IP:27015`).

All players need 0.2.0: an older version is refused with a message that names both versions. Settings are kept; Uninstall: run `OTH-LAN-Setup.exe` again → **Uninstall**.

## Установка

1. Распакуйте `OTH-LAN-0.2.0.zip` (правой кнопкой → «Извлечь всё…»).
2. Закройте игру, запустите `OTH-LAN-Setup.exe`, нажмите **«Установить»** (установщик не подписан: на «Система Windows защитила ваш компьютер» нажмите «Подробнее» → «Выполнить в любом случае»).
3. Запустите Over The Hill через Steam (первый запуск дольше на 1–3 минуты и один раз требует интернет). Если первый запуск зависает на скачивании Unity libraries, вместо этого запустите `OTH-LAN-Launch.exe` из той же папки (он скачает офлайн-пакет и перезапустит игру).
4. Все подключаются к одной локальной или виртуальной сети (Radmin VPN, Hamachi, ZeroTier). Хост: **главное меню → «Локальный кооп» → «Стать хостом»**. Остальные: **главное меню → «Локальный кооп»**, выберите хост, **«Войти»** (или **«Добавить по IP»** с `IP:27015`).

Всем нужна 0.2.0: более старая версия отклоняется с сообщением, где названы обе версии. Настройки сохраняются; удаление: снова `OTH-LAN-Setup.exe` → **«Удалить»**.

---

## Updating

From 0.1.8.1 / 0.1.9 / 0.1.9.1 / 0.1.9.2 / 0.1.9.3 the main menu offers new versions itself: **Install / Later / Skip this version**. If the check cannot reach GitHub, a notice with **Retry / Later** appears. The download is checked against `SHA256SUMS.txt` of the same release and is copied in after you quit the game. Switch it off in F7 ("Check for updates"). From 0.1.8 or older, install 0.2.0 by hand once (steps above).

## Обновление

Начиная с 0.1.8.1 / 0.1.9 / 0.1.9.1 / 0.1.9.2 / 0.1.9.3 главное меню само предлагает новые версии: **«Установить» / «Позже» / «Пропустить эту версию»**. Если GitHub недоступен, появляется уведомление **«Повторить» / «Позже»**. Скачанный файл сверяется с `SHA256SUMS.txt` того же релиза и копируется после выхода из игры. Выключается в F7 («Проверять обновления»). С 0.1.8 и более ранних версий установите 0.2.0 вручную один раз (шаги выше).

---

## What's new in 0.2.0

- Smoother remote cars and wheels: dead networked wheel spin falls back to body speed; RemoteSmoother no longer dives underground after a fall; gamepad can focus the missing-mods dialog.
- Lower client CPU when Fusion resimulates: `ClientPhysicsSimulation=SimulateForward`; one shared car scan; own-car local steps run after Fusion each frame.
- New OTC3 car-state stream (sent alongside the old pose packets): host relays it; observers draw from a playback buffer when the stream is fresh. Further interaction/UI polish follows live testing after this release.

## Что нового в 0.2.0

- Плавнее чужие машины и колёса: при нулевом сетевом спине берётся скорость кузова; RemoteSmoother не уводит машину под землю после падения; геймпад фокусирует окно недостающих модов.
- Меньше нагрузки на клиенте при пересимуляции Fusion: `ClientPhysicsSimulation=SimulateForward`; один общий скан машин; локальные шаги своей машины — после Fusion в кадре.
- Новый поток состояния OTC3 (параллельно старым пакетам поз): хост пересылает; наблюдатели рисуют из буфера воспроизведения, пока поток свежий. Доводка взаимодействий и UI — после живых тестов этого релиза.

---

## Known limits

- Tested mainly with synthetic unit checks and builds; full multi-PC live validation of the new stream path is planned right after this release.
- Collisions with other players' cars are still approximate; player↔player winch stays on the older host-authority path until the next interaction pass.
- OTP2 pose packets remain until the OTC3 path is confirmed in live sessions (then they will be removed).

## Известные ограничения

- Проверено в основном синтетическими тестами и сборками; полный живой прогон нового потока на нескольких ПК — сразу после этого релиза.
- Столкновения с чужими машинами по-прежнему приблизительные; лебёдка игрок↔игрок на старом host-authority пути до следующего прохода по взаимодействиям.
- Пакеты OTP2 остаются, пока OTC3 не подтвердят в живых сессиях (потом их уберут).

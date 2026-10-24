## About

**Unofficial** LAN / virtual-LAN co-op mod for Over The Hill. Not affiliated with, endorsed by, or supported by Funselektor Labs or Exit Games (Photon). Contains no game files: every player needs their own copy of the game from Steam.

## О моде

**Неофициальный** мод для кооператива в Over The Hill по локальной или виртуальной сети. Не связан с Funselektor Labs и Exit Games (Photon), не одобрен и не поддерживается ими. Файлов игры нет: каждому игроку нужна своя копия игры из Steam.

---

## Download

Under **Assets** below, click **`OTH-LAN-0.2.2.zip`** (not "Source code").

| File | What it is | Size | SHA-256 |
|---|---|---|---|
| `OTH-LAN-0.2.2.zip` | Installer (recommended) | 130241554 B | `3a3f042a2ea96b34cfac11951fa64d4e1d6d435b5fcd956f12758681dc8b7625` |
| `OTH-LAN-0.2.2-manual.zip` | Manual install without the installer | 34578090 B | `e3ce02ceccf4ee8a5c1e31f25f5d7e80c01f96e2cea4cb62f9256ea51f6855e6` |

Plugin `OthLan.dll` sha256 `cb19748d61a0f4423312cd64fefc94cfa49d470f89178bc93daa82b43bfdcd5d`. All checksums are also in `SHA256SUMS.txt`. Optional asset `6000.0.75.zip` is the offline Unity base libraries pack used by `OTH-LAN-Launch.exe` if BepInEx hangs on first download.

## Скачать

В блоке **Assets** ниже нажмите **`OTH-LAN-0.2.2.zip`** (не «Source code»).

| Файл | Что это | Размер | SHA-256 |
|---|---|---|---|
| `OTH-LAN-0.2.2.zip` | Установщик (рекомендуется) | 130241554 Б | `3a3f042a2ea96b34cfac11951fa64d4e1d6d435b5fcd956f12758681dc8b7625` |
| `OTH-LAN-0.2.2-manual.zip` | Ручная установка без установщика | 34578090 Б | `e3ce02ceccf4ee8a5c1e31f25f5d7e80c01f96e2cea4cb62f9256ea51f6855e6` |

Плагин `OthLan.dll` sha256 `cb19748d61a0f4423312cd64fefc94cfa49d470f89178bc93daa82b43bfdcd5d`. Все контрольные суммы есть также в `SHA256SUMS.txt`. Опциональный файл `6000.0.75.zip` — офлайн-пакет Unity base libraries для `OTH-LAN-Launch.exe`, если BepInEx зависает при первой загрузке.

---

## Install

1. Unpack `OTH-LAN-0.2.2.zip` (right-click → *Extract All…*).
2. Close the game, run `OTH-LAN-Setup.exe`, press **Install** (the installer is not code-signed: on "Windows protected your PC" click *More info* → *Run anyway*).
3. Start Over The Hill from Steam (the first start takes 1–3 minutes longer and needs internet once). If that first start hangs on downloading Unity libraries, run `OTH-LAN-Launch.exe` from the same folder instead (it fetches the offline pack and restarts).
4. Everyone joins the same LAN or virtual LAN (Radmin VPN, Hamachi, ZeroTier). Host: **Main menu → Local Co-op → Become host**. Others: **Main menu → Local Co-op**, pick the host, **Join** (or **Add by IP** with `IP:27015`).

All players need 0.2.2: an older version is refused with a message that names both versions. Settings are kept; Uninstall: run `OTH-LAN-Setup.exe` again → **Uninstall**.

## Установка

1. Распакуйте `OTH-LAN-0.2.2.zip` (правой кнопкой → «Извлечь всё…»).
2. Закройте игру, запустите `OTH-LAN-Setup.exe`, нажмите **«Установить»** (установщик не подписан: на «Система Windows защитила ваш компьютер» нажмите «Подробнее» → «Выполнить в любом случае»).
3. Запустите Over The Hill через Steam (первый запуск дольше на 1–3 минуты и один раз требует интернет). Если первый запуск зависает на скачивании Unity libraries, вместо этого запустите `OTH-LAN-Launch.exe` из той же папки (он скачает офлайн-пакет и перезапустит игру).
4. Все подключаются к одной локальной или виртуальной сети (Radmin VPN, Hamachi, ZeroTier). Хост: **главное меню → «Локальный кооп» → «Стать хостом»**. Остальные: **главное меню → «Локальный кооп»**, выберите хост, **«Войти»** (или **«Добавить по IP»** с `IP:27015`).

Всем нужна 0.2.2: более старая версия отклоняется с сообщением, где названы обе версии. Настройки сохраняются; удаление: снова `OTH-LAN-Setup.exe` → **«Удалить»**.

---

## Updating

From 0.1.8.1 / 0.1.9 / 0.1.9.1 / 0.1.9.2 / 0.1.9.3 / 0.2.0 / 0.2.1 the main menu offers new versions itself: **Install / Later / Skip this version**. If the check cannot reach GitHub, a notice with **Retry / Later** appears. The download is checked against `SHA256SUMS.txt` of the same release and is copied in after you quit the game. Switch it off in F7 ("Check for updates"). From 0.1.8 or older, install 0.2.2 by hand once (steps above).

## Обновление

Начиная с 0.1.8.1 / 0.1.9 / 0.1.9.1 / 0.1.9.2 / 0.1.9.3 / 0.2.0 / 0.2.1 главное меню само предлагает новые версии: **«Установить» / «Позже» / «Пропустить эту версию»**. Если GitHub недоступен, появляется уведомление **«Повторить» / «Позже»**. Скачанный файл сверяется с `SHA256SUMS.txt` того же релиза и копируется после выхода из игры. Выключается в F7 («Проверять обновления»). С 0.1.8 и более ранних версий установите 0.2.2 вручную один раз (шаги выше).

---

## What's new in 0.2.2

- Remote cars: blend and proxy pose, wheel visuals from the OTC3 stream, PrePhysics kinematic MovePosition (Servo PhysP flush between Fusion ticks).
- Interactions: Owner contact authority by default; HostShared for car winch / optional world winch.
- Modules: HostFollower, JoinFlow, UpdateJob (HTTP/install split from the updater window).
- UI: join progress toast without F7; coop session panel from the pause menu (resync / save diagnostics / request all).
- More synthetic LogicTests for stream edges, session wire, updater parse, InteractionLogic hysteresis.

## Что нового в 0.2.2

- Чужие машины: blend и proxy-поза, колёса из потока OTC3, PrePhysics kinematic MovePosition (Servo PhysP между тиками Fusion).
- Взаимодействия: по умолчанию Owner для контакта; HostShared для лебёдки игрок↔игрок / опционально мир.
- Модули: HostFollower, JoinFlow, UpdateJob (HTTP/установка отделены от окна апдейтера).
- UI: тост прогресса входа без F7; панель кооп-сессии из меню паузы (ресинк / сохранить диагностику / запросить у всех).
- Больше синтетических LogicTests: края потока, сессия, разбор релиза, гистерезис InteractionLogic.

---

## Known limits

- Tested with synthetic unit checks and builds; full multi-PC live validation is planned right after this release.
- Collisions with other players' cars remain approximate under Owner contact; HostShared kicks in for car winch.
- OTP2 pose packets remain until the OTC3 path is confirmed in live sessions (then they will be removed).
- Update prompt is still IMGUI (Canvas prompt is P2); Now proxies on ClientLink/HostPings/OwnCar are aliases of NetClock.

## Известные ограничения

- Проверено синтетическими тестами и сборками; полный живой прогон на нескольких ПК — сразу после этого релиза.
- Столкновения с чужими машинами при Owner-контакте по-прежнему приблизительные; HostShared включается на лебёдке игрок↔игрок.
- Пакеты OTP2 остаются, пока OTC3 не подтвердят в живых сессиях (потом их уберут).
- Промпт обновления всё ещё IMGUI (Canvas — P2); Now-прокси у ClientLink/HostPings/OwnCar — алиасы NetClock.


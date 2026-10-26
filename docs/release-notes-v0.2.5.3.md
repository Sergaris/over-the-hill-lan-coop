## About

**Unofficial** LAN / virtual-LAN co-op mod for Over The Hill. Not affiliated with, endorsed by, or supported by Funselektor Labs or Exit Games (Photon). Contains no game files: every player needs their own copy of the game from Steam.

## О моде

**Неофициальный** мод для кооператива в Over The Hill по локальной или виртуальной сети. Не связан с Funselektor Labs и Exit Games (Photon), не одобрен и не поддерживается ими. Файлов игры нет: каждому игроку нужна своя копия игры из Steam.

---

## Download

Under **Assets** below, click **`OTH-LAN-0.2.5.3.zip`** (not "Source code").

| File | What it is | Size | SHA-256 |
|---|---|---|---|
| `OTH-LAN-0.2.5.3.zip` | Installer (recommended) | 130254519 B | `c0e22faa5568fc6076754a07bf8d2ff6a7122b79529fa9f0e2eeb3c53d055084` |
| `OTH-LAN-0.2.5.3-manual.zip` | Manual install without the installer | 34591486 B | `490f72a2fc80edb203a4de19205d4d2fdc4f8fef19f6b4492b8a62a4ac289cb3` |

Plugin `OthLan.dll` sha256 `c45647616d8e0a7fc794cb04df9cd4b8e5e67bdb3fc01636943a485345410f12`. All checksums are also in `SHA256SUMS.txt`. Optional asset `6000.0.75.zip` is the offline Unity base libraries pack used by `OTH-LAN-Launch.exe` if BepInEx hangs on first download.

## Скачать

В блоке **Assets** ниже нажмите **`OTH-LAN-0.2.5.3.zip`** (не «Source code»).

| Файл | Что это | Размер | SHA-256 |
|---|---|---|---|
| `OTH-LAN-0.2.5.3.zip` | Установщик (рекомендуется) | 130254519 Б | `c0e22faa5568fc6076754a07bf8d2ff6a7122b79529fa9f0e2eeb3c53d055084` |
| `OTH-LAN-0.2.5.3-manual.zip` | Ручная установка без установщика | 34591486 Б | `490f72a2fc80edb203a4de19205d4d2fdc4f8fef19f6b4492b8a62a4ac289cb3` |

Плагин `OthLan.dll` sha256 `c45647616d8e0a7fc794cb04df9cd4b8e5e67bdb3fc01636943a485345410f12`. Все контрольные суммы есть также в `SHA256SUMS.txt`. Опциональный файл `6000.0.75.zip` — офлайн-пакет Unity base libraries для `OTH-LAN-Launch.exe`, если BepInEx зависает при первой загрузке.

---

## Install

1. Unpack `OTH-LAN-0.2.5.3.zip` (right-click → *Extract All…*).
2. Close the game, run `OTH-LAN-Setup.exe`, press **Install** (the installer is not code-signed: on "Windows protected your PC" click *More info* → *Run anyway*).
3. Start Over The Hill from Steam (the first start takes 1–3 minutes longer and needs internet once). If that first start hangs on downloading Unity libraries, run `OTH-LAN-Launch.exe` from the same folder instead (it fetches the offline pack and restarts).
4. Everyone joins the same LAN or virtual LAN (Radmin VPN, Hamachi, ZeroTier). Host: **Main menu → Local Co-op → Become host**. Others: **Main menu → Local Co-op**, pick the host, **Join** (or **Add by IP** with `IP:27015`).

All players need 0.2.5.3: an older version is refused with a message that names both versions. Settings are kept; Uninstall: run `OTH-LAN-Setup.exe` again → **Uninstall**.

## Установка

1. Распакуйте `OTH-LAN-0.2.5.3.zip` (правой кнопкой → «Извлечь всё…»).
2. Закройте игру, запустите `OTH-LAN-Setup.exe`, нажмите **«Установить»** (установщик не подписан: на «Система Windows защитила ваш компьютер» нажмите «Подробнее» → «Выполнить в любом случае»).
3. Запустите Over The Hill через Steam (первый запуск дольше на 1–3 минуты и один раз требует интернет). Если первый запуск зависает на скачивании Unity libraries, вместо этого запустите `OTH-LAN-Launch.exe` из той же папки (он скачает офлайн-пакет и перезапустит игру).
4. Все подключаются к одной локальной или виртуальной сети (Radmin VPN, Hamachi, ZeroTier). Хост: **главное меню → «Локальный кооп» → «Стать хостом»**. Остальные: **главное меню → «Локальный кооп»**, выберите хост, **«Войти»** (или **«Добавить по IP»** с `IP:27015`).

Всем нужна 0.2.5.3: более старая версия отклоняется с сообщением, где названы обе версии. Настройки сохраняются; удаление: снова `OTH-LAN-Setup.exe` → **«Удалить»**.

---

## Updating

From 0.1.8.1 / 0.1.9 / 0.1.9.1 / 0.1.9.2 / 0.1.9.3 / 0.2.0 / 0.2.1 / 0.2.2 / 0.2.3 / 0.2.4 / 0.2.5 / 0.2.5.1 / 0.2.5.2 the main menu offers new versions itself: **Install / Later / Skip this version**. If the check cannot reach GitHub, a notice with **Retry / Later** appears. The download is checked against `SHA256SUMS.txt` of the same release and is copied in after you quit the game. Switch it off in F7 ("Check for updates"). From 0.1.8 or older, install 0.2.5.3 by hand once (steps above).

## Обновление

Начиная с 0.1.8.1 / 0.1.9 / 0.1.9.1 / 0.1.9.2 / 0.1.9.3 / 0.2.0 / 0.2.1 / 0.2.2 / 0.2.3 / 0.2.4 / 0.2.5 / 0.2.5.1 / 0.2.5.2 главное меню само предлагает новые версии: **«Установить» / «Позже» / «Пропустить эту версию»**. Если GitHub недоступен, появляется уведомление **«Повторить» / «Позже»**. Скачанный файл сверяется с `SHA256SUMS.txt` того же релиза и копируется после выхода из игры. Выключается в F7 («Проверять обновления»). С 0.1.8 и более ранних версий установите 0.2.5.3 вручную один раз (шаги выше).

---

## What's new in 0.2.5.3

- Third-party / non-Steam game copies: if Fusion version and data layout match a tested build, OTH-LAN asks once and can run (code differences alone no longer block).
- Steam and third-party players can join each other when both use the same OTH-LAN and Fusion versions.

## Что нового в 0.2.5.3

- Сторонние / не-Steam копии: если версия Fusion и раскладка данных совпадают с проверенной сборкой, OTH-LAN один раз спросит и может работать (одно отличие кода больше не блокирует).
- Игроки Steam и сторонней копии могут играть вместе при одинаковых OTH-LAN и Fusion.

---

## Known limits

- Tested on one PC with two game instances and simulated lag and packet loss. Report issues with F7 → Save diagnostics.
- A frozen abandoned car does not react to things other than player cars (for example a falling meteorite) until a player comes within winch reach. Switch it off with `ParkIdleCars = false` in `[Netcode]` of `BepInEx/config/dev.othlan.lan.cfg` on the host.
- Third-party copies still need the same Fusion 2.1.2 and matching data layout; a broken or very old crack can still be refused as incompatible. Both sides must run 0.2.5.3 (older hosts do not advertise Fusion for cross-hash join).

## Известные ограничения

- Проверено на одном ПК с двумя копиями игры и имитацией лага и потери пакетов. О проблемах — F7 → сохранить диагностику.
- Замороженная брошенная машина не реагирует ни на что, кроме машин игроков (например, на упавший метеорит), пока игрок не окажется в радиусе лебёдки. Отключается на хосте: `ParkIdleCars = false` в `[Netcode]` файла `BepInEx/config/dev.othlan.lan.cfg`.
- Сторонней копии всё равно нужны Fusion 2.1.2 и совпадающая раскладка данных; сильно сломанный или старый репак по-прежнему может быть incompatible. С обеих сторон нужна 0.2.5.3 (старые хосты не сообщают Fusion для join с другим hash).

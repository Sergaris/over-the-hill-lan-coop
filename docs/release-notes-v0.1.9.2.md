## About

**Unofficial** LAN / virtual-LAN co-op mod for Over The Hill. Not affiliated with, endorsed by, or supported by Funselektor Labs or Exit Games (Photon). Contains no game files: every player needs their own copy of the game from Steam.

## О моде

**Неофициальный** мод для кооператива в Over The Hill по локальной или виртуальной сети. Не связан с Funselektor Labs и Exit Games (Photon), не одобрен и не поддерживается ими. Файлов игры нет: каждому игроку нужна своя копия игры из Steam.

---

## Download

Under **Assets** below, click **`OTH-LAN-0.1.9.2.zip`** (not "Source code").

| File | What it is | Size | SHA-256 |
|---|---|---|---|
| `OTH-LAN-0.1.9.2.zip` | Installer (recommended) | 130208733 B | `f917957897d9ef83072f67cac02be32a15ff8df2b34945e2f686849142d4d5c8` |
| `OTH-LAN-0.1.9.2-manual.zip` | Manual install without the installer | 34545456 B | `733a0426f576f3d4f0e13190a0bdce24908709a4f8a7f9fc82f6124a6a014641` |

Plugin `OthLan.dll` sha256 `833148bdd56f718b64671f14605ba4c6c93276c3ad0970405caac35a0bf3942e`. All checksums are also in `SHA256SUMS.txt`. Optional asset `6000.0.75.zip` is the offline Unity base libraries pack used by `OTH-LAN-Launch.exe` if BepInEx hangs on first download.

## Скачать

В блоке **Assets** ниже нажмите **`OTH-LAN-0.1.9.2.zip`** (не «Source code»).

| Файл | Что это | Размер | SHA-256 |
|---|---|---|---|
| `OTH-LAN-0.1.9.2.zip` | Установщик (рекомендуется) | 130208733 Б | `f917957897d9ef83072f67cac02be32a15ff8df2b34945e2f686849142d4d5c8` |
| `OTH-LAN-0.1.9.2-manual.zip` | Ручная установка без установщика | 34545456 Б | `733a0426f576f3d4f0e13190a0bdce24908709a4f8a7f9fc82f6124a6a014641` |

Плагин `OthLan.dll` sha256 `833148bdd56f718b64671f14605ba4c6c93276c3ad0970405caac35a0bf3942e`. Все контрольные суммы есть также в `SHA256SUMS.txt`. Опциональный файл `6000.0.75.zip` — офлайн-пакет Unity base libraries для `OTH-LAN-Launch.exe`, если BepInEx зависает при первой загрузке.

---

## Install

1. Unpack `OTH-LAN-0.1.9.2.zip` (right-click → *Extract All…*).
2. Close the game, run `OTH-LAN-Setup.exe`, press **Install** (the installer is not code-signed: on "Windows protected your PC" click *More info* → *Run anyway*).
3. Start Over The Hill from Steam (the first start takes 1–3 minutes longer and needs internet once). If that first start hangs on downloading Unity libraries, run `OTH-LAN-Launch.exe` from the same folder instead (it fetches the offline pack and restarts).
4. Everyone joins the same LAN or virtual LAN (Radmin VPN, Hamachi, ZeroTier). Host: **Main menu → Local Co-op → Become host**. Others: **Main menu → Local Co-op**, pick the host, **Join** (or **Add by IP** with `IP:27015`).

All players need 0.1.9.2: an older version is refused with a message that names both versions. Settings are kept; Uninstall: run `OTH-LAN-Setup.exe` again → **Uninstall**.

## Установка

1. Распакуйте `OTH-LAN-0.1.9.2.zip` (правой кнопкой → «Извлечь всё…»).
2. Закройте игру, запустите `OTH-LAN-Setup.exe`, нажмите **«Установить»** (установщик не подписан: на «Система Windows защитила ваш компьютер» нажмите «Подробнее» → «Выполнить в любом случае»).
3. Запустите Over The Hill через Steam (первый запуск дольше на 1–3 минуты и один раз требует интернет). Если первый запуск зависает на скачивании Unity libraries, вместо этого запустите `OTH-LAN-Launch.exe` из той же папки (он скачает офлайн-пакет и перезапустит игру).
4. Все подключаются к одной локальной или виртуальной сети (Radmin VPN, Hamachi, ZeroTier). Хост: **главное меню → «Локальный кооп» → «Стать хостом»**. Остальные: **главное меню → «Локальный кооп»**, выберите хост, **«Войти»** (или **«Добавить по IP»** с `IP:27015`).

Всем нужна 0.1.9.2: более старая версия отклоняется с сообщением, где названы обе версии. Настройки сохраняются; удаление: снова `OTH-LAN-Setup.exe` → **«Удалить»**.

---

## Updating

From 0.1.8.1 / 0.1.9 / 0.1.9.1 the main menu offers new versions itself: **Install / Later / Skip this version**. If the check cannot reach GitHub, a notice with **Retry / Later** appears. The download is checked against `SHA256SUMS.txt` of the same release and is copied in after you quit the game. Switch it off in F7 ("Check for updates"). From 0.1.8 or older, install 0.1.9.2 by hand once (steps above).

## Обновление

Начиная с 0.1.8.1 / 0.1.9 / 0.1.9.1 главное меню само предлагает новые версии: **«Установить» / «Позже» / «Пропустить эту версию»**. Если GitHub недоступен, появляется уведомление **«Повторить» / «Позже»**. Скачанный файл сверяется с `SHA256SUMS.txt` того же релиза и копируется после выхода из игры. Выключается в F7 («Проверять обновления»). С 0.1.8 и более ранних версий установите 0.1.9.2 вручную один раз (шаги выше).

---

## What's new in 0.1.9.2

- Pre-join Modio download now calls the game's real temp installer (`AddTempModAsync`). Calling only `RequestTempMod` after Modio has already booted often did nothing, so the gate stayed on "still missing".
- Listens for Modio livery registry changes and logs diagnostics when a `(modId, variant)` pair never appears.
- New gate button **Join without mods** (also while downloading): joins the host with mods disabled for your client so you are not stuck if Modio fails.

## Что нового в 0.1.9.2

- Скачивание Modio перед входом вызывает настоящий temp-installer игры (`AddTempModAsync`). Один только `RequestTempMod` после уже запущенного Modio часто ничего не делал — гейт зависал на «ещё нет».
- Слушает изменения реестра раскрасок и пишет диагностику, если пара `(modId, вариант)` так и не появляется.
- Новая кнопка **Войти без модов** (и во время скачивания): вход к хосту с выключенными модами у вас, чтобы не застрять, если Modio не отвечает.

---

## Known limits

- Tested mainly on one PC with simulated ping and loss; real multi-PC sessions over the internet may differ.
- Collisions with other players' cars are approximate; towing between two players stays host-driven.
- Mod livery download needs a working Modio login in the game; both players need the same Workshop/Modio mods subscribed for textures to apply. "Join without mods" skips that wait — other cars may look unpainted.
- What is still to be tested and finished is in the [roadmap](https://github.com/Sergaris/over-the-hill-lan-coop/blob/main/docs/ROADMAP.md).

## Известные ограничения

- Проверено в основном на одном ПК с имитацией пинга и потерь; реальные сессии на нескольких ПК через интернет могут отличаться.
- Столкновения с машинами других игроков приблизительные; буксировка между двумя игроками остаётся под управлением хоста.
- Скачивание мод-раскраски требует входа в Modio в игре; у обоих игроков должны быть подписаны одни и те же Workshop/Modio моды. «Войти без модов» пропускает ожидание — чужие машины могут быть без раскраски.
- Что ещё нужно проверить и доделать, описано в [плане развития](https://github.com/Sergaris/over-the-hill-lan-coop/blob/main/docs/ROADMAP.md).


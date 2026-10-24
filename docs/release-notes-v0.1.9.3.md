## About

**Unofficial** LAN / virtual-LAN co-op mod for Over The Hill. Not affiliated with, endorsed by, or supported by Funselektor Labs or Exit Games (Photon). Contains no game files: every player needs their own copy of the game from Steam.

## О моде

**Неофициальный** мод для кооператива в Over The Hill по локальной или виртуальной сети. Не связан с Funselektor Labs и Exit Games (Photon), не одобрен и не поддерживается ими. Файлов игры нет: каждому игроку нужна своя копия игры из Steam.

---

## Download

Under **Assets** below, click **`OTH-LAN-0.1.9.3.zip`** (not "Source code").

| File | What it is | Size | SHA-256 |
|---|---|---|---|
| `OTH-LAN-0.1.9.3.zip` | Installer (recommended) | 130209332 B | `52c55017b9af5edd4ce9208997c4f6972b97b096ca05ffa345ffe5ca29178cb4` |
| `OTH-LAN-0.1.9.3-manual.zip` | Manual install without the installer | 34545927 B | `62a199e3381f51f342afb4ac1fffb127a4273ae2b0e60d8fed2ed95c39420225` |

Plugin `OthLan.dll` sha256 `8f91e2fa6bd2c3080f3b5f3f97d2911108c76ac48e056c559820ba94e326013c`. All checksums are also in `SHA256SUMS.txt`. Optional asset `6000.0.75.zip` is the offline Unity base libraries pack used by `OTH-LAN-Launch.exe` if BepInEx hangs on first download.

## Скачать

В блоке **Assets** ниже нажмите **`OTH-LAN-0.1.9.3.zip`** (не «Source code»).

| Файл | Что это | Размер | SHA-256 |
|---|---|---|---|
| `OTH-LAN-0.1.9.3.zip` | Установщик (рекомендуется) | 130209332 Б | `52c55017b9af5edd4ce9208997c4f6972b97b096ca05ffa345ffe5ca29178cb4` |
| `OTH-LAN-0.1.9.3-manual.zip` | Ручная установка без установщика | 34545927 Б | `62a199e3381f51f342afb4ac1fffb127a4273ae2b0e60d8fed2ed95c39420225` |

Плагин `OthLan.dll` sha256 `8f91e2fa6bd2c3080f3b5f3f97d2911108c76ac48e056c559820ba94e326013c`. Все контрольные суммы есть также в `SHA256SUMS.txt`. Опциональный файл `6000.0.75.zip` — офлайн-пакет Unity base libraries для `OTH-LAN-Launch.exe`, если BepInEx зависает при первой загрузке.

---

## Install

1. Unpack `OTH-LAN-0.1.9.3.zip` (right-click → *Extract All…*).
2. Close the game, run `OTH-LAN-Setup.exe`, press **Install** (the installer is not code-signed: on "Windows protected your PC" click *More info* → *Run anyway*).
3. Start Over The Hill from Steam (the first start takes 1–3 minutes longer and needs internet once). If that first start hangs on downloading Unity libraries, run `OTH-LAN-Launch.exe` from the same folder instead (it fetches the offline pack and restarts).
4. Everyone joins the same LAN or virtual LAN (Radmin VPN, Hamachi, ZeroTier). Host: **Main menu → Local Co-op → Become host**. Others: **Main menu → Local Co-op**, pick the host, **Join** (or **Add by IP** with `IP:27015`).

All players need 0.1.9.3: an older version is refused with a message that names both versions. Settings are kept; Uninstall: run `OTH-LAN-Setup.exe` again → **Uninstall**.

## Установка

1. Распакуйте `OTH-LAN-0.1.9.3.zip` (правой кнопкой → «Извлечь всё…»).
2. Закройте игру, запустите `OTH-LAN-Setup.exe`, нажмите **«Установить»** (установщик не подписан: на «Система Windows защитила ваш компьютер» нажмите «Подробнее» → «Выполнить в любом случае»).
3. Запустите Over The Hill через Steam (первый запуск дольше на 1–3 минуты и один раз требует интернет). Если первый запуск зависает на скачивании Unity libraries, вместо этого запустите `OTH-LAN-Launch.exe` из той же папки (он скачает офлайн-пакет и перезапустит игру).
4. Все подключаются к одной локальной или виртуальной сети (Radmin VPN, Hamachi, ZeroTier). Хост: **главное меню → «Локальный кооп» → «Стать хостом»**. Остальные: **главное меню → «Локальный кооп»**, выберите хост, **«Войти»** (или **«Добавить по IP»** с `IP:27015`).

Всем нужна 0.1.9.3: более старая версия отклоняется с сообщением, где названы обе версии. Настройки сохраняются; удаление: снова `OTH-LAN-Setup.exe` → **«Удалить»**.

---

## Updating

From 0.1.8.1 / 0.1.9 / 0.1.9.1 / 0.1.9.2 the main menu offers new versions itself: **Install / Later / Skip this version**. If the check cannot reach GitHub, a notice with **Retry / Later** appears. The download is checked against `SHA256SUMS.txt` of the same release and is copied in after you quit the game. Switch it off in F7 ("Check for updates"). From 0.1.8 or older, install 0.1.9.3 by hand once (steps above).

## Обновление

Начиная с 0.1.8.1 / 0.1.9 / 0.1.9.1 / 0.1.9.2 главное меню само предлагает новые версии: **«Установить» / «Позже» / «Пропустить эту версию»**. Если GitHub недоступен, появляется уведомление **«Повторить» / «Позже»**. Скачанный файл сверяется с `SHA256SUMS.txt` того же релиза и копируется после выхода из игры. Выключается в F7 («Проверять обновления»). С 0.1.8 и более ранних версий установите 0.1.9.3 вручную один раз (шаги выше).

---

## What's new in 0.1.9.3

- Before asking Modio, set the main-menu runner's `AllowMods` so `RequestTempMod` is not silently ignored (the mod gate runs before the LAN session starts).
- Stop re-starting temp installs on a timer — that could cancel an in-progress Modio download. Wait + refresh instead; use **Join without mods** if you need to skip.

## Что нового в 0.1.9.3

- Перед запросом к Modio на menu-runner включается `AllowMods`, чтобы `RequestTempMod` не молчал (гейт модов раньше старта LAN-сессии).
- Больше не перезапускаем temp-установку по таймеру — это могло оборвать уже идущее скачивание. Ждём + Refresh; если нужно — **Войти без модов**.

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


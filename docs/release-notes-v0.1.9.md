## About

**Unofficial** LAN / virtual-LAN co-op mod for Over The Hill. Not affiliated with, endorsed by, or supported by Funselektor Labs or Exit Games (Photon). Contains no game files: every player needs their own copy of the game from Steam.

## О моде

**Неофициальный** мод для кооператива в Over The Hill по локальной или виртуальной сети. Не связан с Funselektor Labs и Exit Games (Photon), не одобрен и не поддерживается ими. Файлов игры нет: каждому игроку нужна своя копия игры из Steam.

---

## Download

Under **Assets** below, click **`OTH-LAN-0.1.9.zip`** (not "Source code").

| File | What it is | Size | SHA-256 |
|---|---|---|---|
| `OTH-LAN-0.1.9.zip` | Installer (recommended) | *(fill at package)* | *(fill)* |
| `OTH-LAN-0.1.9-manual.zip` | Manual install without the installer | *(fill)* | *(fill)* |

Plugin `OthLan.dll` sha256 *(fill after public build)*. All checksums are also in `SHA256SUMS.txt`. Optional asset `6000.0.75.zip` is the offline Unity base libraries pack used by `OTH-LAN-Launch.exe` if BepInEx hangs on first download.

## Скачать

В блоке **Assets** ниже нажмите **`OTH-LAN-0.1.9.zip`** (не «Source code»).

| Файл | Что это | Размер | SHA-256 |
|---|---|---|---|
| `OTH-LAN-0.1.9.zip` | Установщик (рекомендуется) | *(заполнить при сборке)* | *(заполнить)* |
| `OTH-LAN-0.1.9-manual.zip` | Ручная установка без установщика | *(заполнить)* | *(заполнить)* |

Плагин `OthLan.dll` sha256 *(заполнить после публичной сборки)*. Все контрольные суммы есть также в `SHA256SUMS.txt`. Опциональный файл `6000.0.75.zip` — офлайн-пакет Unity base libraries для `OTH-LAN-Launch.exe`, если BepInEx зависает при первой загрузке.

---

## Install

1. Unpack `OTH-LAN-0.1.9.zip` (right-click → *Extract All…*).
2. Close the game, run `OTH-LAN-Setup.exe`, press **Install** (the installer is not code-signed: on "Windows protected your PC" click *More info* → *Run anyway*).
3. Start Over The Hill from Steam (the first start takes 1–3 minutes longer and needs internet once). If that first start hangs on downloading Unity libraries, run `OTH-LAN-Launch.exe` from the same folder instead (it fetches the offline pack and restarts).
4. Everyone joins the same LAN or virtual LAN (Radmin VPN, Hamachi, ZeroTier). Host: **Main menu → Local Co-op → Become host**. Others: **Main menu → Local Co-op**, pick the host, **Join** (or **Add by IP** with `IP:27015`).

All players need 0.1.9: an older version is refused with a message that names both versions. Settings are kept; Uninstall: run `OTH-LAN-Setup.exe` again → **Uninstall**.

## Установка

1. Распакуйте `OTH-LAN-0.1.9.zip` (правой кнопкой → «Извлечь всё…»).
2. Закройте игру, запустите `OTH-LAN-Setup.exe`, нажмите **«Установить»** (установщик не подписан: на «Система Windows защитила ваш компьютер» нажмите «Подробнее» → «Выполнить в любом случае»).
3. Запустите Over The Hill через Steam (первый запуск дольше на 1–3 минуты и один раз требует интернет). Если первый запуск зависает на скачивании Unity libraries, вместо этого запустите `OTH-LAN-Launch.exe` из той же папки (он скачает офлайн-пакет и перезапустит игру).
4. Все подключаются к одной локальной или виртуальной сети (Radmin VPN, Hamachi, ZeroTier). Хост: **главное меню → «Локальный кооп» → «Стать хостом»**. Остальные: **главное меню → «Локальный кооп»**, выберите хост, **«Войти»** (или **«Добавить по IP»** с `IP:27015`).

Всем нужна 0.1.9: более старая версия отклоняется с сообщением, где названы обе версии. Настройки сохраняются; удаление: снова `OTH-LAN-Setup.exe` → **«Удалить»**.

---

## Updating

From 0.1.8.1 the main menu offers new versions itself: **Install / Later / Skip this version**. The download is checked against `SHA256SUMS.txt` of the same release and is copied in after you quit the game. Switch it off in F7 ("Check for updates"). From 0.1.8 or older, install 0.1.9 by hand once (steps above).

## Обновление

Начиная с 0.1.8.1 главное меню само предлагает новые версии: **«Установить» / «Позже» / «Пропустить эту версию»**. Скачанный файл сверяется с `SHA256SUMS.txt` того же релиза и копируется после выхода из игры. Выключается в F7 («Проверять обновления»). С 0.1.8 и более ранних версий установите 0.1.9 вручную один раз (шаги выше).

---

## What's new in 0.1.9

- Other players' headlights, brake/reverse lights and wheel spin/steer work again when remote interpolation is on (wheel speed from networked state / motion, not a frozen kinematic rigidbody). Host Modio liveries re-apply on clients.
- Pre-join: if the host uses Modio liveries you lack, a dialog lists them with **Download** / **Cancel**; Download subscribes via the game's Modio path.
- `OTH-LAN-Launch.exe`: if BepInEx hangs more than 10 s downloading Unity base libraries, fetches `6000.0.75.zip` from this release into `BepInEx/unity-libs/` and restarts.
- F7 (client): live toggles for remote interpolation, own-car freeze smoothing, and export diagnostics to the Desktop. F7 no longer auto-opens when joining a host.
- Own-car local clock is cheaper; it pauses during car-to-car contact or winching.
- Remote cars keep moving visually through short host snapshot gaps instead of freezing then jumping.
- Host pose playback (jitter buffer + Hermite) on by default; client pose stream default rate raised (SendHz 20 → 30).
- Version is three-part `0.1.9` so both 0.1.8 and 0.1.8.1 updaters can see it.

## Что нового в 0.1.9

- Снова работают фары, стоп/задний ход и вращение колёс чужих машин при включённой интерполяции (скорость колёс из сетевого состояния / движения, не из замороженного kinematic rigidbody). Мод-раскраска хоста снова применяется на клиентах.
- Перед входом: если у хоста Modio-раскраски, которых у вас нет — диалог со списком и кнопками **Скачать** / **Отмена**; Скачать подписывает через игровой путь Modio.
- `OTH-LAN-Launch.exe`: если BepInEx больше 10 с висит на скачивании Unity base libraries, качает `6000.0.75.zip` из этого релиза в `BepInEx/unity-libs/` и перезапускает игру.
- F7 (клиент): живые переключатели интерполяции чужих, сглаживания заморозки своей машины и экспорт диагностики на рабочий стол. F7 больше не открывается само при входе к хосту.
- Локальные часы своей машины дешевле; на паузе при контакте машин или лебёдке.
- Чужие машины визуально продолжают ехать через короткие разрывы снапшотов хоста вместо «замри и прыгни».
- На хосте по умолчанию playback поз (jitter + Hermite); поток поз клиента чаще (SendHz 20 → 30).
- Версия из трёх чисел `0.1.9`, чтобы её видели обновлялки и 0.1.8, и 0.1.8.1.

---

## Known limits

- Tested mainly on one PC with simulated ping and loss; real multi-PC sessions over the internet may differ.
- Collisions with other players' cars are approximate; towing between two players stays host-driven.
- Mod livery download needs a working Modio login in the game; both players need the same Workshop/Modio mods subscribed for textures to apply.
- What is still to be tested and finished is in the [roadmap](https://github.com/Sergaris/over-the-hill-lan-coop/blob/main/docs/ROADMAP.md).

## Известные ограничения

- Проверено в основном на одном ПК с имитацией пинга и потерь; реальные сессии на нескольких ПК через интернет могут отличаться.
- Столкновения с машинами других игроков приблизительные; буксировка между двумя игроками остаётся под управлением хоста.
- Скачивание мод-раскраски требует входа в Modio в игре; у обоих игроков должны быть подписаны одни и те же Workshop/Modio моды, иначе текстурам не из чего примениться.
- Что ещё нужно проверить и доделать, описано в [плане развития](https://github.com/Sergaris/over-the-hill-lan-coop/blob/main/docs/ROADMAP.md).

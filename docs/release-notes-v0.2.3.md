## About

**Unofficial** LAN / virtual-LAN co-op mod for Over The Hill. Not affiliated with, endorsed by, or supported by Funselektor Labs or Exit Games (Photon). Contains no game files: every player needs their own copy of the game from Steam.

## О моде

**Неофициальный** мод для кооператива в Over The Hill по локальной или виртуальной сети. Не связан с Funselektor Labs и Exit Games (Photon), не одобрен и не поддерживается ими. Файлов игры нет: каждому игроку нужна своя копия игры из Steam.

---

## Download

Under **Assets** below, click **`OTH-LAN-0.2.3.zip`** (not "Source code").

| File | What it is | Size | SHA-256 |
|---|---|---|---|
| `OTH-LAN-0.2.3.zip` | Installer (recommended) | 130246146 B | `96f956c12f7c3b58bfeb8aa12dc43f91a6dc656724a0d9817b955cfae09f7b9c` |
| `OTH-LAN-0.2.3-manual.zip` | Manual install without the installer | 34582479 B | `6a781519354c17d5f27760e565ab5c15a0efb036a5e713ad78b397e9bfc4227f` |

Plugin `OthLan.dll` sha256 `32fec2d45439f326d56a7842d957c6dccaf2e0ca746179e2dc44ba31312324d5`. All checksums are also in `SHA256SUMS.txt`. Optional asset `6000.0.75.zip` is the offline Unity base libraries pack used by `OTH-LAN-Launch.exe` if BepInEx hangs on first download.

## Скачать

В блоке **Assets** ниже нажмите **`OTH-LAN-0.2.3.zip`** (не «Source code»).

| Файл | Что это | Размер | SHA-256 |
|---|---|---|---|
| `OTH-LAN-0.2.3.zip` | Установщик (рекомендуется) | 130246146 Б | `96f956c12f7c3b58bfeb8aa12dc43f91a6dc656724a0d9817b955cfae09f7b9c` |
| `OTH-LAN-0.2.3-manual.zip` | Ручная установка без установщика | 34582479 Б | `6a781519354c17d5f27760e565ab5c15a0efb036a5e713ad78b397e9bfc4227f` |

Плагин `OthLan.dll` sha256 `32fec2d45439f326d56a7842d957c6dccaf2e0ca746179e2dc44ba31312324d5`. Все контрольные суммы есть также в `SHA256SUMS.txt`. Опциональный файл `6000.0.75.zip` — офлайн-пакет Unity base libraries для `OTH-LAN-Launch.exe`, если BepInEx зависает при первой загрузке.

---

## Install

1. Unpack `OTH-LAN-0.2.3.zip` (right-click → *Extract All…*).
2. Close the game, run `OTH-LAN-Setup.exe`, press **Install** (the installer is not code-signed: on "Windows protected your PC" click *More info* → *Run anyway*).
3. Start Over The Hill from Steam (the first start takes 1–3 minutes longer and needs internet once). If that first start hangs on downloading Unity libraries, run `OTH-LAN-Launch.exe` from the same folder instead (it fetches the offline pack and restarts).
4. Everyone joins the same LAN or virtual LAN (Radmin VPN, Hamachi, ZeroTier). Host: **Main menu → Local Co-op → Become host**. Others: **Main menu → Local Co-op**, pick the host, **Join** (or **Add by IP** with `IP:27015`).

All players need 0.2.3: an older version is refused with a message that names both versions. Settings are kept; Uninstall: run `OTH-LAN-Setup.exe` again → **Uninstall**.

## Установка

1. Распакуйте `OTH-LAN-0.2.3.zip` (правой кнопкой → «Извлечь всё…»).
2. Закройте игру, запустите `OTH-LAN-Setup.exe`, нажмите **«Установить»** (установщик не подписан: на «Система Windows защитила ваш компьютер» нажмите «Подробнее» → «Выполнить в любом случае»).
3. Запустите Over The Hill через Steam (первый запуск дольше на 1–3 минуты и один раз требует интернет). Если первый запуск зависает на скачивании Unity libraries, вместо этого запустите `OTH-LAN-Launch.exe` из той же папки (он скачает офлайн-пакет и перезапустит игру).
4. Все подключаются к одной локальной или виртуальной сети (Radmin VPN, Hamachi, ZeroTier). Хост: **главное меню → «Локальный кооп» → «Стать хостом»**. Остальные: **главное меню → «Локальный кооп»**, выберите хост, **«Войти»** (или **«Добавить по IP»** с `IP:27015`).

Всем нужна 0.2.3: более старая версия отклоняется с сообщением, где названы обе версии. Настройки сохраняются; удаление: снова `OTH-LAN-Setup.exe` → **«Удалить»**.

---

## Updating

From 0.1.8.1 / 0.1.9 / 0.1.9.1 / 0.1.9.2 / 0.1.9.3 / 0.2.0 / 0.2.1 / 0.2.2 the main menu offers new versions itself: **Install / Later / Skip this version**. If the check cannot reach GitHub, a notice with **Retry / Later** appears. The download is checked against `SHA256SUMS.txt` of the same release and is copied in after you quit the game. Switch it off in F7 ("Check for updates"). From 0.1.8 or older, install 0.2.3 by hand once (steps above).

## Обновление

Начиная с 0.1.8.1 / 0.1.9 / 0.1.9.1 / 0.1.9.2 / 0.1.9.3 / 0.2.0 / 0.2.1 / 0.2.2 главное меню само предлагает новые версии: **«Установить» / «Позже» / «Пропустить эту версию»**. Если GitHub недоступен, появляется уведомление **«Повторить» / «Позже»**. Скачанный файл сверяется с `SHA256SUMS.txt` того же релиза и копируется после выхода из игры. Выключается в F7 («Проверять обновления»). С 0.1.8 и более ранних версий установите 0.2.3 вручную один раз (шаги выше).

---

## What's new in 0.2.3

- Mod missing-gate: Modio display names when known, per-row state (needed / queued / downloading N% / installed / error), **Retry** after auth or download timeout.
- Session toasts (host left, link lost/restored, player join/leave) via SessionNotify + JoinToast.
- Update prompt on Canvas (UiModal / UiNav); main-menu focus restored on close.
- All `Now` call sites use NetClock (no bare Time proxies).
- More LogicTests: updater 0.2.3 parse/skip/sums, OTPL sanitize, OTC3 version/quat, StatePlayback old-epoch, InteractionLogic NaN RTT.

## Что нового в 0.2.3

- Окно недостающих модов: имена Modio при наличии, состояние строки (нужен / в очереди / качается N% / установлен / ошибка), **«Повторить»** после таймаута входа или скачивания.
- Тосты сессии (хост вышел, связь потеряна/восстановлена, игрок вошёл/вышел) через SessionNotify + JoinToast.
- Промпт обновления на Canvas (UiModal / UiNav); фокус главного меню восстанавливается при закрытии.
- Все вызовы `Now` идут через NetClock (без голых прокси Time).
- Больше LogicTests: разбор/skip/sums апдейтера 0.2.3, санитизация OTPL, версия/кват OTC3, старый epoch StatePlayback, NaN RTT в InteractionLogic.

---

## Known limits

- Live two-PC verification of this build is pending (owner away). Report issues with F7 → Save diagnostics.
- OTP2 parallel path not removed yet (T3.8 after live confirmation).

## Известные ограничения

- Живая проверка на двух ПК для этой сборки ещё впереди (владелец был не у компьютера). О проблемах — F7 → сохранить диагностику.
- Параллельный путь OTP2 ещё не снят (T3.8 после live-подтверждения).


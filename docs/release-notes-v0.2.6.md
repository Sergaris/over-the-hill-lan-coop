## About

**Unofficial** LAN / virtual-LAN co-op mod for Over The Hill. Not affiliated with, endorsed by, or supported by Funselektor Labs or Exit Games (Photon). Contains no game files: every player needs their own copy of the game from Steam.

## О моде

**Неофициальный** мод для кооператива в Over The Hill по локальной или виртуальной сети. Не связан с Funselektor Labs и Exit Games (Photon), не одобрен и не поддерживается ими. Файлов игры нет: каждому игроку нужна своя копия игры из Steam.

---

## Download

Under **Assets** below, click **`OTH-LAN-0.2.6.zip`** (not "Source code").

| File | What it is | Size | SHA-256 |
|---|---|---|---|
| `OTH-LAN-0.2.6.zip` | Installer (recommended) | 130255790 B | `296668f5b7e003b5273336812879c7303bbf7bbd397be1d619a7e15228e99046` |
| `OTH-LAN-0.2.6-manual.zip` | Manual install without the installer | 34592384 B | `346b5637fb1cff0e35ed070cb2dedbbc4eb5eaef9049075386adde859ff68d5b` |

Plugin `OthLan.dll` sha256 `f1c8ee24012d6c34a8d2e43b06d8ea42f87afef9c6a66969b878b0471a2fd42a`. All checksums are also in `SHA256SUMS.txt`. Optional asset `6000.0.75.zip` is the offline Unity base libraries pack used by `OTH-LAN-Launch.exe` if BepInEx hangs on first download.

## Скачать

В блоке **Assets** ниже нажмите **`OTH-LAN-0.2.6.zip`** (не «Source code»).

| Файл | Что это | Размер | SHA-256 |
|---|---|---|---|
| `OTH-LAN-0.2.6.zip` | Установщик (рекомендуется) | 130255790 Б | `296668f5b7e003b5273336812879c7303bbf7bbd397be1d619a7e15228e99046` |
| `OTH-LAN-0.2.6-manual.zip` | Ручная установка без установщика | 34592384 Б | `346b5637fb1cff0e35ed070cb2dedbbc4eb5eaef9049075386adde859ff68d5b` |

Плагин `OthLan.dll` sha256 `f1c8ee24012d6c34a8d2e43b06d8ea42f87afef9c6a66969b878b0471a2fd42a`. Все контрольные суммы есть также в `SHA256SUMS.txt`. Опциональный файл `6000.0.75.zip` — офлайн-пакет Unity base libraries для `OTH-LAN-Launch.exe`, если BepInEx зависает при первой загрузке.

---

## Install

1. Unpack `OTH-LAN-0.2.6.zip` (right-click → *Extract All…*).
2. Close the game, run `OTH-LAN-Setup.exe`, press **Install** (the installer is not code-signed: on "Windows protected your PC" click *More info* → *Run anyway*).
3. Start Over The Hill from Steam (the first start takes 1–3 minutes longer and needs internet once). If that first start hangs on downloading Unity libraries, run `OTH-LAN-Launch.exe` from the same folder instead (it fetches the offline pack and restarts).
4. Everyone joins the same LAN or virtual LAN (Radmin VPN, Hamachi, ZeroTier). Host: **Main menu → Local Co-op → Become host**. Others: **Main menu → Local Co-op**, pick the host, **Join** (or **Add by IP** with `IP:27015`).

All players need 0.2.6: an older version is refused with a message that names both versions. Settings are kept; Uninstall: run `OTH-LAN-Setup.exe` again → **Uninstall**.

## Установка

1. Распакуйте `OTH-LAN-0.2.6.zip` (правой кнопкой → «Извлечь всё…»).
2. Закройте игру, запустите `OTH-LAN-Setup.exe`, нажмите **«Установить»** (установщик не подписан: на «Система Windows защитила ваш компьютер» нажмите «Подробнее» → «Выполнить в любом случае»).
3. Запустите Over The Hill через Steam (первый запуск дольше на 1–3 минуты и один раз требует интернет). Если первый запуск зависает на скачивании Unity libraries, вместо этого запустите `OTH-LAN-Launch.exe` из той же папки (он скачает офлайн-пакет и перезапустит игру).
4. Все подключаются к одной локальной или виртуальной сети (Radmin VPN, Hamachi, ZeroTier). Хост: **главное меню → «Локальный кооп» → «Стать хостом»**. Остальные: **главное меню → «Локальный кооп»**, выберите хост, **«Войти»** (или **«Добавить по IP»** с `IP:27015`).

Всем нужна 0.2.6: более старая версия отклоняется с сообщением, где названы обе версии. Настройки сохраняются; удаление: снова `OTH-LAN-Setup.exe` → **«Удалить»**.

---

## Updating

From 0.1.8.1 / 0.1.9 / 0.1.9.1 / 0.1.9.2 / 0.1.9.3 / 0.2.0 / 0.2.1 / 0.2.2 / 0.2.3 / 0.2.4 / 0.2.5 / 0.2.5.1 / 0.2.5.2 / 0.2.5.3 / 0.2.5.4 the main menu offers new versions itself: **Install / Later / Skip this version**. If the check cannot reach GitHub, a notice with **Retry / Later** appears. The download is checked against `SHA256SUMS.txt` of the same release and is copied in after you quit the game. Switch it off in F7 ("Check for updates"). From 0.1.8 or older, install 0.2.6 by hand once (steps above).

## Обновление

Начиная с 0.1.8.1 / 0.1.9 / 0.1.9.1 / 0.1.9.2 / 0.1.9.3 / 0.2.0 / 0.2.1 / 0.2.2 / 0.2.3 / 0.2.4 / 0.2.5 / 0.2.5.1 / 0.2.5.2 / 0.2.5.3 / 0.2.5.4 главное меню само предлагает новые версии: **«Установить» / «Позже» / «Пропустить эту версию»**. Если GitHub недоступен, появляется уведомление **«Повторить» / «Позже»**. Скачанный файл сверяется с `SHA256SUMS.txt` того же релиза и копируется после выхода из игры. Выключается в F7 («Проверять обновления»). С 0.1.8 и более ранних версий установите 0.2.6 вручную один раз (шаги выше).

---

## What's new in 0.2.6

- Client own-car hitch after a prediction reset: a cheap skip still restores NetworkRigidbody/VPP Previous and resets the correction offset, so the own car no longer draws a host-old→resim jump when the gap was within noise (the F7 «own car local clock» toggle does not affect this path).

## Что нового в 0.2.6

- Статтер своей машины у клиента после prediction reset: на cheap skip всё равно восстанавливаются Previous NRB/VPP и сбрасывается correction offset — машина больше не рисует прыжок host-old→resim при ошибке в шуме (переключатель F7 «своя машина на локальных часах» этот путь не меняет).

---

## Known limits

- Tested on one PC with two game instances and simulated lag and packet loss. Report issues with F7 → Save diagnostics.
- A frozen abandoned car does not react to things other than player cars (for example a falling meteorite) until a player comes within winch reach. Switch it off with `ParkIdleCars = false` in `[Netcode]` of `BepInEx/config/dev.othlan.lan.cfg` on the host.
- Third-party copies still need the same Fusion 2.1.2 and matching data layout; a broken or very old crack can still be refused as incompatible. Both sides must run 0.2.6 (older hosts do not advertise Fusion for cross-hash join).

## Известные ограничения

- Проверено на одном ПК с двумя копиями игры и имитацией лага и потери пакетов. О проблемах — F7 → сохранить диагностику.
- Замороженная брошенная машина не реагирует ни на что, кроме машин игроков (например, на упавший метеорит), пока игрок не окажется в радиусе лебёдки. Отключается на хосте: `ParkIdleCars = false` в `[Netcode]` файла `BepInEx/config/dev.othlan.lan.cfg`.
- Сторонней копии всё равно нужны Fusion 2.1.2 и совпадающая раскладка данных; сильно сломанный или старый репак по-прежнему может быть incompatible. С обеих сторон нужна 0.2.6 (старые хосты не сообщают Fusion для join с другим hash).

## About

**Unofficial** LAN / virtual-LAN co-op mod for Over The Hill. Not affiliated with, endorsed by, or supported by Funselektor Labs or Exit Games (Photon). Contains no game files: every player needs their own copy of the game from Steam.

## О моде

**Неофициальный** мод для кооператива в Over The Hill по локальной или виртуальной сети. Не связан с Funselektor Labs и Exit Games (Photon), не одобрен и не поддерживается ими. Файлов игры нет: каждому игроку нужна своя копия игры из Steam.

---

## Download

Under **Assets** below, click **`OTH-LAN-0.2.7.zip`** (not "Source code").

| File | What it is | Size | SHA-256 |
|---|---|---|---|
| `OTH-LAN-0.2.7.zip` | Installer (recommended) | 130269186 B | `20fc18af761b66040c63dbeeb2894fb40d9a89127334baa3175ae5e6a249df2c` |
| `OTH-LAN-0.2.7-manual.zip` | Manual install without the installer | 34603407 B | `d8c69277e9271d070fecfef89c3535ea3395d963b2d246138fab7b55a1969867` |

Plugin `OthLan.dll` sha256 `b4270f79f38b5f18b46b1eee9c76d4505cd3349b815f778493bb473bf3ff59e5`. All checksums are also in `SHA256SUMS.txt`. Optional asset `6000.0.75.zip` (`53c15d574b129486f71f93825677983787e7242aed98d083d3db93b87a985655`) is the offline Unity base libraries pack used by `OTH-LAN-Launch.exe` if BepInEx hangs on first download.

## Скачать

В блоке **Assets** ниже нажмите **`OTH-LAN-0.2.7.zip`** (не «Source code»).

| Файл | Что это | Размер | SHA-256 |
|---|---|---|---|
| `OTH-LAN-0.2.7.zip` | Установщик (рекомендуется) | 130269186 Б | `20fc18af761b66040c63dbeeb2894fb40d9a89127334baa3175ae5e6a249df2c` |
| `OTH-LAN-0.2.7-manual.zip` | Ручная установка без установщика | 34603407 Б | `d8c69277e9271d070fecfef89c3535ea3395d963b2d246138fab7b55a1969867` |

Плагин `OthLan.dll` sha256 `b4270f79f38b5f18b46b1eee9c76d4505cd3349b815f778493bb473bf3ff59e5`. Все контрольные суммы есть также в `SHA256SUMS.txt`. Опциональный файл `6000.0.75.zip` (`53c15d574b129486f71f93825677983787e7242aed98d083d3db93b87a985655`) — офлайн-пакет Unity base libraries для `OTH-LAN-Launch.exe`, если BepInEx зависает при первой загрузке.

---

## Install

1. Unpack `OTH-LAN-0.2.7.zip` (right-click → *Extract All…*).
2. Close the game, run `OTH-LAN-Setup.exe`, press **Install** (the installer is not code-signed: on "Windows protected your PC" click *More info* → *Run anyway*).
3. Start Over The Hill from Steam (the first start takes 1–3 minutes longer and needs internet once). If that first start hangs on downloading Unity libraries, run `OTH-LAN-Launch.exe` from the same folder instead (it fetches the offline pack and restarts).
4. Everyone joins the same LAN or virtual LAN (Radmin VPN, Hamachi, ZeroTier). Host: **Main menu → Local Co-op → Become host**. Others: **Main menu → Local Co-op**, pick the host, **Join** (or **Add by IP** with `IP:27015`).

All players need 0.2.7: an older version is refused with a message that names both versions. Settings are kept; Uninstall: run `OTH-LAN-Setup.exe` again → **Uninstall**.

## Установка

1. Распакуйте `OTH-LAN-0.2.7.zip` (правой кнопкой → «Извлечь всё…»).
2. Закройте игру, запустите `OTH-LAN-Setup.exe`, нажмите **«Установить»** (установщик не подписан: на «Система Windows защитила ваш компьютер» нажмите «Подробнее» → «Выполнить в любом случае»).
3. Запустите Over The Hill через Steam (первый запуск дольше на 1–3 минуты и один раз требует интернет). Если первый запуск зависает на скачивании Unity libraries, вместо этого запустите `OTH-LAN-Launch.exe` из той же папки (он скачает офлайн-пакет и перезапустит игру).
4. Все подключаются к одной локальной или виртуальной сети (Radmin VPN, Hamachi, ZeroTier). Хост: **главное меню → «Локальный кооп» → «Стать хостом»**. Остальные: **главное меню → «Локальный кооп»**, выберите хост, **«Войти»** (или **«Добавить по IP»** с `IP:27015`).

Всем нужна 0.2.7: более старая версия отклоняется с сообщением, где названы обе версии. Настройки сохраняются; удаление: снова `OTH-LAN-Setup.exe` → **«Удалить»**.

---

## Updating

From 0.1.8.1 / 0.1.9 / 0.1.9.1 / 0.1.9.2 / 0.1.9.3 / 0.2.0 / 0.2.1 / 0.2.2 / 0.2.3 / 0.2.4 / 0.2.5 / 0.2.5.1 / 0.2.5.2 / 0.2.5.3 / 0.2.5.4 / 0.2.6 / 0.2.6.1 / 0.2.6.2 / 0.2.6.3 the main menu offers new versions itself: **Install / Later / Skip this version**. If the check cannot reach GitHub, a notice with **Retry / Later** appears. The download is checked against `SHA256SUMS.txt` of the same release and is copied in after you quit the game. Switch it off in F7 ("Check for updates"). From 0.1.8 or older, install 0.2.7 by hand once (steps above).

## Обновление

Начиная с 0.1.8.1 / 0.1.9 / 0.1.9.1 / 0.1.9.2 / 0.1.9.3 / 0.2.0 / 0.2.1 / 0.2.2 / 0.2.3 / 0.2.4 / 0.2.5 / 0.2.5.1 / 0.2.5.2 / 0.2.5.3 / 0.2.5.4 / 0.2.6 / 0.2.6.1 / 0.2.6.2 / 0.2.6.3 главное меню само предлагает новые версии: **«Установить» / «Позже» / «Пропустить эту версию»**. Если GitHub недоступен, появляется уведомление **«Повторить» / «Позже»**. Скачанный файл сверяется с `SHA256SUMS.txt` того же релиза и копируется после выхода из игры. Выключается в F7 («Проверять обновления»). С 0.1.8 и более ранних версий установите 0.2.7 вручную один раз (шаги выше).

---

## What's new in 0.2.7

- Higher FPS for players who join at high ping: after every update from the host, the game no longer recalculates the physics of your own car (that result was thrown away anyway). This work is now about 35–40% cheaper for the CPU.
- No more camera shake: the camera now moves in step with your car, so the car no longer twitches on screen even when FPS is fine.

## Что нового в 0.2.7

- Выше FPS у тех, кто подключается с высоким пингом: после каждого обновления от хоста игра больше не пересчитывает заново физику вашей машины (этот результат всё равно выбрасывался). Нагрузка на процессор от этой работы стала примерно на 35–40% меньше.
- Камера больше не дёргается: она двигается вместе с машиной, и машина не подрагивает на экране даже при нормальном FPS.

---

## Known limits

- Tested in a real two-player session over a network (works well), and with two game instances on one PC plus simulated lag and packet loss. Report issues with F7 → Save diagnostics.
- Any accessories hung on a car (a coiled rope, decorative lamp, antenna, and the like) jitter heavily when viewed as a client. They look fine from the host.
- If after a lag spike your car bounces or stops reacting to steering, set `OwnResimSkip = false` in `[Netcode]` of `BepInEx/config/dev.othlan.lan.cfg` and restart the game; please send diagnostics too.
- A frozen abandoned car does not react to things other than player cars (for example a falling meteorite) until a player comes within winch reach. Switch it off with `ParkIdleCars = false` in `[Netcode]` of `BepInEx/config/dev.othlan.lan.cfg` on the host.
- Third-party copies still need the same Fusion 2.1.2 and matching data layout; a broken or very old crack can still be refused as incompatible. Both sides must run 0.2.7 (older hosts do not advertise Fusion for cross-hash join).

## Известные ограничения

- Проверено в настоящей сессии двух игроков по сети (работает хорошо), а также двумя копиями игры на одном ПК с имитацией лага и потери пакетов. О проблемах — F7 → сохранить диагностику.
- Любые аксессуары, которые вешаются на машину (скрученная верёвка, декоративная лампа, антенна и тому подобное), сильно дёргаются, если смотреть от лица клиента. От лица хоста всё выглядит нормально.
- Если после скачка пинга ваша машина подпрыгивает или перестаёт слушаться руля, поставьте `OwnResimSkip = false` в `[Netcode]` файла `BepInEx/config/dev.othlan.lan.cfg` и перезапустите игру; пришлите, пожалуйста, и диагностику.
- Замороженная брошенная машина не реагирует ни на что, кроме машин игроков (например, на упавший метеорит), пока игрок не окажется в радиусе лебёдки. Отключается на хосте: `ParkIdleCars = false` в `[Netcode]` файла `BepInEx/config/dev.othlan.lan.cfg`.
- Сторонней копии всё равно нужны Fusion 2.1.2 и совпадающая раскладка данных; сильно сломанный или старый репак по-прежнему может быть incompatible. С обеих сторон нужна 0.2.7 (старые хосты не сообщают Fusion для join с другим hash).

## About

**Unofficial** LAN / virtual-LAN co-op mod for Over The Hill. Not affiliated with, endorsed by, or supported by Funselektor Labs or Exit Games (Photon). Contains no game files: every player needs their own copy of the game from Steam.

## О моде

**Неофициальный** мод для кооператива в Over The Hill по локальной или виртуальной сети. Не связан с Funselektor Labs и Exit Games (Photon), не одобрен и не поддерживается ими. Файлов игры нет: каждому игроку нужна своя копия игры из Steam.

---

## Download

Under **Assets** below, click **`OTH-LAN-0.2.4.zip`** (not "Source code").

| File | What it is | Size | SHA-256 |
|---|---|---|---|
| `OTH-LAN-0.2.4.zip` | Installer (recommended) | *(fill at package)* | *(fill)* |
| `OTH-LAN-0.2.4-manual.zip` | Manual install without the installer | *(fill at package)* | *(fill)* |

Plugin `OthLan.dll` sha256 *(fill after public build)*. All checksums are also in `SHA256SUMS.txt`. Optional asset `6000.0.75.zip` is the offline Unity base libraries pack used by `OTH-LAN-Launch.exe` if BepInEx hangs on first download.

## Скачать

В блоке **Assets** ниже нажмите **`OTH-LAN-0.2.4.zip`** (не «Source code»).

| Файл | Что это | Размер | SHA-256 |
|---|---|---|---|
| `OTH-LAN-0.2.4.zip` | Установщик (рекомендуется) | *(заполнить при сборке)* | *(заполнить)* |
| `OTH-LAN-0.2.4-manual.zip` | Ручная установка без установщика | *(заполнить при сборке)* | *(заполнить)* |

Плагин `OthLan.dll` sha256 *(заполнить после публичной сборки)*. Все контрольные суммы есть также в `SHA256SUMS.txt`. Опциональный файл `6000.0.75.zip` — офлайн-пакет Unity base libraries для `OTH-LAN-Launch.exe`, если BepInEx зависает при первой загрузке.

---

## Install

1. Unpack `OTH-LAN-0.2.4.zip` (right-click → *Extract All…*).
2. Close the game, run `OTH-LAN-Setup.exe`, press **Install** (the installer is not code-signed: on "Windows protected your PC" click *More info* → *Run anyway*).
3. Start Over The Hill from Steam (the first start takes 1–3 minutes longer and needs internet once). If that first start hangs on downloading Unity libraries, run `OTH-LAN-Launch.exe` from the same folder instead (it fetches the offline pack and restarts).
4. Everyone joins the same LAN or virtual LAN (Radmin VPN, Hamachi, ZeroTier). Host: **Main menu → Local Co-op → Become host**. Others: **Main menu → Local Co-op**, pick the host, **Join** (or **Add by IP** with `IP:27015`).

All players need 0.2.4: an older version is refused with a message that names both versions. Settings are kept; Uninstall: run `OTH-LAN-Setup.exe` again → **Uninstall**.

## Установка

1. Распакуйте `OTH-LAN-0.2.4.zip` (правой кнопкой → «Извлечь всё…»).
2. Закройте игру, запустите `OTH-LAN-Setup.exe`, нажмите **«Установить»** (установщик не подписан: на «Система Windows защитила ваш компьютер» нажмите «Подробнее» → «Выполнить в любом случае»).
3. Запустите Over The Hill через Steam (первый запуск дольше на 1–3 минуты и один раз требует интернет). Если первый запуск зависает на скачивании Unity libraries, вместо этого запустите `OTH-LAN-Launch.exe` из той же папки (он скачает офлайн-пакет и перезапустит игру).
4. Все подключаются к одной локальной или виртуальной сети (Radmin VPN, Hamachi, ZeroTier). Хост: **главное меню → «Локальный кооп» → «Стать хостом»**. Остальные: **главное меню → «Локальный кооп»**, выберите хост, **«Войти»** (или **«Добавить по IP»** с `IP:27015`).

Всем нужна 0.2.4: более старая версия отклоняется с сообщением, где названы обе версии. Настройки сохраняются; удаление: снова `OTH-LAN-Setup.exe` → **«Удалить»**.

---

## Updating

From 0.1.8.1 / 0.1.9 / 0.1.9.1 / 0.1.9.2 / 0.1.9.3 / 0.2.0 / 0.2.1 / 0.2.2 / 0.2.3 the main menu offers new versions itself: **Install / Later / Skip this version**. If the check cannot reach GitHub, a notice with **Retry / Later** appears. The download is checked against `SHA256SUMS.txt` of the same release and is copied in after you quit the game. Switch it off in F7 ("Check for updates"). From 0.1.8 or older, install 0.2.4 by hand once (steps above).

## Обновление

Начиная с 0.1.8.1 / 0.1.9 / 0.1.9.1 / 0.1.9.2 / 0.1.9.3 / 0.2.0 / 0.2.1 / 0.2.2 / 0.2.3 главное меню само предлагает новые версии: **«Установить» / «Позже» / «Пропустить эту версию»**. Если GitHub недоступен, появляется уведомление **«Повторить» / «Позже»**. Скачанный файл сверяется с `SHA256SUMS.txt` того же релиза и копируется после выхода из игры. Выключается в F7 («Проверять обновления»). С 0.1.8 и более ранних версий установите 0.2.4 вручную один раз (шаги выше).

---

## What's new in 0.2.4

- Steering at high ping: the client simulates physics the game's own way again, so your car no longer bounces or ignores the wheel after the network corrects it.
- Far fewer freezes on lossy links: the host freezes abandoned cars that stand still away from all players (they wake as soon as someone comes near). Their idle physics noise used to fill every network packet; packets are now about five times smaller.
- Winch: a client can always release a winch attached to the host's car, also with lag and packet loss.
- Host view of other players' cars: no more jumps of several metres; the host follows only the client's own stream.
- Remote cars: wheels spin the right way when reversing, reverse lights follow the car, a car whose data stops stays put instead of drifting.
- More automated tests (car parking, winch release, packet-loss scenarios).

## Что нового в 0.2.4

- Руление при большом пинге: клиент снова считает физику так же, как сама игра, поэтому машина больше не подпрыгивает и не игнорирует руль после сетевой поправки.
- Намного меньше зависаний при потере пакетов: хост замораживает брошенные машины, которые стоят вдали от всех игроков (они просыпаются, как только кто-то подъезжает). Их «шум» физики забивал каждый сетевой пакет; теперь пакеты примерно в пять раз меньше.
- Лебёдка: клиент всегда может отцепить лебёдку от машины хоста, в том числе при лаге и потере пакетов.
- Машины других игроков на экране хоста больше не прыгают на несколько метров: хост следует только потоку самого клиента.
- Чужие машины: колёса крутятся в нужную сторону при движении назад, фары заднего хода следуют за машиной, машина без новых данных стоит на месте, а не уплывает.
- Больше автоматических тестов (парковка машин, отцепление лебёдки, сценарии с потерей пакетов).

---

## Known limits

- Tested on one PC with two game instances and simulated lag and packet loss; a live test on two PCs is still pending. Report issues with F7 → Save diagnostics.
- A frozen abandoned car does not react to things other than player cars (for example a falling meteorite) until a player comes within winch reach. Switch it off with `ParkIdleCars = false` in `[Netcode]` of `BepInEx/config/dev.othlan.lan.cfg` on the host.

## Известные ограничения

- Проверено на одном ПК с двумя копиями игры и имитацией лага и потери пакетов; живая проверка на двух ПК ещё впереди. О проблемах — F7 → сохранить диагностику.
- Замороженная брошенная машина не реагирует ни на что, кроме машин игроков (например, на упавший метеорит), пока игрок не окажется в радиусе лебёдки. Отключается на хосте: `ParkIdleCars = false` в `[Netcode]` файла `BepInEx/config/dev.othlan.lan.cfg`.

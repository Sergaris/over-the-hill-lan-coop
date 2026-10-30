## About

**Unofficial** LAN / virtual-LAN co-op mod for Over The Hill. Not affiliated with, endorsed by, or supported by Funselektor Labs or Exit Games (Photon). Contains no game files: every player needs their own copy of the game from Steam.

## О моде

**Неофициальный** мод для кооператива в Over The Hill по локальной или виртуальной сети. Не связан с Funselektor Labs и Exit Games (Photon), не одобрен и не поддерживается ими. Файлов игры нет: каждому игроку нужна своя копия игры из Steam.

---

## Download

Under **Assets** below, click **`OTH-LAN-0.1.8.1.zip`** (not "Source code").

| File | What it is | Size | SHA-256 |
|---|---|---|---|
| `OTH-LAN-0.1.8.1.zip` | Installer (recommended) | 100404403 B | `1093ba347ad5edad1c581f4e20bef9645f0d72d419a7901dc676899bbe78b9aa` |
| `OTH-LAN-0.1.8.1-manual.zip` | Manual install without the installer | 34527054 B | `0fbbe77b89dc261167aec3b02d08d5429dfa5bea4403971d7c4c879c80ccaf14` |

Plugin `OthLan.dll` sha256 `9fdad8acd01b7c485e7dc4d01837a95481e3856ca35f78523b94de92544262eb`. All checksums are also in `SHA256SUMS.txt`.

## Скачать

В блоке **Assets** ниже нажмите **`OTH-LAN-0.1.8.1.zip`** (не «Source code»).

| Файл | Что это | Размер | SHA-256 |
|---|---|---|---|
| `OTH-LAN-0.1.8.1.zip` | Установщик (рекомендуется) | 100404403 Б | `1093ba347ad5edad1c581f4e20bef9645f0d72d419a7901dc676899bbe78b9aa` |
| `OTH-LAN-0.1.8.1-manual.zip` | Ручная установка без установщика | 34527054 Б | `0fbbe77b89dc261167aec3b02d08d5429dfa5bea4403971d7c4c879c80ccaf14` |

Плагин `OthLan.dll` sha256 `9fdad8acd01b7c485e7dc4d01837a95481e3856ca35f78523b94de92544262eb`. Все контрольные суммы есть также в `SHA256SUMS.txt`.

---

## Install

1. Unpack `OTH-LAN-0.1.8.1.zip` (right-click → *Extract All…*).
2. Close the game, run `OTH-LAN-Setup.exe`, press **Install** (the installer is not code-signed: on "Windows protected your PC" click *More info* → *Run anyway*).
3. Start Over The Hill from Steam (the first start takes 1–3 minutes longer and needs internet once).
4. Everyone joins the same LAN or virtual LAN (Radmin VPN, Hamachi, ZeroTier). Host: **Main menu → Local Co-op → Become host**. Others: **Main menu → Local Co-op**, pick the host, **Join** (or **Add by IP** with `IP:27015`).

All players need 0.1.8.1: an older version is refused with a message that names both versions. Settings are kept; Uninstall: run `OTH-LAN-Setup.exe` again → **Uninstall**.

## Установка

1. Распакуйте `OTH-LAN-0.1.8.1.zip` (правой кнопкой → «Извлечь всё…»).
2. Закройте игру, запустите `OTH-LAN-Setup.exe`, нажмите **«Установить»** (установщик не подписан: на «Система Windows защитила ваш компьютер» нажмите «Подробнее» → «Выполнить в любом случае»).
3. Запустите Over The Hill через Steam (первый запуск дольше на 1–3 минуты и один раз требует интернет).
4. Все подключаются к одной локальной или виртуальной сети (Radmin VPN, Hamachi, ZeroTier). Хост: **главное меню → «Локальный кооп» → «Стать хостом»**. Остальные: **главное меню → «Локальный кооп»**, выберите хост, **«Войти»** (или **«Добавить по IP»** с `IP:27015`).

Всем игрокам нужна 0.1.8.1: игра со старой версией не пускается, в сообщении указаны обе версии. Настройки сохраняются; Удаление: снова запустите `OTH-LAN-Setup.exe` → **«Удалить»**.

---

## Updating

0.1.7 and 0.1.8 cannot update to 0.1.8.1 by themselves (0.1.7 has no update check, 0.1.8 does not read four-part versions), so install 0.1.8.1 by hand once (steps above, over the old version). From 0.1.8.1 on, the main menu offers new versions itself: **Install / Later / Skip this version**. The download is checked against `SHA256SUMS.txt` of the same release and is copied in after you quit the game. Switch it off in F7 ("Check for updates").

## Обновление

0.1.7 и 0.1.8 сами до 0.1.8.1 не обновятся (в 0.1.7 нет проверки обновлений, 0.1.8 не понимает версии из четырёх чисел), поэтому 0.1.8.1 один раз установите вручную (шаги выше, поверх старой версии). Начиная с 0.1.8.1 главное меню само предлагает новые версии: **«Установить» / «Позже» / «Пропустить эту версию»**. Скачанный файл сверяется с `SHA256SUMS.txt` того же релиза и копируется после выхода из игры. Выключается в F7 («Проверять обновления»).

---

## What's new in 0.1.8.1

Both players need 0.1.8.1. From 0.1.8 or older, update by hand once (download the zip, run OTH-LAN-Setup.exe → Install).

- Game update of 23 October 2026 (Steam build 26136884) is supported. 0.1.8 and 0.1.7 show "incompatible" on it and
  keep LAN off.
- F7 on the client: the "Own car without host rollback" switch is back, marked experimental. It turns
  `[Netcode] OwnLocalClock` on or off for this PC only and works at once, even mid-race. If your own car twitches or
  jumps, turn it off here.
- Update check: four-part versions (0.1.8.1) are understood: 0.1.8 < 0.1.8.1 < 0.1.9. 0.1.8 cannot read them, which is
  why the one manual update is needed.

## Что нового в 0.1.8.1

У обоих игроков должна быть 0.1.8.1. С 0.1.8 и более ранних версий обновитесь вручную один раз (скачайте zip, запустите OTH-LAN-Setup.exe → «Установить»).

- Поддерживается обновление игры от 23 октября 2026 (сборка Steam 26136884). 0.1.8 и 0.1.7 показывают на ней
  «несовместимо» и не включают LAN.
- F7 у клиента: вернулся переключатель «Своя машина без отката к хосту», с пометкой «экспериментально». Он
  включает и выключает `[Netcode] OwnLocalClock` только на этом ПК и действует сразу, даже посреди заезда. Если ваша
  машина дёргается или прыгает, выключите его здесь.
- Проверка обновлений: понимает версии из четырёх чисел (0.1.8.1): 0.1.8 < 0.1.8.1 < 0.1.9. 0.1.8 их не понимает,
  поэтому и нужно одно ручное обновление.

---

## Known limits

- Tested only on one PC with simulated ping and loss, not yet between two real PCs over the internet. Real-world results may differ.
- Collisions with other players' cars are approximate; towing between two players stays host-driven.
- What is still to be tested and finished is in the [roadmap](https://github.com/Sergaris/over-the-hill-lan-coop/blob/main/docs/ROADMAP.md).

## Известные ограничения

- Проверено только на одном ПК с имитацией пинга и потерь, ещё не между двумя настоящими ПК через интернет. В реальной игре результаты могут отличаться.
- Столкновения с машинами других игроков приблизительные; буксировка между двумя игроками остаётся под управлением хоста.
- Что ещё нужно проверить и доделать, описано в [плане развития](https://github.com/Sergaris/over-the-hill-lan-coop/blob/main/docs/ROADMAP.md).

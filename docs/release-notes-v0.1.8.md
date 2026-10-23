## About

**Unofficial** LAN / virtual-LAN co-op mod for Over The Hill. Not affiliated with, endorsed by, or supported by Funselektor Labs or Exit Games (Photon). Contains no game files: every player needs their own copy of the game from Steam.

## О моде

**Неофициальный** мод для кооператива в Over The Hill по локальной или виртуальной сети. Не связан с Funselektor Labs и Exit Games (Photon), не одобрен и не поддерживается ими. Файлов игры нет: каждому игроку нужна своя копия игры из Steam.

---

## Download

Under **Assets** below, click **`OTH-LAN-0.1.8.zip`** (not "Source code").

| File | What it is | Size | SHA-256 |
|---|---|---|---|
| `OTH-LAN-0.1.8.zip` | Installer (recommended) | 100402435 B | `88ed43506bea24bf7213519aa6c2e8ded32db34a03d4e8eb3676082fc93ca4e3` |
| `OTH-LAN-0.1.8-manual.zip` | Manual install without the installer | 34526033 B | `775c13b931357db41ac6193a1164d90d87cf965b0cf9fb1ba29ab41e6a099868` |

Plugin `OthLan.dll` sha256 `4224e08ee6f52560960737660b80b430e61a64b115655a7fc3c9695baa0dc25a`. All checksums are also in `SHA256SUMS.txt`.

## Скачать

В блоке **Assets** ниже нажмите **`OTH-LAN-0.1.8.zip`** (не «Source code»).

| Файл | Что это | Размер | SHA-256 |
|---|---|---|---|
| `OTH-LAN-0.1.8.zip` | Установщик (рекомендуется) | 100402435 Б | `88ed43506bea24bf7213519aa6c2e8ded32db34a03d4e8eb3676082fc93ca4e3` |
| `OTH-LAN-0.1.8-manual.zip` | Ручная установка без установщика | 34526033 Б | `775c13b931357db41ac6193a1164d90d87cf965b0cf9fb1ba29ab41e6a099868` |

Плагин `OthLan.dll` sha256 `4224e08ee6f52560960737660b80b430e61a64b115655a7fc3c9695baa0dc25a`. Все контрольные суммы есть также в `SHA256SUMS.txt`.

---

## Install

1. Unpack `OTH-LAN-0.1.8.zip` (right-click → *Extract All…*).
2. Close the game, run `OTH-LAN-Setup.exe`, press **Install** (the installer is not code-signed: on "Windows protected your PC" click *More info* → *Run anyway*).
3. Start Over The Hill from Steam (the first start takes 1–3 minutes longer and needs internet once).
4. Everyone joins the same LAN or virtual LAN (Radmin VPN, Hamachi, ZeroTier). Host: **Main menu → Local Co-op → Become host**. Others: **Main menu → Local Co-op**, pick the host, **Join** (or **Add by IP** with `IP:27015`).

All players need 0.1.8: a 0.1.7 game is refused with a message that names both versions. Settings are kept; ping compensation is switched on for everyone once. Uninstall: run `OTH-LAN-Setup.exe` again → **Uninstall**.

## Установка

1. Распакуйте `OTH-LAN-0.1.8.zip` (правой кнопкой → «Извлечь всё…»).
2. Закройте игру, запустите `OTH-LAN-Setup.exe`, нажмите **«Установить»** (установщик не подписан: на «Система Windows защитила ваш компьютер» нажмите «Подробнее» → «Выполнить в любом случае»).
3. Запустите Over The Hill через Steam (первый запуск дольше на 1–3 минуты и один раз требует интернет).
4. Все подключаются к одной локальной или виртуальной сети (Radmin VPN, Hamachi, ZeroTier). Хост: **главное меню → «Локальный кооп» → «Стать хостом»**. Остальные: **главное меню → «Локальный кооп»**, выберите хост, **«Войти»** (или **«Добавить по IP»** с `IP:27015`).

Всем игрокам нужна 0.1.8: игра с 0.1.7 не пускается, в сообщении указаны обе версии. Настройки сохраняются; компенсация пинга один раз включается у всех. Удаление: снова запустите `OTH-LAN-Setup.exe` → **«Удалить»**.

---

## Updating

0.1.7 has no update check, so install 0.1.8 by hand once (steps above, over the old version). From 0.1.8 on, the main menu offers new versions itself: **Install / Later / Skip this version**. The download is checked against `SHA256SUMS.txt` of the same release and is copied in after you quit the game. Switch it off in F7 ("Check for updates").

## Обновление

В 0.1.7 нет проверки обновлений, поэтому 0.1.8 один раз установите вручную (шаги выше, поверх старой версии). Начиная с 0.1.8 главное меню само предлагает новые версии: **«Установить» / «Позже» / «Пропустить эту версию»**. Скачанный файл сверяется с `SHA256SUMS.txt` того же релиза и копируется после выхода из игры. Выключается в F7 («Проверять обновления»).

---

## What's new in 0.1.8

**Netcode: your own car feels like single player.**
- Your own car runs on your PC's clock. Before, at high ping Fusion (the game's network engine) stopped and then caught up in one frame: the car froze, then lurched. Now it keeps driving with your current controls.
- Other players' cars are shown smoothly from Fusion's interpolation buffer instead of being predicted and corrected. Collisions with them are approximate.
- Short freezes are smoothed out.
- F7: turning ping compensation off needs a second click, and F7 warns while it is off (with packet loss the game's own netcode can throw cars back by metres).

**Measured on one PC (self-test with 2–4 game copies, simulated delay and loss), about 3 minutes each:**

| Case | 0.1.7, compensation off | 0.1.7, compensation on | 0.1.8 |
|---|---|---|---|
| Own car, 600 ms ping: jumps over 50 cm | 165 | 305 | 1–2 |
| Own car, 600 ms ping: largest jump | 206 cm | 1032 cm | 58 cm |
| Own car, 400 ms ping + 5 % loss: jumps over 50 cm | – | – | 1 (none over 2 m) |
| Other players' cars, 600 ms ping: jumps over 50 cm / over 2 m | 237 / 2 | – | 11 / 0 |

**Fixed (reports from 0.1.7):**
- The Local Co-op button and screen work with arrow keys and a gamepad.
- "Join" no longer fails with "The host is on map … and you do not have it".
- Own car no longer stutters with jerky steering and image (the old "own car without rollback" switch is replaced by ping compensation).
- New "Mods" button on the Local Co-op screen: the host chooses whether mods are allowed (on by default).
- No more heavy stutter after a connection drop: reconnecting works at once and the host no longer fights a frozen client.

**Update checker:** see "Updating" above.

Full details: [CHANGELOG](https://github.com/Sergaris/over-the-hill-lan-coop/blob/main/CHANGELOG.md), [README](https://github.com/Sergaris/over-the-hill-lan-coop#readme).

## Что нового в 0.1.8

**Сетевой код: своя машина ощущается как в одиночной игре.**
- Своя машина идёт по часам вашего ПК. Раньше на высоком пинге Fusion (сетевой движок игры) останавливался, а потом догонял за один кадр: машина замирала, потом дёргалась вперёд. Теперь она едет дальше с вашим текущим управлением.
- Машины других игроков показываются плавно из буфера интерполяции Fusion, а не предсказываются с поправками. Столкновения с ними приблизительные.
- Короткие замирания сглаживаются.
- F7: чтобы выключить компенсацию пинга, нужно нажать второй раз, и пока она выключена, F7 предупреждает (при потерях пакетов обычный сетевой код игры может откидывать машины на метры).

**Замеры на одном ПК (самотест с 2–4 копиями игры, имитация задержки и потерь), примерно по 3 минуты:**

| Случай | 0.1.7, компенсация выкл. | 0.1.7, компенсация вкл. | 0.1.8 |
|---|---|---|---|
| Своя машина, пинг 600 мс: рывков больше 50 см | 165 | 305 | 1–2 |
| Своя машина, пинг 600 мс: самый большой рывок | 206 см | 1032 см | 58 см |
| Своя машина, пинг 400 мс + 5 % потерь: рывков больше 50 см | – | – | 1 (больше 2 м ни одного) |
| Машины других игроков, пинг 600 мс: рывков больше 50 см / больше 2 м | 237 / 2 | – | 11 / 0 |

**Исправлено (отчёты по 0.1.7):**
- Кнопка и экран «Локальный кооп» работают со стрелками и геймпадом.
- «Войти» больше не падает с «Хост на карте …, а у вас её нет».
- Своя машина больше не дёргается, руль и картинка плавные (старый переключатель «своя машина без отката» заменён компенсацией пинга).
- Новая кнопка «Моды» на экране «Локальный кооп»: хост выбирает, разрешены ли моды (по умолчанию да).
- Больше нет сильных рывков после обрыва связи: повторный вход работает сразу, и хост больше не борется с зависшим клиентом.

**Проверка обновлений:** см. «Обновление» выше.

Подробности: [CHANGELOG](https://github.com/Sergaris/over-the-hill-lan-coop/blob/main/CHANGELOG.md), [README](https://github.com/Sergaris/over-the-hill-lan-coop#readme).

---

## Known limits

- Tested only on one PC with simulated ping and loss, not yet between two real PCs over the internet. Real-world results may differ.
- Collisions with other players' cars are approximate; towing between two players stays host-driven.
- What is still to be tested and finished is in the [roadmap](https://github.com/Sergaris/over-the-hill-lan-coop/blob/main/docs/ROADMAP.md).

## Известные ограничения

- Проверено только на одном ПК с имитацией пинга и потерь, ещё не между двумя настоящими ПК через интернет. В реальной игре результаты могут отличаться.
- Столкновения с машинами других игроков приблизительные; буксировка между двумя игроками остаётся под управлением хоста.
- Что ещё нужно проверить и доделать, описано в [плане развития](https://github.com/Sergaris/over-the-hill-lan-coop/blob/main/docs/ROADMAP.md).

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OthLan.Installer;

/// <summary>Russian GUI for friends. Test flags: --game DIR, --no-autodetect, --do install|uninstall|install,uninstall, --snap FILE.png, --exit</summary>
sealed class MainForm : Form
{
    readonly string[] _args;
    readonly JsonElement _m;
    readonly int _appId;
    readonly string _modVersion;
    readonly string _supportedBuilds;
    public int ExitCode { get; private set; }

    string _gameDir;
    bool _buildOk;
    bool _busy;

    readonly Font _f = new("Segoe UI", 10f);
    readonly Font _fb = new("Segoe UI", 10f, FontStyle.Bold);
    readonly Font _fh = new("Segoe UI", 15f, FontStyle.Bold);

    Panel _pathDot, _buildDot, _radminDot;
    TextBox _path, _logBox;
    Label _buildLbl, _radminLbl, _modLbl, _progLbl;
    Button _btnInstall, _btnRemove, _btnLog, _btnPick, _btnCopy;
    LinkLabel _radminLink;
    ProgressBar _prog;

    static readonly Color Ok = Color.FromArgb(46, 160, 67), Bad = Color.FromArgb(207, 34, 46), Wait = Color.FromArgb(150, 150, 150), Warn = Color.FromArgb(210, 150, 0);

    public MainForm(string[] args)
    {
        _args = args;
        _m = Payload.Manifest().RootElement;
        _appId = _m.GetProperty("steamAppId").GetInt32();
        _modVersion = _m.GetProperty("modVersion").GetString();
        _supportedBuilds = string.Join(", ", _m.GetProperty("builds").EnumerateArray().Select(b => b.GetProperty("buildId").GetString()));
        Log.Info($"OTH-LAN Setup GUI {_modVersion} (log: {Log.PathOnDisk})");
        try { var dll = Payload.Embedded("OthLan.dll"); Log.Info($"bundled plugin OthLan.dll sha256 {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(dll)).ToLowerInvariant()}"); } catch { }
        Build();
        Log.Line += (lvl, line) => UI(() => { _logBox.AppendText(line + Environment.NewLine); });
        Shown += async (_, _) => await OnShownAsync();
    }

    // ---------------- layout ----------------
    void Build()
    {
        Text = UiText.T($"OTH-LAN {_modVersion} — установка мода для Over The Hill", $"OTH-LAN {_modVersion}: mod setup for Over The Hill");
        Font = _f;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(780, 820);
        BackColor = Color.White;

        int x = 20, w = 740, y = 14;
        Add(new Label { Text = UiText.T("OTH-LAN: игра Over The Hill по сети через Radmin VPN", "OTH-LAN: play Over The Hill together over Radmin VPN / LAN"), Font = _fh, Left = x, Top = y, Width = w, Height = 32 });
        y += 36;
        Add(new Label
        {
            Left = x, Top = y, Width = w, Height = 235, BackColor = Color.FromArgb(245, 247, 250), Padding = new Padding(8),
            Text =
                UiText.T("Программа ставит мод OTH-LAN в папку игры, чтобы играть вместе напрямую через Radmin VPN, без серверов Photon.\n", "This program installs the OTH-LAN mod into the game folder so you can play together directly over Radmin VPN or LAN, without Photon servers.\n") +
                UiText.T("Что нужно сделать:\n", "What to do:\n") +
                UiText.T("  1. Установите Radmin VPN и войдите в ту же сеть, что и хост\n      (название и пароль сети спросите у хоста).\n", "  1. Install Radmin VPN and join the same network as the host\n      (ask the host for the network name and password). On one LAN you can skip this.\n") +
                UiText.T("  2. Закройте игру и нажмите «Установить» ниже.\n", "  2. Close the game and press \"Install\" below.\n") +
                UiText.T("  3. Запустите игру через Steam. Первый запуск с модом дольше на 1–3 минуты (нужен интернет).\n", "  3. Start the game through Steam. The first start with the mod takes 1–3 minutes longer (needs internet).\n") +
                UiText.T("  4. Подключиться к другу: главное меню > «Локальный кооп» — там список игр в вашей сети;\n", "  4. Join a friend: main menu > \"Local Co-op\" lists the games on your network;\n") +
                UiText.T("      или F7, введите Radmin-IP:порт хоста (например 26.x.x.x:27015) и нажмите «Join».\n", "      or press F7, enter the host's Radmin IP:port (e.g. 26.x.x.x:27015) and press \"Join\".\n") +
                UiText.T("  5. Быть хостом: «Локальный кооп» > «Стать хостом» (или F8, затем обычное «Создать сервер»).\n      Если друзья не видят вас в списке, дайте им свой Radmin-IP:27015 (кнопка «Копировать IP»).\n", "  5. Host yourself: \"Local Co-op\" > \"Become host\" (or F8, then the usual \"Create server\").\n      Give friends your Radmin IP:27015 (the \"Copy IP\" button) if they do not see you in the list.\n") +
                UiText.T("Удалить мод можно в любой момент кнопкой «Удалить» — файлы игры вернутся как были.", "You can remove the mod at any time with \"Uninstall\": the game files go back to how they were.")
        });
        y += 245;

        // game folder
        Add(new Label { Text = UiText.T("Папка игры", "Game folder"), Font = _fb, Left = x, Top = y, Width = 300 });
        y += 24;
        _pathDot = Dot(x, y + 6);
        _path = new TextBox { Left = x + 24, Top = y + 2, Width = 520, ReadOnly = true, BackColor = Color.White, Text = UiText.T("поиск…", "searching…") };
        Add(_path);
        _btnPick = Btn(UiText.T("Выбрать папку…", "Choose folder…"), x + 556, y, 184, 30, (_, _) => PickFolder());
        y += 38;
        _buildDot = Dot(x, y + 6);
        _buildLbl = Lbl(UiText.T("Версия игры: —", "Game version: —"), x + 24, y, w - 24, 40);
        y += 46;

        // radmin
        Add(new Label { Text = "Radmin VPN", Font = _fb, Left = x, Top = y, Width = 300 });
        y += 24;
        _radminDot = Dot(x, y + 6);
        _radminLbl = Lbl(UiText.T("Ищем адаптер Radmin VPN…", "Looking for the Radmin VPN adapter…"), x + 24, y, 520, 24);
        _btnCopy = Btn(UiText.T("Копировать IP", "Copy IP"), x + 556, y - 3, 184, 30, (_, _) => CopyIp());
        _radminLink = new LinkLabel { Left = x + 24, Top = y + 24, Width = 520, Height = 22, Text = UiText.T("Скачать Radmin VPN: https://www.radmin-vpn.com/ru/", "Download Radmin VPN: https://www.radmin-vpn.com/"), Visible = false };
        _radminLink.LinkClicked += (_, _) => OpenUrl("https://www.radmin-vpn.com/ru/");
        Add(_radminLink);
        y += 52;

        _modLbl = Lbl(UiText.T("Мод: —", "Mod: —"), x, y, w, 24);
        _modLbl.Font = _fb;
        y += 32;

        _btnInstall = Btn(UiText.T("Установить", "Install"), x, y, 300, 52, async (_, _) => await DoInstallAsync());
        _btnInstall.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
        _btnInstall.BackColor = Color.FromArgb(46, 160, 67); _btnInstall.ForeColor = Color.White; _btnInstall.FlatStyle = FlatStyle.Flat;
        _btnRemove = Btn(UiText.T("Удалить (вернуть оригинал)", "Uninstall (restore original)"), x + 312, y, 260, 52, async (_, _) => await DoRemoveAsync());
        _btnLog = Btn(UiText.T("Открыть лог", "Open log"), x + 584, y, 156, 52, (_, _) => OpenLog());
        y += 62;

        _prog = new ProgressBar { Left = x, Top = y, Width = w, Height = 22, Minimum = 0, Maximum = 100 };
        Add(_prog);
        y += 26;
        _progLbl = Lbl("", x, y, w, 22);
        y += 26;
        _logBox = new TextBox { Left = x, Top = y, Width = w, Height = 820 - y - 12, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 8.5f), BackColor = Color.FromArgb(250, 250, 250) };
        Add(_logBox);
        SetButtons();
    }

    T Add<T>(T c) where T : Control { Controls.Add(c); return c; }
    Label Lbl(string t, int x, int y, int w, int h) => Add(new Label { Text = t, Left = x, Top = y, Width = w, Height = h });
    Button Btn(string t, int x, int y, int w, int h, EventHandler click) { var b = Add(new Button { Text = t, Left = x, Top = y, Width = w, Height = h }); b.Click += click; return b; }
    Panel Dot(int x, int y) => Add(new Panel { Left = x, Top = y, Width = 14, Height = 14, BackColor = Wait });
    void UI(Action a) { if (IsDisposed) return; if (InvokeRequired) BeginInvoke(a); else a(); }

    // ---------------- logic ----------------
    string Arg(string n) { var i = Array.IndexOf(_args, n); return i >= 0 && i + 1 < _args.Length ? _args[i + 1] : null; }

    async Task OnShownAsync()
    {
        RefreshRadmin();
        string dir = null;
        if (Arg("--game") is string g) dir = Steam.ResolveGameDir(g) ?? g;
        else if (!_args.Contains("--no-autodetect"))
        {
            _path.Text = UiText.T("ищем игру в библиотеках Steam…", "looking for the game in the Steam libraries…");
            var found = await Task.Run(() => { try { return Steam.Find(_appId); } catch (Exception e) { Log.Warn("Steam search: " + e.Message); return null; } });
            dir = found?.Dir;
        }
        if (dir == null)
        {
            Log.Warn("Game not found automatically");
            _pathDot.BackColor = Bad;
            _path.Text = UiText.T("Игра не найдена автоматически. Нажмите «Выбрать папку…»", "The game was not found automatically. Press \"Choose folder…\"");
            _buildLbl.Text = UiText.T("Укажите папку, где лежит «over the hill.exe». Где она: в Steam щёлкните правой кнопкой\nпо Over The Hill > Управление > Просмотреть локальные файлы.", "Choose the folder that contains \"over the hill.exe\". To find it: in Steam right-click\nOver The Hill > Manage > Browse local files.");
        }
        else await SetGameDirAsync(dir);
        await RunTestScriptAsync();
    }

    void RefreshRadmin()
    {
        var ip = Net.RadminIp(out var adapter);
        if (ip != null)
        {
            _radminDot.BackColor = Ok;
            _radminLbl.Text = UiText.T($"Ваш Radmin-IP: {ip}   (для друзей: {ip}:27015)", $"Your Radmin IP: {ip}   (for friends: {ip}:27015)");
            _btnCopy.Enabled = true; _radminLink.Visible = false;
            Log.Info($"Radmin VPN IP: {ip} ({adapter})");
        }
        else
        {
            _radminDot.BackColor = Warn;
            _radminLbl.Text = UiText.T("Radmin VPN не найден. Установите его и войдите в сеть хоста:", "Radmin VPN not found. Install it and join the host's network:");
            _btnCopy.Enabled = false; _radminLink.Visible = true;
            Log.Info("Radmin VPN adapter not found");
        }
    }

    void CopyIp()
    {
        var ip = Net.RadminIp(out _);
        if (ip == null) return;
        try { Clipboard.SetText($"{ip}:27015"); _progLbl.Text = UiText.T($"Скопировано: {ip}:27015", $"Copied: {ip}:27015"); }
        catch (Exception e) { _progLbl.Text = UiText.T("Не удалось скопировать: ", "Could not copy: ") + e.Message; }
    }

    void PickFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = UiText.T("Выберите папку игры Over The Hill (там, где лежит «over the hill.exe»)", "Choose the Over The Hill game folder (the one with \"over the hill.exe\")"),
            UseDescriptionForTitle = true, ShowNewFolderButton = false,
            SelectedPath = _gameDir ?? "",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) _ = SetGameDirAsync(dlg.SelectedPath);
    }

    async Task SetGameDirAsync(string picked)
    {
        var dir = Steam.ResolveGameDir(picked);
        _buildOk = false; _gameDir = null; SetButtons();
        if (dir == null)
        {
            _pathDot.BackColor = Bad;
            _path.Text = picked;
            _buildDot.BackColor = Bad;
            _buildLbl.Text = UiText.T("В этой папке нет «over the hill.exe». Выберите папку игры (…\\steamapps\\common\\over the hill).", "There is no \"over the hill.exe\" in this folder. Choose the game folder (…\\steamapps\\common\\over the hill).");
            Log.Warn($"Not a game folder: {picked}");
            return;
        }
        _gameDir = dir;
        _pathDot.BackColor = Ok;
        _path.Text = dir;
        _buildDot.BackColor = Wait;
        _buildLbl.Text = UiText.T("Проверяем версию игры…", "Checking the game version…");
        Log.Info($"Game folder: {dir}");
        var inst = new Installer(dir, _m);
        Dictionary actual = null;
        var match = await Task.Run(() => { var r = inst.MatchBuild(out var a); actual = new Dictionary(a); return r; });
        var steamBuild = Steam.BuildIdFor(dir, _appId);
        if (match != null)
        {
            _buildOk = true;
            _buildDot.BackColor = Ok;
            var bid = match.Value.GetProperty("buildId").GetString();
            _buildLbl.Text = UiText.T($"Версия игры подходит (build {bid})", $"The game version is supported (build {bid})");
            Log.Ok($"Supported build {match.Value.GetProperty("buildId").GetString()} (acf buildid {steamBuild ?? "?"})");
        }
        else
        {
            _buildDot.BackColor = Warn;
            var yours = steamBuild ?? UiText.T("другой", "another");
            _buildLbl.Text = UiText.T($"Эта версия игры не проверена (проверен Steam-build {_supportedBuilds}, у вас {yours}).\n", $"This game version has not been tested (tested: Steam build {_supportedBuilds}, yours: {yours}).\n") +
                             UiText.T("Установить можно: при запуске мод сам проверит совместимость и спросит, включать ли LAN.", "You can install: on start the mod checks compatibility itself and asks whether to enable LAN.");
            foreach (var kv in actual.Items) Log.Warn($"  {kv.Key} = {kv.Value}");
            Log.Warn($"Untested build (acf buildid {steamBuild ?? "?"}); install allowed with a warning");
        }
        RefreshModState();
    }

    sealed class Dictionary { public System.Collections.Generic.Dictionary<string, string> Items; public Dictionary(System.Collections.Generic.Dictionary<string, string> d) => Items = d; }

    void RefreshModState()
    {
        if (_gameDir == null) { _modLbl.Text = UiText.T("Мод: —", "Mod: —"); SetButtons(); return; }
        var st = new Installer(_gameDir, _m).LoadState();
        _modLbl.Text = st == null ? UiText.T("Мод: не установлен", "Mod: not installed") : UiText.T($"Мод: установлен OTH-LAN {st.ModVersion} ({st.InstalledAt:dd.MM.yyyy HH:mm})", $"Mod: OTH-LAN {st.ModVersion} installed ({st.InstalledAt:yyyy-MM-dd HH:mm})");
        _btnInstall.Text = st == null ? UiText.T("Установить", "Install") : (st.ModVersion == _modVersion ? UiText.T("Переустановить", "Reinstall") : UiText.T($"Обновить до {_modVersion}", $"Update to {_modVersion}"));
        SetButtons();
    }

    void SetButtons()
    {
        if (_btnInstall == null) return;
        var installed = _gameDir != null && new Installer(_gameDir, _m).LoadState() != null;
        _btnInstall.Enabled = !_busy && _gameDir != null;
        _btnInstall.BackColor = _btnInstall.Enabled ? Color.FromArgb(46, 160, 67) : Color.FromArgb(180, 180, 180);
        _btnRemove.Enabled = !_busy && installed;
        _btnPick.Enabled = !_busy;
    }

    bool GameRunningGuard()
    {
        if (_gameDir == null || !new Installer(_gameDir, _m).GameRunning()) return false;
        Friendly(UiText.T("Игра сейчас запущена", "The game is running"), UiText.T("Закройте Over The Hill и нажмите кнопку ещё раз.", "Close Over The Hill and press the button again."));
        return true;
    }

    async Task<bool> RunOpAsync(string title, Func<Installer, Action<int, string>, bool> op)
    {
        if (_gameDir == null || GameRunningGuard()) return false;
        _busy = true; SetButtons();
        _prog.Value = 0; _progLbl.Text = title + "…";
        var inst = new Installer(_gameDir, _m);
        void Progress(int p, string what) => UI(() => { _prog.Value = Math.Clamp(p, 0, 100); _progLbl.Text = $"{title}: {what} ({p}%)"; });
        bool ok;
        try { ok = await Task.Run(() => op(inst, Progress)); }
        catch (Exception e) { Log.Err(e.ToString()); ok = false; }
        _busy = false;
        RefreshModState();
        return ok;
    }

    async Task DoInstallAsync()
    {
        if (!_buildOk && !Quiet && MessageBox.Show(this,
                UiText.T("Эта версия игры не проверена с OTH-LAN (игра, вероятно, обновилась).\n\n", "This game version has not been tested with OTH-LAN (the game was probably updated).\n\n") +
                UiText.T("При запуске игры мод проверит код и структуры Fusion:\n", "When the game starts, the mod checks Fusion's code and data layout:\n") +
                UiText.T(" • если они не изменились — один раз спросит, включать ли LAN на ваш риск;\n", " • unchanged: it asks once whether to enable LAN at your own risk;\n") +
                UiText.T(" • если изменились — LAN останется выключен до обновления мода.\n\n", " • changed: LAN stays off until the mod is updated.\n\n") +
                UiText.T("Хост и клиенты всё равно должны иметь одинаковую версию игры.\n\nУстановить?", "The host and the clients still need the same game version.\n\nInstall?"),
                UiText.T("OTH-LAN — версия игры не проверена", "OTH-LAN: game version not tested"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        var ok = await RunOpAsync(UiText.T("Установка", "Installing"), (i, p) => i.Install(false, 27015, p));
        if (ok)
        {
            _prog.Value = 100;
            _progLbl.Text = UiText.T("Готово! Запустите игру через Steam. В меню: F7 — окно OTH-LAN, F8 — хост, F9 — подключиться.", "Done! Start the game through Steam. Main menu: \"Local Co-op\"; F7: OTH-LAN window, F8: host, F9: join.");
            if (!Quiet) MessageBox.Show(this, UiText.T("Мод установлен.\n\nЗапустите игру через Steam (первый запуск 1–3 минуты дольше).\n", "The mod is installed.\n\nStart the game through Steam (the first start takes 1–3 minutes longer).\n") +
                UiText.T("В главном меню откройте «Локальный кооп», чтобы увидеть игры в сети и войти,\nили нажмите F7 и введите Radmin-IP хоста:27015. Хостить: «Локальный кооп» > «Стать хостом».", "In the main menu open \"Local Co-op\" to see and join games on your network,\nor press F7 and enter the host's Radmin IP:27015. To host: \"Local Co-op\" > \"Become host\"."),
                UiText.T("OTH-LAN — готово", "OTH-LAN: done"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else Fail(UiText.T("Установка не удалась", "Installation failed"));
    }

    async Task DoRemoveAsync()
    {
        if (!Quiet && MessageBox.Show(this, UiText.T("Удалить мод и вернуть файлы игры к оригиналу?", "Remove the mod and restore the original game files?"), "OTH-LAN", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var ok = await RunOpAsync(UiText.T("Удаление", "Removing"), (i, p) => i.Uninstall(p));
        if (ok) { _progLbl.Text = UiText.T("Мод удалён, файлы игры восстановлены.", "Mod removed, game files restored."); if (!Quiet) MessageBox.Show(this, UiText.T("Мод удалён. Игра снова как была.", "Mod removed. The game is as it was."), "OTH-LAN", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        else Fail(UiText.T("Удаление прошло не полностью", "Removal was incomplete"));
    }

    bool Quiet => _args.Contains("--do");

    void Fail(string what)
    {
        var last = _logBox.Lines.LastOrDefault(l => l.Contains("ERROR")) ?? "";
        _progLbl.Text = what + UiText.T(". Подробности — кнопка «Открыть лог».", ". Details: the \"Open log\" button.");
        Friendly(what, UiText.T($"{last}\n\nЕсли не получается — нажмите «Открыть лог» и отправьте файл тому, кто помогает с установкой.\nЛог: {Log.PathOnDisk}", $"{last}\n\nIf it does not work, press \"Open log\" and send the file to whoever helps you with the setup.\nLog: {Log.PathOnDisk}"));
    }

    void Friendly(string title, string text)
    {
        Log.Warn($"{title}: {text.Replace('\n', ' ')}");
        if (!Quiet) MessageBox.Show(this, text, "OTH-LAN — " + title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else _progLbl.Text = title + ": " + text.Split('\n')[0];
    }

    void OpenLog()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Log.PathOnDisk}\"") { UseShellExecute = true }); }
        catch { try { Process.Start(new ProcessStartInfo(Log.PathOnDisk) { UseShellExecute = true }); } catch (Exception e) { Friendly(UiText.T("Не удалось открыть лог", "Could not open the log"), Log.PathOnDisk + "\n" + e.Message); } }
    }

    static void OpenUrl(string u) { try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch { } }

    public static void Crash(Exception e)
    {
        Log.Err("Unhandled: " + e);
        try { MessageBox.Show(UiText.T($"Непредвиденная ошибка:\n{e?.Message}\n\nЛог для разбора проблемы:\n{Log.PathOnDisk}", $"Unexpected error:\n{e?.Message}\n\nLog for troubleshooting:\n{Log.PathOnDisk}"), UiText.T("OTH-LAN — ошибка", "OTH-LAN: error"), MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
    }

    // ---------------- test automation ----------------
    async Task RunTestScriptAsync()
    {
        var snap = Arg("--snap");
        int n = 0;
        async Task Snap(string tag)
        {
            if (snap == null) return;
            await Task.Delay(600);
            Refresh();
            var file = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(snap)) ?? ".", $"{Path.GetFileNameWithoutExtension(snap)}-{n++}-{tag}.png");
            using var bmp = new Bitmap(Width, Height);
            using (var g = Graphics.FromImage(bmp))
            {
                try { g.CopyFromScreen(Bounds.Location, Point.Empty, Bounds.Size); }  // real pixels (DrawToBitmap is blank under Wine)
                catch { DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height)); }
            }
            bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            Log.Info($"[test] snapshot {file}");
        }
        await Snap("start");
        var steps = (Arg("--do") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var s in steps)
        {
            if (s == "install") { await DoInstallAsync(); ExitCode |= _btnRemove.Enabled ? 0 : 1; }
            else if (s == "uninstall") { await DoRemoveAsync(); ExitCode |= _btnRemove.Enabled ? 2 : 0; }
            await Snap(s);
        }
        if (_args.Contains("--exit")) { Log.Info($"[test] exit code {ExitCode}"); Close(); }
    }
}

using UnityEngine;

namespace OthLan;

/// <summary>
/// 0.1.8 in-game update: once per start (background, 5 s timeout) ask GitHub for the latest release of the public repo;
/// if newer, a prompt in the main menu (Install / Later / Skip this version; mouse, keyboard, gamepad). 0.1.9.1: if the
/// check fails (timeout / HTTP / parse), a one-shot Offline notice (Retry / Later) appears in the main menu too.
/// Install (<see cref="UpdateJob"/>): download OTH-LAN-x.y.z.zip + SHA256SUMS.txt of the same release, sha256 must match
/// (zip and the Setup.exe inside it), unpack to %TEMP%, start that Setup.exe "update --wait-pid": it replaces the plugin
/// DLL after the game has exited (the loaded DLL cannot be replaced in place). Rules and parsing: UpdateLogic.
/// </summary>
internal static class Updater
{
    public enum St { Off, Idle, Checking, UpToDate, Offline, Available, Downloading, Ready, Failed }
    public static volatile St State = St.Idle;
    public static UpdateLogic.Release Latest;
    public static string Detail = "";             // English, for the log / F7 (failure reason)
    public static float Progress;                 // 0..1 while downloading
    public static bool PromptOpen => _prompt;
    public static int Checks, Fails, Started;
    static bool _prompt, _dismissed, _readyShown, _offlineShown;
    static int _focus;
    static UnityEngine.EventSystems.EventSystem _es;

    public static void Init()
    {
#if SELFTEST
        if (!string.IsNullOrWhiteSpace(Plugin.UpdateTestApi.Value))
        {
            var api = Plugin.UpdateTestApi.Value.Trim();
            UpdateJob.SetTestEndpoint(api);
            Plugin.L.LogWarning($"[Update] TEST endpoint {api} (self-test build only)");
        }
#endif
        if (!Plugin.UpdateCheck.Value) { State = St.Off; Plugin.L.LogInfo("[Update] check for updates is off (F7 / cfg Update.CheckForUpdates)"); return; }
        CheckAsync(delaySeconds: 3);
    }

    public static void CheckAsync(double delaySeconds = 0)
    {
        if (State is St.Checking or St.Downloading) return;
        State = St.Checking; Checks++;
        _offlineShown = false; // a fresh check may notify again if it still fails
        UpdateJob.Check(delaySeconds);
    }

    /// <summary>F7 / dialog: re-run the GitHub check now.</summary>
    public static void CheckNow()
    {
        Plugin.UpdateSkipped.Value = "";
        _dismissed = false; _readyShown = false; _offlineShown = false;
        if (_prompt) Close();
        CheckAsync(0);
    }

    /// <summary>Download, verify, unpack, start the verified Setup.exe "update --wait-pid". Background.</summary>
    public static void Install()
    {
        var rel = Latest;
        if (rel == null || State is St.Downloading or St.Ready) return;
        State = St.Downloading; Progress = 0; Detail = "";
        UpdateJob.Install(rel);
    }

    public static void Skip() { if (Latest != null) { Plugin.UpdateSkipped.Value = Latest.Version; Plugin.L.LogInfo($"[Update] {Latest.Version} skipped by the user"); } Close(); }
    public static void Later() { Plugin.L.LogInfo("[Update] later"); Close(); }
    public static void QuitAndUpdate() { Plugin.L.LogInfo("[Update] quit and update"); Close(); Application.Quit(); }

    static void Close()
    {
        _prompt = false; _dismissed = true;
        try { if (_es != null && !_es.WasCollected) _es.enabled = true; } catch { }
        _es = null;
        UiModal.Close("update");
    }

    static string[] Buttons() => State switch
    {
        St.Ready => new[] { "upd.quitUpdate", "upd.later" },
        St.Failed => new[] { "upd.retry", "upd.later" },
        St.Offline => new[] { "upd.retry", "upd.later" },
        St.Downloading => new[] { "upd.later" },
        _ => new[] { "upd.install", "upd.later", "upd.skip" },
    };

    static void Press(string key)
    {
        switch (key)
        {
            case "upd.install": Install(); _focus = 0; break;
            case "upd.retry":
                if (State == St.Offline) CheckNow();
                else Install();
                _focus = 0;
                break;
            case "upd.later": Later(); break;
            case "upd.skip": Skip(); break;
            case "upd.quitUpdate": QuitAndUpdate(); break;
        }
    }

    /// <summary>Main thread: opens the prompt in the main menu; keyboard + gamepad focus.</summary>
    public static void Tick()
    {
        if (!_prompt)
        {
            // a finished download re-opens the prompt once (Quit and update), even after "Later" during the download
            // T1.3: do not steal focus while another Canvas modal (e.g. mod gate) is open.
            if (UiModal.Current != null) return;
            if (State == St.Ready && !_readyShown && Latest != null && CoopMenu.MainMenuVisible) { _readyShown = true; _dismissed = false; }
            else if (State == St.Offline && !_offlineShown && !_dismissed && CoopMenu.MainMenuVisible)
            {
                if (!UiModal.TryOpen("update")) return;
                _offlineShown = true; _dismissed = false;
                _prompt = true; _focus = 0;
                try { _es = UnityEngine.EventSystems.EventSystem.current; if (_es != null) _es.enabled = false; } catch { _es = null; }
                Plugin.L.LogInfo($"[Update] offline prompt shown ({Detail})");
                return;
            }
            else if (_dismissed || State != St.Available || Latest == null || !CoopMenu.MainMenuVisible) return;
            else if (!UpdateLogic.ShouldOffer(Latest.Version, Plugin.Version, Plugin.UpdateSkipped.Value)) { _dismissed = true; return; }
            if (!UiModal.TryOpen("update")) return;
            _prompt = true; _focus = 0;
            // the game's menu must not move behind the prompt (bug #1 lesson: one owner of navigation at a time)
            try { _es = UnityEngine.EventSystems.EventSystem.current; if (_es != null) _es.enabled = false; } catch { _es = null; }
            Plugin.L.LogInfo($"[Update] prompt shown for {Latest.Version}");
            return;
        }
        if (!CoopMenu.MainMenuVisible && State != St.Downloading && State != St.Ready)
        {
            _prompt = false;
            try { if (_es != null) _es.enabled = true; } catch { }
            _es = null;
            UiModal.Close("update");
            return;
        }
        var b = Buttons();
        if (_focus >= b.Length) _focus = 0;
        try
        {
            var kb = UnityEngine.InputSystem.Keyboard.current; var gp = UnityEngine.InputSystem.Gamepad.current;
            bool left = kb != null && (kb.leftArrowKey.wasPressedThisFrame || kb.upArrowKey.wasPressedThisFrame) || gp != null && (gp.dpad.left.wasPressedThisFrame || gp.dpad.up.wasPressedThisFrame || gp.leftStick.left.wasPressedThisFrame);
            bool right = kb != null && (kb.rightArrowKey.wasPressedThisFrame || kb.downArrowKey.wasPressedThisFrame || kb.tabKey.wasPressedThisFrame) || gp != null && (gp.dpad.right.wasPressedThisFrame || gp.dpad.down.wasPressedThisFrame || gp.leftStick.right.wasPressedThisFrame);
            bool ok = kb != null && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame) || gp != null && gp.buttonSouth.wasPressedThisFrame;
            bool back = kb != null && kb.escapeKey.wasPressedThisFrame || gp != null && gp.buttonEast.wasPressedThisFrame;
            if (left) _focus = (_focus + b.Length - 1) % b.Length;
            if (right) _focus = (_focus + 1) % b.Length;
            if (ok) Press(b[_focus]);
            else if (back) Later();
        }
        catch { }
    }

    public static void OnGUI()
    {
        if (!_prompt) return;
        if (State != St.Offline && Latest == null) return;
        Cursor.visible = true; Cursor.lockState = CursorLockMode.None;
        float w = Mathf.Min(760, Screen.width - 40), h = 330, x = (Screen.width - w) / 2, y = (Screen.height - h) / 2;
        GUI.Box(new Rect(x, y, w, h), ""); GUI.Box(new Rect(x, y, w, h), ""); GUI.Box(new Rect(x, y, w, h), "");
        var title = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
        var body = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true };
        var titleText = State == St.Offline ? Loc.T("upd.titleOffline") : Loc.F("upd.title", Latest.Version);
        GUI.Label(new Rect(x + 18, y + 14, w - 36, 32), titleText, title);
        string text = State switch
        {
            St.Offline => Loc.F("upd.bodyOffline", string.IsNullOrEmpty(Detail) ? "—" : Detail),
            St.Downloading => Loc.F("upd.downloading", Progress * 100),
            St.Ready => Loc.T("upd.ready"),
            St.Failed => Loc.F("upd.failed", Detail),
            _ => Loc.F("upd.body", Plugin.Version, Latest.Version) + "\n\n" + UpdateLogic.ShortNotes(Latest.Notes, Loc.Lang),
        };
        GUI.Label(new Rect(x + 18, y + 52, w - 36, h - 120), text, body);
        var b = Buttons();
        float bw = (w - 36 - 12 * (b.Length - 1)) / b.Length, by = y + h - 58;
        for (int i = 0; i < b.Length; i++)
        {
            var bs = new GUIStyle(GUI.skin.button) { fontSize = 18, fontStyle = i == _focus ? FontStyle.Bold : FontStyle.Normal };
            var label = (i == _focus ? "› " : "") + Loc.T(b[i]) + (i == _focus ? " ‹" : "");
            var r = new Rect(x + 18 + i * (bw + 12), by, bw, 42);
            if (i == _focus) { GUI.Box(r, ""); GUI.Box(new Rect(r.x - 3, r.y - 3, r.width + 6, r.height + 6), ""); }
            if (GUI.Button(r, label, bs)) { _focus = i; Press(b[i]); }
        }
        PromptFrame = Time.frameCount;
    }
    public static int PromptFrame = -1;

    /// <summary>F7 line.</summary>
    public static Msg StatusMsg() => State switch
    {
        St.Off => new Msg("upd.st.off"),
        St.Checking => new Msg("upd.st.checking"),
        St.UpToDate => new Msg("upd.st.upToDate", Plugin.Version),
        St.Offline => new Msg("upd.st.offline"),
        St.Available => new Msg("upd.st.available", Latest?.Version ?? "?"),
        St.Downloading => new Msg("upd.downloading", Progress * 100),
        St.Ready => new Msg("upd.st.ready", Latest?.Version ?? "?"),
        St.Failed => new Msg("upd.failed", Detail),
        _ => new Msg("upd.st.idle"),
    };

    /// <summary>Self-test: press a prompt button by key.</summary>
    public static bool TestPress(string key) { if (!_prompt) return false; Press(key); return true; }
    public static string TestButtons() => string.Join(",", Buttons());
    public static int TestFocus => _focus;
}

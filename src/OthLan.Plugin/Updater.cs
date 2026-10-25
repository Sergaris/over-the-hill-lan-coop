using UnityEngine;

namespace OthLan;

/// <summary>
/// In-game update lifecycle: GitHub check via <see cref="UpdateJob"/>, state, F7 status.
/// Prompt UI is <see cref="UpdatePrompt"/> (Canvas / UiModal / UiNav).
/// </summary>
internal static class Updater
{
    public enum St { Off, Idle, Checking, UpToDate, Offline, Available, Downloading, Ready, Failed }
    public static volatile St State = St.Idle;
    public static UpdateLogic.Release Latest;
    public static string Detail = "";
    public static float Progress;
    public static bool PromptOpen => _prompt;
    public static int Checks, Fails, Started;
    public static int PromptFrame { get => UpdatePrompt.PromptFrame; set { } }
    static bool _prompt, _dismissed, _readyShown, _offlineShown;

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
        _offlineShown = false;
        UpdateJob.Check(delaySeconds);
    }

    public static void CheckNow()
    {
        Plugin.UpdateSkipped.Value = "";
        _dismissed = false; _readyShown = false; _offlineShown = false;
        if (_prompt) Close();
        CheckAsync(0);
    }

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
        UpdatePrompt.Close();
    }

    /// <summary>Button keys for the current state (single owner for Autopilot / UpdatePrompt).</summary>
    public static string[] ButtonKeys() => State switch
    {
        St.Ready => new[] { "upd.quitUpdate", "upd.later" },
        St.Failed => new[] { "upd.retry", "upd.later" },
        St.Offline => new[] { "upd.retry", "upd.later" },
        St.Downloading => new[] { "upd.later" },
        _ => new[] { "upd.install", "upd.later", "upd.skip" },
    };

    /// <summary>Self-test / Canvas click: press a prompt button by localization key.</summary>
    public static void PressKey(string key)
    {
        switch (key)
        {
            case "upd.install": Install(); break;
            case "upd.retry":
                if (State == St.Offline) CheckNow();
                else Install();
                break;
            case "upd.later": Later(); break;
            case "upd.skip": Skip(); break;
            case "upd.quitUpdate": QuitAndUpdate(); break;
        }
    }

    public static void Tick()
    {
        if (!_prompt)
        {
            if (UiModal.Current != null) return;
            if (State == St.Ready && !_readyShown && Latest != null && CoopMenu.MainMenuVisible)
            {
                if (!UiModal.TryOpen("update")) return;
                _readyShown = true; _dismissed = false;
                _prompt = true;
                UpdatePrompt.Open();
                Plugin.L.LogInfo($"[Update] ready prompt shown for {Latest.Version}");
                return;
            }
            if (State == St.Offline && !_offlineShown && !_dismissed && CoopMenu.MainMenuVisible)
            {
                if (!UiModal.TryOpen("update")) return;
                _offlineShown = true; _dismissed = false;
                _prompt = true;
                UpdatePrompt.Open();
                Plugin.L.LogInfo($"[Update] offline prompt shown ({Detail})");
                return;
            }
            if (_dismissed || State != St.Available || Latest == null || !CoopMenu.MainMenuVisible) return;
            if (!UpdateLogic.ShouldOffer(Latest.Version, Plugin.Version, Plugin.UpdateSkipped.Value)) { _dismissed = true; return; }
            if (!UiModal.TryOpen("update")) return;
            _prompt = true;
            UpdatePrompt.Open();
            Plugin.L.LogInfo($"[Update] prompt shown for {Latest.Version}");
            return;
        }
        if (!CoopMenu.MainMenuVisible && State != St.Downloading && State != St.Ready)
        {
            _prompt = false;
            UpdatePrompt.Close();
            return;
        }
        UpdatePrompt.Tick();
    }

    public static void OnGUI() => UpdatePrompt.OnGUI();

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

    public static bool TestPress(string key) { if (!_prompt) return false; PressKey(key); return true; }
    public static string TestButtons() => string.Join(",", ButtonKeys());
    public static int TestFocus => UpdatePrompt.TestFocus;
}

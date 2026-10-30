using System;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace OthLan;

/// <summary>
/// IMGUI window. F7 toggles it (type the host's Radmin IP:port here). F8 = arm host, then Create Server as usual.
/// F9 = join the configured/typed address. F10 = disarm. Keys only act while the game window is focused.
/// Join, the host query and the start watchdog live in <see cref="JoinFlow"/>.
/// </summary>
public class Overlay : MonoBehaviour
{
    public Overlay(IntPtr ptr) : base(ptr) { }

    static Overlay _inst;
    bool _show;
    bool _guiBroken;
    string _join;
    Rect _rect = new Rect(40, 40, 540, 430);
    static Application.LogCallback _logCb; // keep alive

    void Awake()
    {
        _inst = this;
        try
        {
            _logCb = DelegateSupport.ConvertDelegate<Application.LogCallback>(new Action<string, string, LogType>(OnUnityLog));
            Application.add_logMessageReceived(_logCb);
        }
        catch (Exception e) { Plugin.L.LogWarning($"[LAN] log listener unavailable: {e.Message}"); }
    }

    void OnApplicationQuit() => SessionLogs.OnQuit();

    static void OnUnityLog(string msg, string stack, LogType type)
    {
        if (msg != null && LanState.Active != LanRole.Off) NetStats.OnUnityLog(msg);
        JoinFlow.NoteUnityLog(msg, type);
    }

    internal static void Show() { if (_inst != null && !Autopilot.Active) _inst._show = true; }   // keep self-test screenshots clean
    /// <summary>0.1.9: close F7 without a keypress (e.g. client just entered the host's world).</summary>
    internal static void Hide() { if (_inst != null) _inst._show = false; }
    public static void SetShownForTest(bool v) { if (_inst != null) _inst._show = v; }

    void Update()
    {
        try { LangWatch.Tick(); } catch (Exception e) { LogOnce("lang", e); }
        try { JoinFlow.Tick(); } catch (Exception e) { LogOnce("joinflow", e); }
        try { ModLiveryGate.Tick(); } catch (Exception e) { LogOnce("modgate", e); }
        try { GatePrompt.Tick(); } catch (Exception e) { LogOnce("gateprompt", e); }
        try { JoinToast.Tick(); } catch (Exception e) { LogOnce("jointoast", e); }
        try { HostLiveryModsTick(); } catch (Exception e) { LogOnce("hostliveries", e); }
        try { NetFrame.Update(); } catch (Exception e) { LogOnce("netframe", e); }
        try { HostSlotsTick(); } catch (Exception e) { LogOnce("slots", e); }
        try { Autopilot.Tick(); } catch (Exception e) { LogOnce("autopilot", e); }
        try { CoopMenu.Tick(); } catch (Exception e) { LogOnce("coop", e); }
        try { CoopSession.Tick(); } catch (Exception e) { LogOnce("session", e); }
        try { SessionNotify.Tick(); } catch (Exception e) { LogOnce("sessionnotify", e); }
        try { Updater.Tick(); } catch (Exception e) { LogOnce("update", e); }
        try
        {
            AutoTest();
            if (!Application.isFocused) return;
            var input = BepInEx.UnityInput.Current;
            if (Plugin.ToggleOverlayKey.Value.IsDown()) { _show = !_show; Plugin.L.LogInfo($"[UI] overlay {(_show ? "shown" : "hidden")}"); }
            if (_show) { Cursor.visible = true; Cursor.lockState = CursorLockMode.None; }
            if (input.GetKeyDown(KeyCode.F8)) LanStart.ArmHostFromUi();
            if (input.GetKeyDown(KeyCode.F9)) JoinFlow.Join(_show && !string.IsNullOrWhiteSpace(_join) ? _join : Plugin.JoinAddress.Value);
            if (input.GetKeyDown(KeyCode.F10)) LanStart.Disarm();
        }
        catch (Exception e) { Plugin.L.LogError($"[UI] {e.Message}"); }
    }

    static float _nextSlotSync, _nextLiveryMods;
    /// <summary>Host: publish required Modio livery ids for joining clients (HostInfo liverymods=).</summary>
    static void HostLiveryModsTick()
    {
        if (LanState.Active != LanRole.Host) return;
        if (Time.realtimeSinceStartup < _nextLiveryMods) return;
        _nextLiveryMods = Time.realtimeSinceStartup + 2f;
        try
        {
            string csv;
#if SELFTEST
            csv = !string.IsNullOrEmpty(Autopilot.FakeLiveryMods)
                ? Autopilot.FakeLiveryMods
                : ModLiveryGateLogic.Format(ModLiveryBridge.CollectHostRequired());
#else
            csv = ModLiveryGateLogic.Format(ModLiveryBridge.CollectHostRequired());
#endif
            HostInfo.SetLiveryMods(csv);
        }
        catch (Exception e) { Plugin.L.LogWarning($"[LAN] host liverymods: {e.Message}"); }
    }

    /// <summary>Host: mark reserved slots connected / release the slots of players who left (SlotAllocator).</summary>
    static void HostSlotsTick()
    {
        if (LanState.Active != LanRole.Host) return;
        if (LanState.Slots.KickPending)
        {   // every frame, not once a second: the reconnecting client connects within ~1-2 s
            var rk = OverlandNetworkRunner.Instance?.NetworkRunner;
            foreach (var k in LanState.Slots.TakeKicks())
            {
                try { if (rk != null && rk.IsRunning && rk.IsPlayerValid(Fusion.PlayerRef.FromIndex(k))) { rk.Disconnect(Fusion.PlayerRef.FromIndex(k)); Plugin.L.LogInfo($"[LAN] slot {k}: the same player is reconnecting; dropped its old connection"); } }
                catch (Exception e) { Plugin.L.LogWarning($"[LAN] slot {k}: could not drop the old connection ({e.Message})"); }
            }
        }
        if (Time.realtimeSinceStartup < _nextSlotSync) return;
        _nextSlotSync = Time.realtimeSinceStartup + 1f;
        if (!CoopMenu.LobbyNameSet) CoopMenu.InitLobbyName();
        var r = OverlandNetworkRunner.Instance?.NetworkRunner;
        if (r == null || !r.IsRunning) return;
        var released = LanState.Slots.Sync(i => r.IsPlayerValid(Fusion.PlayerRef.FromIndex(i)), NetClock.Now);
        int cur = 0;
        for (int i = 0; i < Math.Max(LanState.EffectivePlayers, 2); i++) if (r.IsPlayerValid(Fusion.PlayerRef.FromIndex(i))) cur++;
        HostInfo.CurPlayers = Math.Max(1, cur);
        foreach (var sl in released) Plugin.L.LogInfo($"[LAN] player slot {sl} released (player left) [{LanState.Slots.Describe()}]");
        try { SessionNotify.OnSlotsReleased(released); } catch (Exception e) { LogOnce("sessionnotify-leave", e); }
    }

    static readonly System.Collections.Generic.HashSet<string> _logged = new();
    static void LogOnce(string key, Exception e) { if (_logged.Add(key)) Plugin.L.LogError($"[LAN] {key}: {e}"); }

    // ---------- UI ----------
    bool _pingBroken, _carLabelsBroken;
    void LateUpdate()
    {
        try { NetFrame.Late(); } catch (Exception e) { LogOnce("netframe-late", e); }
    }

    static float _offConfirmUntil = -1;
    static bool OffWarnShown() => LanState.Active != LanRole.Off && !CarSync.Enabled && (LanState.Active == LanRole.Host || CarSync.HostFlag == 0);
    void OnGUI()
    {
        try { NetStats.OnRepaint(); } catch { }
        if (!_pingBroken)
        {
            try { PingHud.OnGUI(_show && !_guiBroken); }
            catch (Exception e) { _pingBroken = true; Plugin.L.LogWarning($"[UI] ping label disabled: {e.Message}"); }
        }
        try { CoopMenu.OnGUI(); } catch (Exception e) { LogOnce("coop-gui", e); }
        try { ModLiveryGate.OnGUI(); } catch (Exception e) { LogOnce("modgate-gui", e); }
        try { Updater.OnGUI(); } catch (Exception e) { LogOnce("update-gui", e); }
        if (!_carLabelsBroken)
        {
            try { LinkLabels.OnGUI(); }
            catch (Exception e) { _carLabelsBroken = true; Plugin.L.LogWarning($"[UI] over-the-car link labels disabled: {e.Message}"); }
        }
        // Canvas GatePrompt first; IMGUI only if the Canvas dialog has no answer buttons yet.
        if (!_guiBroken && BuildGate.NeedsChoice && !(GatePrompt.Visible && GatePrompt.HasButtons))
        {
            try { GateDialog(); }
            catch (Exception e) { _guiBroken = true; Plugin.L.LogWarning($"[UI] gate dialog unavailable ({e.Message}); answer in F7 / Safety.UnverifiedBuildChoices"); }
        }
        if (!_show || _guiBroken) return;
        try { DrawF7(); }
        catch (Exception e) { _guiBroken = true; Plugin.L.LogWarning($"[UI] IMGUI unavailable ({e.Message}); use F8/F9/F10 + config"); }
    }

    void DrawF7()
    {
        _join ??= NetInfo.DemoIp != null ? "26.10.20.40:27015" : Plugin.JoinAddress.Value ?? "";
        var byUser = BuildGate.Outcome == GateLogic.Outcome.DisabledByUser;
        _rect.height = 430 + Mathf.Max(0, HostFollower.HostStats.Count - 2) * 18 + (byUser ? 32 : 0) + (LanState.Active == LanRole.Client ? 144 : LanState.Active == LanRole.Host ? 54 : 0) + 56
            + (OffWarnShown() ? 40 : 0);
        GUI.Box(_rect, Loc.F("f7.title", Plugin.Version));
        float x = _rect.x + 10, y = _rect.y + 25, w = _rect.width - 20;
        DrawF7Header(ref x, ref y, w, byUser);
        DrawF7Session(ref x, ref y, w);
        if (LanState.Active == LanRole.Client) DrawF7Client(ref x, ref y, w);
        else DrawF7Host(ref x, ref y, w);
        DrawF7Updater(ref x, ref y, w);
    }

    void DrawF7Header(ref float x, ref float y, float w, bool byUser)
    {
        var gate = BuildGate.Status();
        GUI.Label(new Rect(x, y, w, 20), LanState.Disabled ? gate : gate.Length > 0 && LanState.Active == LanRole.Off && LanState.Armed == LanRole.Off ? gate : RoleText());
        y += 22;
        if (byUser)
        {
            if (GUI.Button(new Rect(x, y, w, 26), Loc.T("f7.enableAnyway"))) BuildGate.UserChoice(true, "F7 button");
            y += 32;
        }
        GUI.Label(new Rect(x, y, w, 20), Loc.F("f7.myIp", NetInfo.DisplayIp() ?? Loc.T("f7.ipNotFound"), Plugin.Port.Value, Plugin.PlayerCount.Value));
        y += 22;
        GUI.Label(new Rect(x, y, w, 44), NetInfo.Mask(Loc.F("f7.status", LanState.LastStatus)));
        y += 46;
        if (GUI.Button(new Rect(x, y, 245, 28), Loc.T("f7.host"))) LanStart.ArmHostFromUi();
        if (GUI.Button(new Rect(x + 255, y, 245, 28), Loc.T("f7.off"))) LanStart.Disarm();
        y += 36;
        GUI.Label(new Rect(x, y, w, 20), Loc.T("f7.hostAddress"));
        y += 20;
        _join = GUI.TextField(new Rect(x, y, 300, 24), _join ?? "");
        if (GUI.Button(new Rect(x + 310, y, 190, 24), Loc.T("f7.join"))) JoinFlow.Join(_join);
        y += 30;
        var same = Loc.T("f7.sameVersions");
        var sameH = Mathf.Max(20f, GUI.skin.label.CalcHeight(new GUIContent(same), w));
        GUI.Label(new Rect(x, y, w, sameH), same);
        y += sameH + 4;
    }

    void DrawF7Session(ref float x, ref float y, float w)
    {
        var per = string.Join("  ", System.Linq.Enumerable.Select(PingHud.PerSlotMs, kv => Loc.F("f7.playerPing", kv.Key, double.IsNaN(kv.Value) ? "--" : kv.Value.ToString("0"))));
        var pingTxt = double.IsNaN(PingHud.SmoothedMs) || PingHud.SmoothedMs <= 0 ? "--" : Loc.F("unit.ms", PingHud.SmoothedMs);
        var pingNote = PingHud.TimedOut ? "  " + Loc.T("f7.noReply") : PingHud.LossPct >= 1 ? "  " + Loc.F("f7.loss", PingHud.LossPct) : "";
        GUI.Label(new Rect(x, y, w, 20), LanState.Active == LanRole.Off ? Loc.T("f7.pingOff")
            : Loc.F("f7.ping", $"{pingTxt}{pingNote}{(LanState.Active == LanRole.Host && per.Length > 0 ? "   (" + per + ")" : "")}"));
        y += 20;
        GUI.Label(new Rect(x, y, w, 20), PingHud.FusionCompare.ToString());
        y += 22;
        string cs;
        if (LanState.Active == LanRole.Client) cs = HostFollower.ClientStat.ToString();
        else if (LanState.Active == LanRole.Host)
            cs = !CarSync.HostEnabled ? Loc.T("cs.hostOff")
               : HostFollower.HostStats.Count == 0 ? Loc.T("cs.hostWaiting")
               : string.Join("\n", System.Linq.Enumerable.Select(HostFollower.HostStats.Values, m => m.ToString()));
        else cs = CarSync.LocalSetting ? Loc.T("cs.willBeOn") : Loc.T("cs.willBeOff");
        if (string.IsNullOrEmpty(cs)) cs = "--";
        var csH = Mathf.Max(48f, 18f * (1 + cs.Split('\n').Length) + 4f);
        GUI.Label(new Rect(x, y, w, csH), Loc.F("cs.caption", cs));
        y += csH + 2;
        if (OffWarnShown()) { GUI.Label(new Rect(x, y, w, 40), Loc.T("cs.offWarn")); y += 40; }
    }

    void DrawF7Client(ref float x, ref float y, float w)
    {
        var hs = CarSync.HostFlag < 0 ? "?" : CarSync.HostFlag == 1 ? Loc.T("common.on") : Loc.T("common.off");
        var prev = GUI.enabled; GUI.enabled = false;
        GUI.Button(new Rect(x, y, w, 26), Loc.F("cs.hostControls", hs));
        GUI.enabled = prev;
        y += 30;
        var st = OwnCar.State == "client" ? Loc.T("own.client") : OwnCar.State == "soft" ? Loc.T("own.soft") : OwnCar.State == "suspend" ? Loc.T("own.suspend") : Loc.T("own.none");
        GUI.Label(new Rect(x, y, w, 20), Loc.F("own.caption", st));
        y += 20;
        if (GUI.Button(new Rect(x, y, w, 26), Plugin.OwnLocalClock.Value ? Loc.T("own.localOn") : Loc.T("own.localOff")))
            OwnLocal.SetEnabled(!Plugin.OwnLocalClock.Value, "F7 button");
        y += 30;
        if (GUI.Button(new Rect(x, y, w, 26), Plugin.RemoteInterpolate.Value ? Loc.T("own.remoteOn") : Loc.T("own.remoteOff")))
            RemoteSmooth.SetInterpolate(!Plugin.RemoteInterpolate.Value, "F7 button");
        y += 30;
        if (GUI.Button(new Rect(x, y, w, 26), Plugin.OwnFreezeSmoothing.Value ? Loc.T("own.freezeOn") : Loc.T("own.freezeOff")))
            OwnRender.SetEnabled(!Plugin.OwnFreezeSmoothing.Value, "F7 button");
        y += 30;
        if (GUI.Button(new Rect(x, y, w, 26), Loc.T("f7.exportDiag")))
            Diagnostics.ExportZip("F7 button");
        y += 30;
        GUI.Label(new Rect(x, y, w, 20), Loc.F("f7.netStats", NetStats.Gaps.Over500ms, NetStats.InputDropped, NetStats.Own.Over10cm));
        y += 22;
    }

    void DrawF7Host(ref float x, ref float y, float w)
    {
        var confirming = CarSync.LocalSetting && Time.unscaledTime < _offConfirmUntil;
        if (GUI.Button(new Rect(x, y, w, 26), confirming ? Loc.T("cs.confirmOff") : CarSync.LocalSetting ? Loc.T("cs.toggleOn") : Loc.T("cs.toggleOff")))
        {
            if (CarSync.LocalSetting && !confirming) _offConfirmUntil = Time.unscaledTime + 4f;
            else { _offConfirmUntil = -1; CarSync.UserToggle("F7 button"); }
        }
        if (LanState.Active == LanRole.Host) { y += 30; GUI.Label(new Rect(x, y, w, 20), Loc.F("f7.netStats", "-", "-", NetStats.Own.Over10cm)); }
        y += 30;
        if (LanState.Active == LanRole.Host || LanState.Active == LanRole.Off)
        {
            if (GUI.Button(new Rect(x, y, w, 26), Loc.T("f7.exportDiag")))
                Diagnostics.ExportZip("F7 button");
            y += 30;
        }
    }

    void DrawF7Updater(ref float x, ref float y, float w)
    {
        GUI.Label(new Rect(x, y, w, 20), Loc.F("upd.f7", Updater.StatusMsg()));
        y += 22;
        if (GUI.Button(new Rect(x, y, 245, 26), Plugin.UpdateCheck.Value ? Loc.T("upd.toggleOn") : Loc.T("upd.toggleOff")))
        {
            Plugin.UpdateCheck.Value = !Plugin.UpdateCheck.Value;
            Plugin.L.LogInfo($"[Update] check for updates {(Plugin.UpdateCheck.Value ? "on" : "off")} (F7)");
            if (!Plugin.UpdateCheck.Value) Updater.State = Updater.St.Off;
        }
        if (GUI.Button(new Rect(x + 255, y, 245, 26), Updater.State == Updater.St.Available ? Loc.T("upd.install") : Loc.T("upd.checkNow")))
        {
            if (Updater.State == Updater.St.Available) Updater.Install();
            else Updater.CheckNow();
        }
    }

    /// <summary>One-time warning for a game build that is not in the manifest but passed the code/layout checks.</summary>
    void GateDialog()
    {
        Cursor.visible = true; Cursor.lockState = CursorLockMode.None;
        float w = 660, h = 290, x = (Screen.width - w) / 2, y = (Screen.height - h) / 2;
        // Bilingual on purpose when the language is not known yet; once the game's language is read, only that one.
        var both = !LangWatch.GameKnown;
        GUI.Box(new Rect(x, y, w, h), both ? Loc.En("dlg.title", Plugin.Version) + "  /  " + Loc.TIn("ru", "dlg.title").Replace("{0}", Plugin.Version) : Loc.F("dlg.title", Plugin.Version));
        GUI.Box(new Rect(x, y, w, h), "");   // second layer: opaque enough over the menu
        float cx = x + 16, cw = w - 32, cy = y + 28;
        string Two(string key) => both ? Loc.TIn("ru", key) + "\n" + Loc.TIn("en", key) : Loc.T(key);
        if (both)
        {
            GUI.Label(new Rect(cx, cy, cw, 70), Loc.TIn("ru", "dlg.body")); cy += 72;
            GUI.Label(new Rect(cx, cy, cw, 70), Loc.TIn("en", "dlg.body")); cy += 80;
        }
        else { GUI.Label(new Rect(cx, cy, cw, 140), Loc.T("dlg.body")); cy += 152; }
        if (GUI.Button(new Rect(cx, cy, cw / 2 - 6, 36), Two("dlg.continue"))) BuildGate.UserChoice(true, "dialog");
        if (GUI.Button(new Rect(cx + cw / 2 + 6, cy, cw / 2 - 6, 36), Two("dlg.disable"))) BuildGate.UserChoice(false, "dialog");
        GateDialogFrame = Time.frameCount;
    }
    public static int GateDialogFrame = -1;

    static string RoleText()
    {
        if (LanState.Active == LanRole.Host) return Loc.T("role.host");
        if (LanState.Active == LanRole.Client) return Loc.T("role.client");
        if (LanState.Armed == LanRole.Host) return Loc.T("role.armedHost");
        if (LanState.Armed == LanRole.Client) return Loc.T("role.armedClient");
        return Loc.T("role.off");
    }

    // ---------- dev ----------
    public static void RearmAutoTestForRejoin() { if (_inst != null) { _inst._autoDone = false; _inst._instTried = false; _inst._instReadyAt = Time.realtimeSinceStartup + 15f; } }
    bool _autoDone, _instTried;
    float _instReadyAt;
    void AutoTest()
    {
        var mode = Autopilot.Active ? Autopilot.Mode : Plugin.AutoTest.Value;
        if (_autoDone || string.IsNullOrEmpty(mode) || LanState.Disabled || mode == "coop") return;
        var delay = Autopilot.Active ? Autopilot.Delay : Plugin.AutoTestDelay.Value;
        if (Time.realtimeSinceStartup < delay) return;
        if (OverlandNetworkRunner.Instance == null)
        {
            if (Autopilot.Active && !_instTried)
            {
                _instTried = true;
                try { OverlandNetworkRunner.Instantiate(); Plugin.L.LogWarning("[AutoTest] OverlandNetworkRunner.Instantiate() (as the server browser would)"); }
                catch (Exception e) { Plugin.L.LogError($"[AutoTest] Instantiate: {e}"); }
            }
            return;
        }
        if (Autopilot.Active && Time.realtimeSinceStartup < _instReadyAt) return;
        if (Autopilot.Active && _instReadyAt == 0) { _instReadyAt = Time.realtimeSinceStartup + 5f; return; }
        _autoDone = true;
        Plugin.L.LogWarning($"[AutoTest] Debug.AutoTest={mode}: starting without user input (dev only)");
        try
        {
            if (mode == "host")
            {
                LanStart.ArmHostFromUi();
                LanStart.PushStart(Fusion.GameMode.Host, "LAN host autotest", LanStart.FindArea(Plugin.JoinArea.Value, out _));
            }
            else if (mode.StartsWith("join:")) JoinFlow.Join(mode.Substring(5));
        }
        catch (Exception e) { Plugin.L.LogError($"[AutoTest] {e}"); JoinFlow.Cancel(); }
    }
}

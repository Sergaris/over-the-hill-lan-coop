using System;
using FunCore.Managers;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace OthLan;

/// <summary>
/// IMGUI window. F7 toggles it (type the host's Radmin IP:port here). F8 = arm host, then Create Server as usual.
/// F9 = join the configured/typed address. F10 = disarm. Keys only act while the game window is focused.
/// A watchdog returns the game to the main menu (the game's own QuitToMenuAsync) when a LAN start fails.
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

    const float TimeoutBeforeStartGame = 90f; // region fetch over a flaky link can take a while
    const float TimeoutClientConnect = 25f;
    const float TimeoutHostStart = 60f;

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

    static void OnUnityLog(string msg, string stack, LogType type)
    {
        if (msg != null && LanState.Active != LanRole.Off) NetStats.OnUnityLog(msg);
        if (!LanState.Watching || msg == null) return;
        if (msg.Contains("StartGameAsync failed") || msg.Contains("StartGame failed") ||
            (type == LogType.Exception && LanState.StartGameSeenAt < 0))
            LanState.FailureText = msg.Length > 200 ? msg[..200] : msg;
    }

    internal static void Show() { if (_inst != null && !Autopilot.Active) _inst._show = true; }   // keep self-test screenshots clean
    /// <summary>0.1.9: close F7 without a keypress (e.g. client just entered the host's world).</summary>
    internal static void Hide() { if (_inst != null) _inst._show = false; }
    public static void SetShownForTest(bool v) { if (_inst != null) _inst._show = v; }

    void Update()
    {
        try { LangWatch.Tick(); } catch (Exception e) { LogOnce("lang", e); }
        try { PollQuery(); } catch (Exception e) { _querying = false; Plugin.L.LogError($"[LAN] join: {e}"); }
        try { ModLiveryGate.Tick(); } catch (Exception e) { LogOnce("modgate", e); }
        try { HostLiveryModsTick(); } catch (Exception e) { LogOnce("hostliveries", e); }
        try { Watchdog(); } catch (Exception e) { Plugin.L.LogError($"[LAN] watchdog: {e}"); }
        try { PingHud.Tick(); } catch (Exception e) { LogOnce("ping", e); }
        try { CarSync.Tick(); } catch (Exception e) { LogOnce("carsync", e); }
        try { OwnCar.Tick(); } catch (Exception e) { LogOnce("owncar", e); }
        try { FreezeWatch.Tick(); } catch (Exception e) { LogOnce("freeze", e); }
        try { Diagnostics.Tick(); } catch (Exception e) { LogOnce("diag", e); }
        try { NetStats.Tick(); } catch (Exception e) { LogOnce("netstats", e); }
        try { HostSlotsTick(); } catch (Exception e) { LogOnce("slots", e); }
        try { Autopilot.Tick(); } catch (Exception e) { LogOnce("autopilot", e); }
        try { CoopMenu.Tick(); } catch (Exception e) { LogOnce("coop", e); }
        try { Updater.Tick(); } catch (Exception e) { LogOnce("update", e); }
        try
        {
            AutoTest();
            if (!Application.isFocused) return;
            var input = BepInEx.UnityInput.Current;
            if (Plugin.ToggleOverlayKey.Value.IsDown()) { _show = !_show; Plugin.L.LogInfo($"[UI] overlay {(_show ? "shown" : "hidden")}"); }
            if (_show) { Cursor.visible = true; Cursor.lockState = CursorLockMode.None; }
            if (input.GetKeyDown(KeyCode.F8)) ArmHost();
            if (input.GetKeyDown(KeyCode.F9)) Join(_show && !string.IsNullOrWhiteSpace(_join) ? _join : Plugin.JoinAddress.Value);
            if (input.GetKeyDown(KeyCode.F10)) Disarm();
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
        var released = LanState.Slots.Sync(i => r.IsPlayerValid(Fusion.PlayerRef.FromIndex(i)), HostPings.Now);
        int cur = 0;
        for (int i = 0; i < Math.Max(LanState.EffectivePlayers, 2); i++) if (r.IsPlayerValid(Fusion.PlayerRef.FromIndex(i))) cur++;
        HostInfo.CurPlayers = Math.Max(1, cur);
        foreach (var sl in released) Plugin.L.LogInfo($"[LAN] player slot {sl} released (player left) [{LanState.Slots.Describe()}]");
    }

    static readonly System.Collections.Generic.HashSet<string> _logged = new();
    static void LogOnce(string key, Exception e) { if (_logged.Add(key)) Plugin.L.LogError($"[LAN] {key}: {e}"); }

    // ---------- failure handling ----------
    static void BeginWatch(LanRole role)
    {
        LanState.Watching = true; LanState.WatchRole = role;
        LanState.WatchStart = Time.realtimeSinceStartup; LanState.StartGameSeenAt = -1; LanState.FailureText = null;
    }

    void Watchdog()
    {
        if (!LanState.Watching) return;
        var now = Time.realtimeSinceStartup;
        if (LanState.FailureText != null) { Recover(new Msg("recover.gameSaid", LanState.FailureText)); return; }
        if (LanState.StartGameSeenAt < 0)
        {
            if (now - LanState.WatchStart > TimeoutBeforeStartGame) Recover(new Msg("recover.netNotStarted", TimeoutBeforeStartGame));
            return;
        }
        var runner = OverlandNetworkRunner.Instance?.NetworkRunner;
        if (LanState.WatchRole == LanRole.Client)
        {
            if (runner != null && runner.IsConnectedToServer)
            {
                LanState.Watching = false; LanState.Status("status.connected", $"{LanState.TargetIp}:{LanState.TargetPort}");
                Plugin.L.LogInfo("[LAN] " + LanState.LastStatus.En());
            }
            else if (now - LanState.StartGameSeenAt > TimeoutClientConnect)
                Recover(new Msg("recover.clientTimeout", $"{LanState.TargetIp}:{LanState.TargetPort}", TimeoutClientConnect));
        }
        else if (runner != null && runner.IsRunning)
        {
            LanState.Watching = false; LanState.Status("status.hosting", Plugin.Port.Value);
            Plugin.L.LogInfo("[LAN] host running");
        }
        else if (now - LanState.StartGameSeenAt > TimeoutHostStart) Recover(new Msg("recover.hostTimeout", TimeoutHostStart));
    }

    static void Recover(Msg why)
    {
        LanState.Watching = false;
        LanState.Armed = LanRole.Off; LanState.Active = LanRole.Off; HostInfo.Clear();
        LanState.Status("recover.status", why);
        Plugin.L.LogError("[LAN] " + LanState.LastStatus.En());
        Show();
        try
        {
            var onr = OverlandNetworkRunner.Instance;
            if (onr == null) { Plugin.L.LogError("[LAN] recover: no OverlandNetworkRunner"); return; }
            // The game's own path back to the menu (used on shutdown/disconnect); shows its "unable to start" notice.
            onr.QuitToMenuAsync(MainMenuContext.Reason.UnableToStartGame, "OTH-LAN: " + why);
            Plugin.L.LogInfo("[LAN] QuitToMenuAsync(UnableToStartGame) called");
        }
        catch (Exception e) { Plugin.L.LogError($"[LAN] recover failed: {e}"); }
    }

    // ---------- UI ----------
    bool _pingBroken, _carLabelsBroken;
    void LateUpdate()
    {
        try { RemoteSmooth.LateTick(); } catch (Exception e) { LogOnce("remotesmooth", e); }
        try { RemoteVisuals.LateTick(); } catch (Exception e) { LogOnce("remotevisuals", e); }
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
        if (!_guiBroken && BuildGate.NeedsChoice)
        {
            try { GateDialog(); }
            catch (Exception e) { _guiBroken = true; Plugin.L.LogWarning($"[UI] gate dialog unavailable ({e.Message}); answer in F7 is impossible, see cfg Safety.UnverifiedBuildChoices"); }
        }
        if (!_show || _guiBroken) return;
        try
        {
            _join ??= NetInfo.DemoIp != null ? "26.10.20.40:27015" : Plugin.JoinAddress.Value ?? "";
            var byUser = BuildGate.Outcome == GateLogic.Outcome.DisabledByUser;
            _rect.height = 430 + Mathf.Max(0, CarSync.HostStats.Count - 2) * 18 + (byUser ? 32 : 0) + (LanState.Active == LanRole.Client ? 144 : LanState.Active == LanRole.Host ? 54 : 0) + 56
                + (OffWarnShown() ? 40 : 0);   // client: local + remote + freeze + export; host: export
            GUI.Box(_rect, Loc.F("f7.title", Plugin.Version));
            float x = _rect.x + 10, y = _rect.y + 25, w = _rect.width - 20;
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
            if (GUI.Button(new Rect(x, y, 245, 28), Loc.T("f7.host"))) ArmHost();
            if (GUI.Button(new Rect(x + 255, y, 245, 28), Loc.T("f7.off"))) Disarm();
            y += 36;
            GUI.Label(new Rect(x, y, w, 20), Loc.T("f7.hostAddress"));
            y += 20;
            _join = GUI.TextField(new Rect(x, y, 300, 24), _join ?? "");
            if (GUI.Button(new Rect(x + 310, y, 190, 24), Loc.T("f7.join"))) Join(_join);
            y += 30;
            var same = Loc.T("f7.sameVersions");
            var sameH = Mathf.Max(20f, GUI.skin.label.CalcHeight(new GUIContent(same), w));   // long translations wrap: no overlap
            GUI.Label(new Rect(x, y, w, sameH), same);
            y += sameH + 4;
            var per = string.Join("  ", System.Linq.Enumerable.Select(PingHud.PerSlotMs, kv => Loc.F("f7.playerPing", kv.Key, double.IsNaN(kv.Value) ? "--" : kv.Value.ToString("0"))));
            var pingTxt = double.IsNaN(PingHud.SmoothedMs) || PingHud.SmoothedMs <= 0 ? "--" : Loc.F("unit.ms", PingHud.SmoothedMs);
            var pingNote = PingHud.TimedOut ? "  " + Loc.T("f7.noReply") : PingHud.LossPct >= 1 ? "  " + Loc.F("f7.loss", PingHud.LossPct) : "";
            GUI.Label(new Rect(x, y, w, 20), LanState.Active == LanRole.Off ? Loc.T("f7.pingOff")
                : Loc.F("f7.ping", $"{pingTxt}{pingNote}{(LanState.Active == LanRole.Host && per.Length > 0 ? "   (" + per + ")" : "")}"));
            y += 20;
            GUI.Label(new Rect(x, y, w, 20), PingHud.FusionCompare.ToString());
            y += 22;
            string cs;
            if (LanState.Active == LanRole.Client) cs = CarSync.ClientStat.ToString();
            else if (LanState.Active == LanRole.Host)
                cs = !CarSync.HostEnabled ? Loc.T("cs.hostOff")
                   : CarSync.HostStats.Count == 0 ? Loc.T("cs.hostWaiting")
                   : string.Join("\n", System.Linq.Enumerable.Select(CarSync.HostStats.Values, m => m.ToString()));
            else cs = CarSync.LocalSetting ? Loc.T("cs.willBeOn") : Loc.T("cs.willBeOff");
            if (string.IsNullOrEmpty(cs)) cs = "--";
            var csLines = 1 + cs.Split('\n').Length;          // first line wraps after the caption
            var csH = Mathf.Max(48f, 18f * csLines + 4f);
            GUI.Label(new Rect(x, y, w, csH), Loc.F("cs.caption", cs));
            y += csH + 2;
            if (OffWarnShown())
            { GUI.Label(new Rect(x, y, w, 40), Loc.T("cs.offWarn")); y += 40; }
            if (LanState.Active == LanRole.Client)
            {
                var hs = CarSync.HostFlag < 0 ? "?" : CarSync.HostFlag == 1 ? Loc.T("common.on") : Loc.T("common.off");
                var prev = GUI.enabled; GUI.enabled = false;
                GUI.Button(new Rect(x, y, w, 26), Loc.F("cs.hostControls", hs));
                GUI.enabled = prev;
                y += 30;
                var st = OwnCar.State == "client" ? Loc.T("own.client") : OwnCar.State == "soft" ? Loc.T("own.soft") : OwnCar.State == "suspend" ? Loc.T("own.suspend") : Loc.T("own.none");
                GUI.Label(new Rect(x, y, w, 20), Loc.F("own.caption", st));
                y += 20;
                // 0.1.8.1: the client's own toggle is back (experimental): OwnLocalClock, applied live (OwnLocal.SetEnabled
                // puts back a pending skipped tick and resets the local clock + render interpolation).
                if (GUI.Button(new Rect(x, y, w, 26), Plugin.OwnLocalClock.Value ? Loc.T("own.localOn") : Loc.T("own.localOff")))
                    OwnLocal.SetEnabled(!Plugin.OwnLocalClock.Value, "F7 button");
                y += 30;
                // 0.1.9 U0: live A/B for RemoteInterpolate (was cfg-only; TuneProxy ran once per car).
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
            else
            {
                // Turning compensation OFF lands everyone in vanilla Fusion (host-authoritative cars: thrown back by
                // metres under loss, see DEV-0.1.8-LOG "Moment 15:06"): needs a second click within 4 s.
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
            // 0.1.8 updates
            GUI.Label(new Rect(x, y, w, 20), Loc.F("upd.f7", Updater.StatusMsg()));
            y += 22;
            if (GUI.Button(new Rect(x, y, 245, 26), Plugin.UpdateCheck.Value ? Loc.T("upd.toggleOn") : Loc.T("upd.toggleOff")))
            { Plugin.UpdateCheck.Value = !Plugin.UpdateCheck.Value; Plugin.L.LogInfo($"[Update] check for updates {(Plugin.UpdateCheck.Value ? "on" : "off")} (F7)"); if (!Plugin.UpdateCheck.Value) Updater.State = Updater.St.Off; }
            if (GUI.Button(new Rect(x + 255, y, 245, 26), Updater.State == Updater.St.Available ? Loc.T("upd.install") : Loc.T("upd.checkNow")))
            { if (Updater.State == Updater.St.Available) Updater.Install(); else Updater.CheckNow(); }
        }
        catch (Exception e) { _guiBroken = true; Plugin.L.LogWarning($"[UI] IMGUI unavailable ({e.Message}); use F8/F9/F10 + config"); }
    }

    /// <summary>One-time warning for a game build that is not in the manifest but passed the code/layout checks.</summary>
    void GateDialog()
    {
        Cursor.visible = true; Cursor.lockState = CursorLockMode.None;
        float w = 660, h = 250, x = (Screen.width - w) / 2, y = (Screen.height - h) / 2;
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

    /// <summary>AreaInfo ScriptableObject by asset name (null name = first loaded).
    /// 0.1.8 (bug: "the host is on R1A3_Ridge, which you do not have (you have: )"): only AreaInfos already in memory were
    /// searched; they are loaded when the game's server browser / area selection was opened. A client joining from our
    /// Local Co-op screen right after start had none. The game's own resolver (BenchmarkBootRunner.ResolveArea: loads the
    /// RegionsList addressable and looks the name up) used to be a self-test-only fallback, which is why the self-tests
    /// passed. It is now always used.</summary>
    internal static AreaInfo FindArea(string name, out string available)
    {
        var names = new System.Collections.Generic.List<string>();
        AreaInfo first = null, hit = null;
        void Scan()
        {
            names.Clear(); first = null; hit = null;
            foreach (var a in Resources.FindObjectsOfTypeAll<AreaInfo>())
            {
                if (a == null) continue;
                names.Add(a.name);
                first ??= a;
                if (!string.IsNullOrEmpty(name) && a.name == name) hit = a;
            }
        }
        Scan();
        var res = string.IsNullOrEmpty(name) ? first : hit;
        if (res == null)
        {
            var want = string.IsNullOrEmpty(name) ? (Autopilot.Active ? Autopilot.Area : null) : name;
            try
            {
                if (want != null)
                {
                    var m = typeof(BenchmarkBootRunner).GetMethod("ResolveArea", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    res = (AreaInfo)m.Invoke(null, new object[] { want });
                    Plugin.L.LogInfo($"[LAN] area '{want}' not loaded yet: the game's resolver (RegionsList) -> {(res != null ? res.name : "null")}");
                }
            }
            catch (Exception e) { Plugin.L.LogError($"[LAN] ResolveArea('{want}'): {e}"); }
            if (res == null) Scan();   // the resolver loaded the regions list: report what exists now
        }
        available = names.Count > 0 ? string.Join(", ", names) : "-";
        return res;
    }

    static void ArmHost()
    {
        if (LanState.Disabled) { LanState.LastStatus = BuildGate.StatusMsg(); Show(); return; }
        LanState.Armed = LanRole.Host; LanState.HostPlayers = 0;
        LanState.Status("status.armedHost", $"{NetInfo.DisplayIp() ?? "<IP>"}:{Plugin.Port.Value}");
        Plugin.L.LogInfo("[LAN] " + LanState.LastStatus.En());
        Show();
    }

    static void Disarm()
    {
        LanState.Armed = LanRole.Off; LanState.Watching = false; _querying = false; HostInfo.Clear();
        LanState.Status("status.off");
        Plugin.L.LogInfo("[LAN] disarmed");
    }

    /// <summary>Starts the game's own StateStartGame flow with a minimal, valid GameSetupContext.</summary>
    static void PushStart(Fusion.GameMode mode, string roomName, AreaInfo area)
    {
        var onr = OverlandNetworkRunner.Instance ?? throw new InvalidOperationException("game network runner not ready (wait for the main menu)");
        if (area == null) throw new InvalidOperationException("no AreaInfo loaded yet (wait for the main menu)");
        string region = null;
        try { region = onr.ConnectedRegion; }
        catch (Exception e) { Plugin.L.LogWarning($"[LAN] ConnectedRegion unavailable ({e.GetType().Name}); using none"); }
        var ctx = new GameSetupContext
        {
            GameMode = mode,
            RoomName = roomName,
            RoomCode = "",
            ServerRegion = region,  // equal region => no ChangeLobbyRegion round-trip
            MaxPlayers = LanState.EffectivePlayers,
            // StartGameAsync dereferences both (AreaInfo.sceneReference + inventory check; RoomProperties in
            // GetSessionProperties): null => NullReferenceException and a stuck UI (seen in v0.1.0).
            // 0.1.8: the game's own mods flag (OverlandNetworkRunner.ModsAllowed reads it; false = "mods are disabled in
            // this session"). Host: [LocalCoop] AllowMods; client: the host's value from its session properties.
            RoomProperties = new OverlandRoomProperties { AllowMods = mode == Fusion.GameMode.Client ? _joinAllowMods : Plugin.AllowMods.Value },
            AreaInfo = area,
        };
        Plugin.L.LogInfo($"[LAN] start ctx built");
        Plugin.L.LogInfo($"[LAN] start ctx: mode={mode} area={area.name} players={LanState.EffectivePlayers} region={ctx.ServerRegion ?? "null"} allowMods={ctx.RoomProperties.AllowMods}");
        BeginWatch(mode == Fusion.GameMode.Client ? LanRole.Client : LanRole.Host);
        HybridSingleton<GameStateManager>.Instance.PushState(new StateStartGame(ctx));
    }

    internal static void Join(string endpoint)
    {
        if (LanState.Disabled) { LanState.LastStatus = BuildGate.StatusMsg(); Show(); return; }
        if (LanState.Watching || _querying) { LanState.Status("status.joinBusy"); Show(); return; }
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Trim().StartsWith("26.0.0.1") ||
            !LanState.TryParseEndpoint(endpoint, out var ip, out var port))
        {
            LanState.LastStatus = string.IsNullOrWhiteSpace(endpoint) ? new Msg("status.noAddress") : new Msg("status.badAddress", endpoint);
            Plugin.L.LogWarning("[LAN] " + LanState.LastStatus.En());
            Show();   // never start a join without a valid address
            return;
        }
        if (OverlandNetworkRunner.Instance == null) { LanState.Status("status.waitMenu"); Show(); return; }
        if (!Autopilot.Active) Plugin.JoinAddress.Value = $"{ip}:{port}";   // self-test instances share one config file
        LanState.TargetIp = ip; LanState.TargetPort = port;
        // Ask the host which area/session it runs before starting anything (HostInfo, UDP port+1).
        _queryIp = ip; _queryPort = port; _queryResult = null; _queryError = null; _queryDone = false; _querying = true;
        LanState.Status("status.querying", $"{ip}:{port + 1}");
        Plugin.L.LogInfo("[LAN] " + LanState.LastStatus.En());
        // 0.1.9: do not auto-open F7 on join (Local Co-op / F9 / freeze-rejoin). Errors below still Show();
        // successful world entry Hide()s via FusionHooks → OnClientEnteredWorld.
        LanState.HostPlayers = 0;
        var hint = FusionHooks.PickClientSlot();   // main thread (reads the Radmin IP); the host prefers it if free
        new System.Threading.Thread(() =>
        {
            var r = HostInfo.Query(ip, port + 1, out var err);
            int? slot = null; string slotErr = null;
            if (r != null && r.Mod == Plugin.Version)   // same mod version: ask the host for a player slot (0.1.6)
                slot = HostInfo.RequestSlot(ip, port + 1, LanState.ClientId, hint, out slotErr);
            _slotResult = slot; _slotError = slotErr;
            _queryResult = r; _queryError = err; _queryDone = true;
        }) { IsBackground = true, Name = "OTH-LAN query" }.Start();
    }

    static volatile bool _querying, _queryDone;
    static volatile HostInfo.Info _queryResult;
    static volatile string _queryError;
    static int? _slotResult; static string _slotError;
    static bool _joinAllowMods = true;
    static string _queryIp; static int _queryPort;

    /// <summary>Main thread: finish a Join once the host answered (or not).</summary>
    static void PollQuery()
    {
        if (!_querying || !_queryDone) return;
        _querying = false;
        var ip = _queryIp; var port = (ushort)_queryPort;
        var info = _queryResult;
        string areaName;
        if (info == null)
        {
            if (string.IsNullOrEmpty(Plugin.JoinArea.Value))
            {
                LanState.Status("status.hostSilent", ip, _queryError, Plugin.Version, $"{port}-{port + 1}");
                Plugin.L.LogWarning("[LAN] " + LanState.LastStatus.En());
                Show(); return;
            }
            areaName = Plugin.JoinArea.Value;
            _joinAllowMods = Plugin.AllowMods.Value;
            LanState.HostPlayers = 0; LanState.AssignedSlot = -1;
            Plugin.L.LogWarning($"[LAN] host info unavailable ({_queryError}); using config JoinArea={areaName}");
        }
        else
        {
            Plugin.L.LogInfo($"[LAN] host info: mod={info.Mod} build={info.Build} players={info.Players} area={info.Area} props={string.Join(", ", info.Props)}");
            if (info.Mod != Plugin.Version)
            { LanState.Status("status.modMismatch", info.Mod, Plugin.Version); Show(); return; }
            if (!GateLogic.SameBuild(info.Build, info.GaHash, BuildGate.BuildKey, BuildGate.GaHash))
            { LanState.Status("status.gameMismatch", info.Build, BuildGate.BuildKey); Plugin.L.LogWarning("[LAN] " + LanState.LastStatus.En()); Show(); return; }
            if (string.IsNullOrEmpty(info.Area))
            { LanState.Status("status.noArea"); Show(); return; }
            areaName = info.Area;
            LanState.HostPlayers = info.Players;
            if (_slotResult == -1)
            { LanState.Status("status.full", info.Players); Plugin.L.LogWarning("[LAN] " + LanState.LastStatus.En()); Show(); return; }
            LanState.AssignedSlot = _slotResult ?? -1;
            Plugin.L.LogInfo(_slotResult.HasValue ? $"[LAN] host assigned player slot {_slotResult}" : $"[LAN] no slot from the host ({_slotError}); using the IP-derived slot");
            if (info.CarSync >= 0) CarSync.OnHostFlags(info.CarSync, ClientLink.Now);
            _joinAllowMods = RoomPropsLogic.AllowMods(info.Props) ?? Plugin.AllowMods.Value;
            Plugin.L.LogInfo($"[LAN] host session: mods {(_joinAllowMods ? "allowed" : "off")}");
            // Pre-join Modio livery gate: host advertises liverymods=…; client must have them locally.
            if (_joinAllowMods && !string.IsNullOrEmpty(info.LiveryMods))
            {
                var required = ModLiveryGateLogic.Parse(info.LiveryMods);
                var missing = ModLiveryBridge.MissingOf(required);
                if (missing.Count > 0)
                {
                    var areaPending = areaName;
                    var ipP = ip; var portP = port;
                    Plugin.L.LogInfo($"[LAN] missing host liveries: {ModLiveryGateLogic.Format(missing)}");
                    LanState.Status("status.modsMissing", missing.Count);
                    ModLiveryGate.Begin(required, missing,
                        () => FinishClientJoin(ipP, portP, areaPending),
                        () =>
                        {
                            LanState.Armed = LanRole.Off; LanState.Watching = false;
                            LanState.Status("status.modsCancelled");
                            Show();
                        });
                    return;
                }
            }
        }
        FinishClientJoin(ip, port, areaName);
    }

    static void FinishClientJoin(string ip, ushort port, string areaName)
    {
        var area = FindArea(areaName, out var avail);
        if (area == null)
        {
            LanState.Status("status.noSuchArea", areaName, avail);
            Plugin.L.LogWarning("[LAN] " + LanState.LastStatus.En()); Show(); return;
        }
        LanState.Armed = LanRole.Client;
        try
        {
            PushStart(Fusion.GameMode.Client, $"LAN {ip}:{port}", area);
            LanState.Status("status.joining", $"{ip}:{port}", area.name);
            Plugin.L.LogInfo("[LAN] " + LanState.LastStatus.En());
            // World start is imminent: close F7 even if the user had it open to click Join.
            Hide();
        }
        catch (Exception e)
        {
            LanState.Watching = false; LanState.Armed = LanRole.Off;
            LanState.Status("status.joinNotStarted", e.Message); Show();
            Plugin.L.LogError($"[LAN] Join: {e}");
        }
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
            // Vanilla creates the runner when the server browser opens; an unattended instance never gets there.
            if (Autopilot.Active && !_instTried)
            {
                _instTried = true;
                try { OverlandNetworkRunner.Instantiate(); Plugin.L.LogWarning("[AutoTest] OverlandNetworkRunner.Instantiate() (as the server browser would)"); }
                catch (Exception e) { Plugin.L.LogError($"[AutoTest] Instantiate: {e}"); }
            }
            return;
        }
        if (Autopilot.Active && Time.realtimeSinceStartup < _instReadyAt) return;
        if (Autopilot.Active && _instReadyAt == 0) { _instReadyAt = Time.realtimeSinceStartup + 5f; return; }   // let it settle
        _autoDone = true;
        Plugin.L.LogWarning($"[AutoTest] Debug.AutoTest={mode}: starting without user input (dev only)");
        try
        {
            if (mode == "host") { ArmHost(); PushStart(Fusion.GameMode.Host, "LAN host autotest", FindArea(Plugin.JoinArea.Value, out _)); }
            else if (mode.StartsWith("join:")) Join(mode.Substring(5));
        }
        catch (Exception e) { Plugin.L.LogError($"[AutoTest] {e}"); LanState.Watching = false; }
    }
}

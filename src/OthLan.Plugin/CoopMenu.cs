using System;
using System.Collections.Generic;
using FunCore.Managers;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace OthLan;

/// <summary>
/// 0.1.7 "Local Co-op" in the game's main menu (ROADMAP 6). A clone of the game's own Multiplayer button is placed
/// right after it; it opens our lobby screen, built at runtime from the game's own button (font, colours, selection
/// look) — nothing of the game is copied into the mod. No native hook: the main menu is found by polling.
/// If anything is missing (game update) there is simply no button; F7 keeps working.
/// </summary>
internal static class CoopMenu
{
    public static string State = "waiting";          // for logs / self-test summary
    public static int Opens, Joins, Hosts, BuildFailures;
    static int _relabels;
    static float _nextPoll;
    static ContextUIMainMenu _menu;
    static GameObject _button;
    static bool _broken, _upper, _componentsLogged;
    static int _locGen = -1;
    static string _buttonText; static TMP_Text _buttonLabel;
    static readonly List<Delegate> _keep = new();

    // screen
    static GameObject _screen;
    static UIButtonSelector _template;
    static TMP_Text _templateText;
    static TMP_Text _status, _header, _title;
    static readonly List<UIButtonSelector> _rows = new();
    static readonly List<DiscoveryLogic.Lobby> _shown = new();
    static UIButtonSelector _playersBtn, _hostBtn, _addBtn, _backBtn;
    static int _players = 4, _maxPlayers = 4;
    static float _nextRefresh;
    static Msg _statusText = Msg.Raw("");
    static bool _addOpen; static string _addText = "";
    public static bool Open => _screen != null;
    const int RowCount = 6;

    static string Cap(string s) => _upper ? s.ToUpperInvariant() : s;

    static string _pendingJoin; static float _pendingJoinAt;
    public static void Tick()
    {
        if (_pendingJoin != null && Time.realtimeSinceStartup >= _pendingJoinAt)
        {
            var e = _pendingJoin; _pendingJoin = null;
            try { Overlay.Join(e); } catch (Exception ex) { Plugin.L.LogError($"[Coop] join: {ex}"); }
        }
        if (_broken || LanState.Disabled && BuildGate.Outcome != GateLogic.Outcome.DisabledByUser) return;
        try
        {
            if (_locGen != Loc.Generation) Relocalize();
            if (_screen != null) { GuardOwned(); ScreenTick(); }
            if (_buttonLabel != null && !_buttonLabel.WasCollected && _buttonLabel.text != _buttonText)
            {   // the game's menu re-localises button labels when it is (re)enabled: keep ours (checked every frame)
                if (_relabels++ < 3) Plugin.L.LogInfo($"[Coop] menu button label was reset to '{_buttonLabel.text}': set again");
                _buttonLabel.text = _buttonText;
            }
            if (Time.realtimeSinceStartup < _nextPoll) return;
            _nextPoll = Time.realtimeSinceStartup + 0.5f;
            if (!Plugin.MenuButton.Value) return;
            if (_menu != null && _menu.WasCollected) { _menu = null; _button = null; }
            if (_menu == null || _button == null)
            {
                var m = UnityEngine.Object.FindObjectOfType<ContextUIMainMenu>();
                if (m == null) { if (_screen != null) Close(false); return; }
                if (m.MultiplayerButton == null || m.buttonsContainer == null)
                { if (State != "no-multiplayer-button") Plugin.L.LogWarning("[Coop] main menu has no Multiplayer button: no Local Co-op button (F7 works)"); State = "no-multiplayer-button"; return; }
                _menu = m;
                AddButton();
            }
            else if (_button.WasCollected || _button == null) _button = null;

        }
        catch (Exception e)
        {
            _broken = true; BuildFailures++; State = "failed";
            Plugin.L.LogError($"[Coop] main-menu button disabled: {e}");
            try { if (_screen != null) UnityEngine.Object.Destroy(_screen); if (_menu != null && !_menu.WasCollected) _menu.buttonsContainer.gameObject.SetActive(true); } catch { }
        }
    }

    static Il2CppSystem.Action<UIButtonSelector> Click(Action a)
    {
        var d = new Action<UIButtonSelector>(_ => { try { a(); } catch (Exception e) { Plugin.L.LogError($"[Coop] click: {e}"); } });
        _keep.Add(d);
        return DelegateSupport.ConvertDelegate<Il2CppSystem.Action<UIButtonSelector>>(d);
    }

    /// <summary>Clone of a game button without its localisation (else it would rewrite our text).</summary>
    static UIButtonSelector CloneButton(Transform parent, string name)
    {
        var go = UnityEngine.Object.Instantiate(_menu.MultiplayerButton.gameObject, parent);
        go.name = name;
        var names = new List<string>();
        foreach (var c in go.GetComponentsInChildren<MonoBehaviour>(true))
        {
            var tn = c.GetIl2CppType().FullName ?? "";
            names.Add(tn);
            if (tn.Contains("Locali")) { c.enabled = false; UnityEngine.Object.Destroy(c); }
        }
        // FunTextTMP localises itself (its LocalizedString = "Multiplayer") whenever the game's language changes: unhook it
        foreach (var ft in go.GetComponentsInChildren<global::FunTextTMP>(true))
            try { ft.ClearTextChangeHandler(); } catch (Exception e) { Plugin.L.LogWarning($"[Coop] FunTextTMP unhook: {e.Message}"); }
        if (!_componentsLogged) { _componentsLogged = true; Plugin.L.LogInfo($"[Coop] button template components: {string.Join(", ", names)}"); }
        go.SetActive(true);
        return go.GetComponent<UIButtonSelector>();
    }

    static void SetLabel(UIButtonSelector b, string text)
    {
        foreach (var t in b.GetComponentsInChildren<TMP_Text>(true)) { t.richText = true; t.text = text; _owned[t.Pointer] = (t, text); }
    }

    // texts we own on cloned game buttons; if the game re-localises one anyway (language change), set ours again
    static readonly Dictionary<IntPtr, (TMP_Text t, string text)> _owned = new();
    static void GuardOwned()
    {
        if (_owned.Count == 0) return;
        List<IntPtr> dead = null;
        foreach (var kv in _owned)
        {
            var (t, text) = kv.Value;
            if (t == null || t.WasCollected) { (dead ??= new()).Add(kv.Key); continue; }
            if (t.text != text) t.text = text;
        }
        if (dead != null) foreach (var d in dead) _owned.Remove(d);
    }

    static void AddButton()
    {
        var mp = _menu.MultiplayerButton;
        _templateText = mp.GetComponentInChildren<TMP_Text>(true);
        var label = _templateText != null ? _templateText.text ?? "" : "";
        _upper = label.Length > 0 && label == label.ToUpperInvariant();
        var b = CloneButton(_menu.buttonsContainer, "OTH-LAN LocalCoopButton");
        b.transform.SetSiblingIndex(mp.transform.GetSiblingIndex() + 1);
        _buttonText = Cap(Loc.T("coop.button")); _locGen = Loc.Generation;
        SetLabel(b, _buttonText);
        b.add_OnPointerClickOrSubmit(Click(OpenScreen));
        _button = b.gameObject;
        _buttonLabel = b.GetComponentInChildren<TMP_Text>(true);
        State = "button";
        InitLobbyName();
        Plugin.L.LogInfo($"[Coop] 'Local Co-op' button added to the main menu (template label '{label}', font {(_templateText != null && _templateText.font != null ? _templateText.font.name : "?")}, lang {Loc.Lang})");
        MaxPlayersFromGame();
    }

    /// <summary>The game's own "max players" choices (create-server screen) if that prefab is loaded; else 4.</summary>
    static void MaxPlayersFromGame()
    {
        try
        {
            foreach (var c in Resources.FindObjectsOfTypeAll<ContextUICreateServer>())
            {
                var r = c.maxPlayersOptionsRange;
                if (r == null || r.Count == 0) continue;
                int mx = 0; var all = new List<string>();
                for (int i = 0; i < r.Count; i++) { all.Add(r[i]); if (int.TryParse(r[i], out var v)) mx = Math.Max(mx, v); }
                Plugin.L.LogInfo($"[Coop] game's max-players options: {string.Join(",", all)}");
                if (mx >= 2) _maxPlayers = Math.Min(mx, 4);
                return;
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[Coop] max players probe: {e.Message}"); }
    }

    // ---------------- screen ----------------
    static GameObject Rect(Transform parent, string name, Vector2 pos, Vector2 size, Vector2 anchor)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = anchor; rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos; rt.sizeDelta = size;
        return go;
    }

    static TMP_Text Text(Transform parent, string name, Vector2 pos, Vector2 size, float fontSize, TextAlignmentOptions align)
    {
        var go = Rect(parent, name, pos, size, new Vector2(0.5f, 0.5f));
        var t = go.AddComponent<TextMeshProUGUI>();
        if (_templateText != null)
        {
            t.font = _templateText.font; t.fontSharedMaterial = _templateText.fontSharedMaterial; t.color = _templateText.color;
        }
        t.fontSize = fontSize; t.alignment = align; t.richText = true; t.raycastTarget = false;
        t.enableWordWrapping = true;
        return t;
    }

    static UIButtonSelector Button(Transform parent, string name, Vector2 pos, Vector2 size)
    {
        var b = CloneButton(parent, name);
        var rt = b.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f); rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos; rt.sizeDelta = size;
        var le = b.GetComponent<LayoutElement>(); if (le != null) le.ignoreLayout = true;
        return b;
    }

    public static void OpenScreen()
    {
        if (_screen != null || _menu == null) return;
        Opens++;
        _template = _menu.MultiplayerButton;
        _screen = new GameObject("OTH-LAN LocalCoopScreen");
        UnityEngine.Object.DontDestroyOnLoad(_screen);
        var canvas = _screen.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 500;
        var sc = _screen.AddComponent<CanvasScaler>();
        sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; sc.referenceResolution = new Vector2(1920, 1080); sc.matchWidthOrHeight = 0.5f;
        _screen.AddComponent<GraphicRaycaster>();
        var root = _screen.transform;
        var dim = Rect(root, "Dim", Vector2.zero, new Vector2(4000, 3000), new Vector2(0.5f, 0.5f));
        dim.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.55f);
        var panel = Rect(root, "Panel", Vector2.zero, new Vector2(1360, 800), new Vector2(0.5f, 0.5f));
        panel.AddComponent<Image>().color = new Color(0.07f, 0.07f, 0.08f, 0.92f);
        var p = panel.transform;
        _title = Text(p, "Title", new Vector2(0, 345), new Vector2(1280, 60), 44, TextAlignmentOptions.Center);
        _sub = Text(p, "Hint", new Vector2(0, 298), new Vector2(1280, 34), 22, TextAlignmentOptions.Center);
        _sub.color = new Color(1, 1, 1, 0.6f);
        _header = Text(p, "Header", new Vector2(0, 252), new Vector2(1240, 34), 22, TextAlignmentOptions.Left);
        StaticTexts();
        _header.color = new Color(1, 1, 1, 0.55f);
        _rows.Clear();
        float rowH = Mathf.Clamp(_template.GetComponent<RectTransform>().rect.height, 48, 72);
        for (int i = 0; i < RowCount; i++)
        {
            var r = Button(p, $"Row{i}", new Vector2(0, 205 - i * (rowH + 8)), new Vector2(1240, rowH));
            int ix = i;
            r.add_OnPointerClickOrSubmit(Click(() => RowClicked(ix)));
            foreach (var t in r.GetComponentsInChildren<TMP_Text>(true)) { t.alignment = TextAlignmentOptions.Left; t.enableWordWrapping = false; }
            _rows.Add(r);
        }
        float by = -300;
        _playersBtn = Button(p, "Players", new Vector2(-470, by), new Vector2(290, 60));
        _playersBtn.add_OnPointerClickOrSubmit(Click(() => { _players = _players >= _maxPlayers ? 2 : _players + 1; Labels(); }));
        _hostBtn = Button(p, "Host", new Vector2(-160, by), new Vector2(310, 60));
        _hostBtn.add_OnPointerClickOrSubmit(Click(BecomeHost));
        _addBtn = Button(p, "AddIp", new Vector2(160, by), new Vector2(310, 60));
        _addBtn.add_OnPointerClickOrSubmit(Click(() => { _addOpen = true; _addText = ""; }));
        _backBtn = Button(p, "Back", new Vector2(470, by), new Vector2(290, 60));
        _backBtn.add_OnPointerClickOrSubmit(Click(() => Close(true)));
        _status = Text(p, "Status", new Vector2(0, -362), new Vector2(1280, 60), 22, TextAlignmentOptions.Center);
        _players = Math.Clamp(Plugin.PlayerCount.Value, 2, _maxPlayers);
        if (Plugin.PlayerCount.Value < 2 || Plugin.PlayerCount.Value > _maxPlayers) _players = _maxPlayers;
        _statusText = Msg.Raw("");
        Labels();
        _menu.buttonsContainer.gameObject.SetActive(false);
        InitLobbyName();
        Discovery.Start();
        _nextRefresh = 0; ScreenTick();
        Select(_hostBtn.gameObject);
        State = "screen";
        Plugin.L.LogInfo($"[Coop] Local Co-op screen opened (row height {rowH:0}, max players {_maxPlayers})");
    }

    static void Labels()
    {
        SetLabel(_playersBtn, Cap(Loc.F("coop.players", _players)));
        SetLabel(_hostBtn, Cap(Loc.T("coop.host")));
        SetLabel(_addBtn, Cap(Loc.T("coop.addIp")));
        SetLabel(_backBtn, Cap(Loc.T("coop.back")));
    }

    static TMP_Text _sub;
    static void StaticTexts()
    {
        _title.text = Cap(Loc.T("coop.button"));
        _sub.text = Loc.T("coop.hint");
        _header.text = Columns(Loc.T("coop.colHost"), Loc.T("coop.colPlayers"), Loc.T("coop.colPing"), Loc.T("coop.colVersion"), "");
    }

    /// <summary>The game's language changed: re-set every text we own (button, open screen; rows refresh by themselves).</summary>
    static void Relocalize()
    {
        _locGen = Loc.Generation;
        if (_buttonLabel != null && !_buttonLabel.WasCollected)
        {
            try { _upper = _templateText != null && !_templateText.WasCollected && _templateText.text is { Length: > 0 } t && t == t.ToUpperInvariant(); } catch { }
            _buttonText = Cap(Loc.T("coop.button")); _buttonLabel.text = _buttonText;
        }
        if (_screen != null && _title != null) { StaticTexts(); Labels(); _nextRefresh = 0; }
        Plugin.L.LogInfo($"[Coop] texts switched to {Loc.Lang}");
    }

    static string Columns(string a, string b, string c, string d, string e) =>
        $"<pos=2%>{a}<pos=44%>{b}<pos=56%>{c}<pos=68%>{d}<pos=88%>{e}";

    static string Esc(string s) => "<noparse>" + (s ?? "").Replace("</noparse>", "") + "</noparse>";

    static void Select(GameObject go) { try { EventSystem.current?.SetSelectedGameObject(go); } catch { } }

    static void ScreenTick()
    {
        if (_menu == null || _menu.WasCollected || !_menu.gameObject.activeInHierarchy && !_menu.buttonsContainer.gameObject.activeSelf && Time.frameCount % 30 == 0 && UnityEngine.Object.FindObjectOfType<ContextUIMainMenu>() == null)
        { Close(false); return; }
        if (!_addOpen && (BackPressed())) { Close(true); return; }
        if (_playersBtn != null && EventSystem.current != null && EventSystem.current.currentSelectedGameObject == _playersBtn.gameObject)
        {
            int d = LeftRight();
            if (d != 0) { _players = _players <= 2 ? _maxPlayers : _players - 1; Labels(); }
        }
        if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject == null && !_addOpen) Select(_hostBtn.gameObject);
        if (Time.realtimeSinceStartup < _nextRefresh) return;
        _nextRefresh = Time.realtimeSinceStartup + 0.5f;
        var list = Discovery.Snapshot();
        _shown.Clear();
        for (int i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            if (i < list.Count)
            {
                var l = list[i]; _shown.Add(l);
                var compat = DiscoveryLogic.Check(l, Plugin.Version, BuildGate.BuildKey, BuildGate.GaHash);
                var lv = double.IsNaN(l.PingMs) ? PingLogic.Level.Waiting : PingLogic.Classify(l.PingMs, Plugin.PingYellowAboveMs.Value, Plugin.PingRedAboveMs.Value);
                var pc = lv == PingLogic.Level.Green ? "#5AD45A" : lv == PingLogic.Level.Yellow ? "#FFDB26" : lv == PingLogic.Level.Red ? "#FF5050" : "#AAAAAA";
                var ping = double.IsNaN(l.PingMs) ? "--" : Loc.F("unit.ms", Math.Max(1, Math.Round(l.PingMs)));
                var ver = compat == DiscoveryLogic.Compat.Ok ? $"<color=#5AD45A>{Esc(l.Mod)} OK</color>"
                        : compat == DiscoveryLogic.Compat.OtherMod ? $"<color=#FF5050>{Esc(l.Mod)} {Loc.T("coop.modDiffers")}</color>"
                        : $"<color=#FF5050>{Loc.T("coop.otherGame")}</color>";
                var act = l.Full ? $"<color=#AAAAAA>{Loc.T("coop.full")}</color>" : compat == DiscoveryLogic.Compat.Ok ? Loc.T("coop.join") : "<color=#AAAAAA>—</color>";
                SetLabel(r, Columns(Esc(l.Name.Length > 0 ? l.Name : l.Ip), $"{l.Cur}/{l.Max}", $"<color={pc}>{ping}</color>", ver, act));
                r.gameObject.SetActive(true);
            }
            else if (i == 0)
            {
                SetLabel(r, $"<pos=2%><color=#AAAAAA>{Loc.T("coop.searching")}</color>");
                r.gameObject.SetActive(true);
            }
            else r.gameObject.SetActive(false);
        }
        Nav();
        var s = _statusText.ToString();
        if (string.IsNullOrEmpty(s))
        {
            var ip = NetInfo.DisplayIp();
            s = Loc.F("coop.footer", ip ?? Loc.T("f7.ipNotFound"));
        }
        _status.text = Esc(NetInfo.Mask(s));
    }

    /// <summary>Explicit gamepad/keyboard navigation: rows top-down, then the bottom bar left-right.</summary>
    static void Nav()
    {
        var sel = new List<Selectable>();
        foreach (var r in _rows) if (r.gameObject.activeSelf) sel.Add(r);
        var bar = new Selectable[] { _playersBtn, _hostBtn, _addBtn, _backBtn };
        for (int i = 0; i < sel.Count; i++)
        {
            var n = new Navigation { mode = Navigation.Mode.Explicit };
            n.selectOnUp = i > 0 ? sel[i - 1] : null;
            n.selectOnDown = i + 1 < sel.Count ? sel[i + 1] : _hostBtn;
            sel[i].navigation = n;
        }
        for (int i = 0; i < bar.Length; i++)
        {
            var n = new Navigation { mode = Navigation.Mode.Explicit };
            n.selectOnLeft = i > 0 ? bar[i - 1] : null;
            n.selectOnRight = i + 1 < bar.Length ? bar[i + 1] : null;
            n.selectOnUp = sel.Count > 0 ? sel[sel.Count - 1] : null;
            if (bar[i] == _playersBtn) { n.selectOnLeft = null; n.selectOnRight = _hostBtn; }
            bar[i].navigation = n;
        }
    }

    static bool BackPressed()
    {
        try
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame) return true;
            var gp = UnityEngine.InputSystem.Gamepad.current;
            if (gp != null && gp.buttonEast.wasPressedThisFrame) return true;
        }
        catch { }
        return false;
    }

    static int LeftRight()
    {
        try
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            var gp = UnityEngine.InputSystem.Gamepad.current;
            // Left on the players button lowers the count (wraps); Submit raises it; Right moves on to "Become host".
            if (kb != null && kb.leftArrowKey.wasPressedThisFrame || gp != null && gp.dpad.left.wasPressedThisFrame) return -1;
        }
        catch { }
        return 0;
    }

    public static void Close(bool selectButton)
    {
        _addOpen = false;
        Discovery.Stop();
        if (_screen != null) { try { UnityEngine.Object.Destroy(_screen); } catch { } }
        _screen = null; _rows.Clear();
        try { if (_menu != null && !_menu.WasCollected) _menu.buttonsContainer.gameObject.SetActive(true); } catch { }
        if (selectButton && _button != null) Select(_button);
        State = "button";
        Plugin.L.LogInfo("[Coop] Local Co-op screen closed");
    }

    static void RowClicked(int ix)
    {
        if (ix >= _shown.Count) { Discovery.RefreshNow(); return; }
        var l = _shown[ix];
        var compat = DiscoveryLogic.Check(l, Plugin.Version, BuildGate.BuildKey, BuildGate.GaHash);
        if (compat == DiscoveryLogic.Compat.OtherMod) { _statusText = new Msg("coop.modMismatch", l.Mod, Plugin.Version); return; }
        if (compat == DiscoveryLogic.Compat.OtherGame) { _statusText = new Msg("coop.gameMismatch", l.Build); return; }
        if (l.Full) { _statusText = new Msg("coop.isFull", l.Cur, l.Max); return; }
        if (LanState.Disabled) { _statusText = BuildGate.StatusMsg(); return; }
        Joins++;
        Plugin.L.LogInfo($"[Coop] join '{l.Name}' at {l.Endpoint} (via {l.Via}, ping {l.PingMs:0} ms, {l.Cur}/{l.Max})");
        if (!Autopilot.Active) Discovery.RememberHost(l.Ip, l.Port);
        Close(false);
        if (OverlandNetworkRunner.Instance == null)
        {   // vanilla creates the runner when its server browser opens; give it a moment like the game does
            try { OverlandNetworkRunner.Instantiate(); Plugin.L.LogInfo("[Coop] OverlandNetworkRunner.Instantiate() (as the server browser would)"); }
            catch (Exception e) { Plugin.L.LogWarning($"[Coop] runner: {e.Message}"); }
            _pendingJoin = l.Endpoint; _pendingJoinAt = Time.realtimeSinceStartup + 3f;
            return;
        }
        Overlay.Join(l.Endpoint);
    }

    /// <summary>"Become host": the game's own create flow (region -> area -> start) with only the player count from us.</summary>
    public static void BecomeHost()
    {
        if (LanState.Disabled) { _statusText = BuildGate.StatusMsg(); return; }
        if (LanState.Watching) { _statusText = new Msg("coop.busy"); return; }
        Hosts++;
        var n = Math.Clamp(_players, 2, _maxPlayers);
        if (!Autopilot.Active) Plugin.PlayerCount.Value = n;
        else Plugin.PlayerCount.Value = n;   // self-test: cfg is restored by the orchestrator
        LanState.Armed = LanRole.Host; LanState.HostPlayers = 0;
        LanState.Status("coop.armedHost", n, $"{NetInfo.DisplayIp() ?? "<IP>"}:{Plugin.Port.Value}");
        Plugin.L.LogInfo($"[Coop] become host: {n} players, opening the game's region/area selection");
        if (OverlandNetworkRunner.Instance == null)
        {
            try { OverlandNetworkRunner.Instantiate(); Plugin.L.LogInfo("[Coop] OverlandNetworkRunner.Instantiate() (as the server browser would)"); }
            catch (Exception e) { Plugin.L.LogWarning($"[Coop] runner: {e.Message}"); }
        }
        string region = null;
        try { region = OverlandNetworkRunner.Instance?.ConnectedRegion; } catch { }
        var ctx = new GameSetupContext
        {
            GameMode = Fusion.GameMode.Host,
            RoomName = "LAN " + HostInfo.LobbyName,
            RoomCode = "",
            ServerRegion = region,
            MaxPlayers = n,
            RoomProperties = new OverlandRoomProperties(),
        };
        Close(false);
        HybridSingleton<GameStateManager>.Instance.PushState(new StateRegionSelectionMenu(ctx));
    }

    /// <summary>IMGUI "add by IP" box (typing needs a keyboard anyway).</summary>
    public static void OnGUI()
    {
        if (!_addOpen || _screen == null) return;
        Cursor.visible = true; Cursor.lockState = CursorLockMode.None;
        float w = 560, h = 170, x = (Screen.width - w) / 2, y = (Screen.height - h) / 2;
        GUI.Box(new Rect(x, y, w, h), ""); GUI.Box(new Rect(x, y, w, h), ""); GUI.Box(new Rect(x, y, w, h), "");
        var st = new GUIStyle(GUI.skin.label) { fontSize = 18 };
        GUI.Label(new Rect(x + 16, y + 12, w - 32, 28), Loc.T("coop.addTitle"), st);
        GUI.SetNextControlName("othlan-addip");
        var ts = new GUIStyle(GUI.skin.textField) { fontSize = 20 };
        _addText = GUI.TextField(new Rect(x + 16, y + 48, w - 32, 34), _addText ?? "", 40, ts);
        GUI.FocusControl("othlan-addip");
        var ev = Event.current;
        bool enter = ev.type == EventType.KeyDown && (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter);
        bool esc = ev.type == EventType.KeyDown && ev.keyCode == KeyCode.Escape;
        var bs = new GUIStyle(GUI.skin.button) { fontSize = 18 };
        if (GUI.Button(new Rect(x + 16, y + 100, (w - 44) / 2, 44), Loc.T("coop.add"), bs) || enter)
        {
            if (Discovery.AddManual(_addText, out var norm)) { _statusText = new Msg("coop.added", norm); _addOpen = false; Plugin.L.LogInfo($"[Coop] manual host added: {norm}"); Select(_addBtn.gameObject); }
            else _statusText = new Msg("coop.badAddress", _addText);
            if (enter) ev.Use();
        }
        if (GUI.Button(new Rect(x + 28 + (w - 44) / 2, y + 100, (w - 44) / 2, 44), Loc.T("coop.cancel"), bs) || esc)
        { _addOpen = false; Select(_addBtn.gameObject); if (esc) ev.Use(); }
    }

    /// <summary>Lobby name: cfg, else the Steam name, else the PC name (main thread, once).</summary>
    public static string StatusText => _statusText?.ToString() ?? "";
    public static string ButtonLabel { get { try { return _buttonLabel != null ? _buttonLabel.text ?? "" : ""; } catch { return ""; } } }
    public static void TestPlayers(int n) { _players = Math.Clamp(n, 2, _maxPlayers); if (_screen != null) Labels(); }
    public static void TestClickRow(int i) => RowClicked(i);
    public static bool LobbyNameSet; public static string DemoName;
    public static void InitLobbyName()
    {
        var n = DemoName ?? Plugin.HostName.Value;
        if (string.IsNullOrWhiteSpace(n))
        {
            try { n = Steamworks.SteamFriends.GetPersonaName(); } catch { n = null; }
        }
        if (string.IsNullOrWhiteSpace(n)) n = Environment.MachineName;
        HostInfo.LobbyName = DiscoveryLogic.Clean(n, 32);
        LobbyNameSet = true;
    }
}

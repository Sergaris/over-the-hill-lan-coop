using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace OthLan;

/// <summary>
/// Pre-join gate: host requires Modio liveries the client lacks → in-game Canvas dialog (game fonts/buttons)
/// with Download / Join without mods / Cancel. Download is driven by <see cref="ModLiveryBridge"/>.
/// </summary>
internal static class ModLiveryGate
{
    public enum St { Idle, Prompt, Downloading }
    public static St State { get; private set; } = St.Idle;
    static List<ModLiveryGateLogic.ModRef> _missing = new();
    static List<ModLiveryGateLogic.ModRef> _required = new();
    static Action _onReady;
    static Action _onCancel;
    static Action _onJoinWithout;

    const string ModalOwner = "modgate";
    static GameObject _root;
    static TMP_Text _title, _body, _list, _status;
    static UIButtonSelector _dlBtn, _skipBtn, _cancelBtn;
    static int _locGen = -1;
    static string _listFp = "";
    static GameObject _prevSelected;
    static ContextUIMainMenu _menuHid;
    static bool _hidButtons;

    public static bool Active => State != St.Idle;

    /// <summary>
    /// Show the gate. <paramref name="onReady"/> = all mods local; <paramref name="onJoinWithout"/> = join with
    /// mods disabled for this client; <paramref name="onCancel"/> = abort join.
    /// </summary>
    public static void Begin(IReadOnlyList<ModLiveryGateLogic.ModRef> required, IReadOnlyList<ModLiveryGateLogic.ModRef> missing,
        Action onReady, Action onCancel, Action onJoinWithout = null)
    {
        if (UiModal.Current != null && UiModal.Current != ModalOwner)
        { Plugin.L.LogInfo($"[ModLivery] gate deferred (modal={UiModal.Current})"); return; }
        if (!UiModal.TryOpen(ModalOwner)) return;
        _required = required != null ? new List<ModLiveryGateLogic.ModRef>(required) : new();
        _missing = missing != null ? new List<ModLiveryGateLogic.ModRef>(missing) : new();
        _onReady = onReady; _onCancel = onCancel; _onJoinWithout = onJoinWithout;
        State = St.Prompt;
        Plugin.L.LogInfo($"[ModLivery] gate: {_missing.Count} missing of {_required.Count} required");
        // Do NOT Overlay.Show() — that opens F7. This gate is its own in-game Canvas.
        Overlay.Hide();
        try { _prevSelected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null; } catch { _prevSelected = null; }
        EnsureUi();
        HideMainMenuButtons();
        RefreshUi();
        FocusInitial();
    }

    public static void Cancel()
    {
        if (State == St.Idle) return;
        ModLiveryBridge.AbortDownload();
        State = St.Idle;
        var c = _onCancel; Clear();
        DestroyUi();
        try { c?.Invoke(); } catch (Exception e) { Plugin.L.LogWarning($"[ModLivery] cancel: {e.Message}"); }
    }

    /// <summary>Join the host without waiting for Modio liveries (client session AllowMods=false).</summary>
    public static void JoinWithoutMods()
    {
        if (State == St.Idle) return;
        ModLiveryBridge.AbortDownload();
        State = St.Idle;
        var skip = _onJoinWithout ?? _onReady;
        Clear();
        DestroyUi();
        Plugin.L.LogInfo("[ModLivery] gate: joining without mods");
        try { skip?.Invoke(); } catch (Exception e) { Plugin.L.LogError($"[ModLivery] join without mods: {e}"); }
    }

    /// <summary>Self-test / automation: same as pressing Download in the dialog.</summary>
    public static void StartDownload()
    {
        if (State != St.Prompt || _missing.Count == 0) return;
        State = St.Downloading;
        ModLiveryBridge.BeginDownload(_missing);
        RefreshUi();
    }

    static void Clear()
    {
        _missing.Clear(); _required.Clear();
        _onReady = null; _onCancel = null; _onJoinWithout = null;
    }

    public static void Tick()
    {
        if (State == St.Idle) return;
        if (_locGen != Loc.Generation) { _locGen = Loc.Generation; RefreshUi(); }

        if (State == St.Downloading)
        {
            ModLiveryBridge.TickDownload();
            _missing = ModLiveryBridge.MissingOf(_required);
            if (ModLiveryBridge.DownloadPhase == ModLiveryBridge.Phase.Done || _missing.Count == 0)
            {
                State = St.Idle;
                var ok = _onReady; Clear();
                DestroyUi();
                Plugin.L.LogInfo("[ModLivery] gate: all mods ready — continuing join");
                try { ok?.Invoke(); } catch (Exception e) { Plugin.L.LogError($"[ModLivery] resume join: {e}"); }
                return;
            }
            if (ModLiveryBridge.DownloadPhase == ModLiveryBridge.Phase.Failed)
            {
                State = St.Prompt; // allow retry / join without
                RefreshUi();
                return;
            }
            // T6.3: rebuild the list only when row state / progress fingerprint changes.
            if (ListFingerprint() != _listFp) RefreshUi();
            else RefreshUiStatusOnly();
        }

        // T1.3: EventSystem Submit owns A/Enter; we only keep B/Esc → Cancel and KeepFocus.
        try
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            var gp = UnityEngine.InputSystem.Gamepad.current;
            bool esc = kb != null && kb.escapeKey.wasPressedThisFrame || gp != null && gp.buttonEast.wasPressedThisFrame;
            if (esc) Cancel();
        }
        catch { }
        KeepGateFocus();
    }

    static void FocusInitial()
    {
        LinkGateButtons();
        var prefer = _dlBtn != null && _dlBtn.gameObject.activeInHierarchy ? _dlBtn : _skipBtn;
        if (prefer != null) UiNav.Select(prefer.gameObject);
    }

    static void LinkGateButtons()
    {
        var row = new Selectable[] { _dlBtn, _skipBtn, _cancelBtn };
        // Drop nulls while preserving order for LinkRow.
        var list = new List<Selectable>(3);
        foreach (var s in row) if (s != null && s.gameObject.activeInHierarchy) list.Add(s);
        if (list.Count > 0) UiNav.LinkRow(list.ToArray());
    }

    static void KeepGateFocus()
    {
        var list = new List<Selectable>(3);
        if (_dlBtn != null && _dlBtn.gameObject.activeInHierarchy) list.Add(_dlBtn);
        if (_skipBtn != null && _skipBtn.gameObject.activeInHierarchy) list.Add(_skipBtn);
        if (_cancelBtn != null && _cancelBtn.gameObject.activeInHierarchy) list.Add(_cancelBtn);
        if (list.Count == 0) return;
        var fallback = list[0];
        if (_dlBtn != null && list.Contains(_dlBtn)) fallback = _dlBtn;
        UiNav.KeepFocus(list.ToArray(), fallback);
    }

    static void HideMainMenuButtons()
    {
        _hidButtons = false; _menuHid = null;
        try
        {
            var m = UnityEngine.Object.FindObjectOfType<ContextUIMainMenu>();
            if (m == null || m.buttonsContainer == null) return;
            m.buttonsContainer.gameObject.SetActive(false);
            _menuHid = m; _hidButtons = true;
        }
        catch { }
    }

    static void RestoreMainMenuButtons()
    {
        if (_hidButtons && _menuHid != null && !_menuHid.WasCollected)
        {
            try { _menuHid.buttonsContainer.gameObject.SetActive(true); } catch { }
        }
        _hidButtons = false; _menuHid = null;
        try
        {
            if (_prevSelected != null && !_prevSelected.WasCollected && _prevSelected.activeInHierarchy)
                UiNav.Select(_prevSelected);
            else if (CoopMenu.LocalCoopButton != null)
                UiNav.Select(CoopMenu.LocalCoopButton);
        }
        catch { }
        _prevSelected = null;
        UiModal.Close(ModalOwner);
    }

    /// <summary>Kept for Overlay.OnGUI hook compatibility; drawing is Canvas-only.</summary>
    public static void OnGUI() { }

    static void EnsureUi()
    {
        if (_root != null && !_root.WasCollected) return;
        _root = new GameObject("OTH-LAN ModLiveryGate");
        UnityEngine.Object.DontDestroyOnLoad(_root);
        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 800;
        var sc = _root.AddComponent<CanvasScaler>();
        sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        sc.referenceResolution = new Vector2(1920, 1080);
        sc.matchWidthOrHeight = 0.5f;
        _root.AddComponent<GraphicRaycaster>();

        var dim = Rect(_root.transform, "Dim", Vector2.zero, new Vector2(4000, 3000));
        var dimImg = dim.AddComponent<Image>();
        dimImg.color = new Color(0f, 0f, 0f, 0.6f);
        dimImg.raycastTarget = true; // T1.3: block clicks through to the menu

        var panel = Rect(_root.transform, "Panel", Vector2.zero, new Vector2(960, 600));
        panel.AddComponent<Image>().color = new Color(0.07f, 0.07f, 0.08f, 0.96f);
        var p = panel.transform;

        TMP_FontAsset font = null; Color col = Color.white;
        try
        {
            var sample = UnityEngine.Object.FindObjectOfType<TMP_Text>();
            if (sample != null) { font = sample.font; col = sample.color; }
        }
        catch { }

        _title = MkText(p, "Title", new Vector2(0, 250), new Vector2(900, 48), 36, TextAlignmentOptions.Center, font, col);
        _body = MkText(p, "Body", new Vector2(0, 185), new Vector2(900, 72), 22, TextAlignmentOptions.Center, font, col);
        _body.color = new Color(col.r, col.g, col.b, 0.85f);
        _list = MkText(p, "List", new Vector2(0, 30), new Vector2(900, 220), 22, TextAlignmentOptions.Left, font, col);
        _status = MkText(p, "Status", new Vector2(0, -110), new Vector2(900, 48), 22, TextAlignmentOptions.Center, font, col);
        _status.color = new Color(1f, 0.85f, 0.4f, 1f);

        // Three actions: Download | Join without mods | Cancel
        _dlBtn = MkButton(p, "Download", new Vector2(-310, -230), new Vector2(280, 64));
        _skipBtn = MkButton(p, "JoinWithout", new Vector2(0, -230), new Vector2(280, 64));
        _cancelBtn = MkButton(p, "Cancel", new Vector2(310, -230), new Vector2(280, 64));
        if (_dlBtn != null) _dlBtn.add_OnPointerClickOrSubmit(Click(StartDownload));
        if (_skipBtn != null) _skipBtn.add_OnPointerClickOrSubmit(Click(JoinWithoutMods));
        if (_cancelBtn != null) _cancelBtn.add_OnPointerClickOrSubmit(Click(Cancel));
        if (_dlBtn == null || _cancelBtn == null)
            Plugin.L.LogWarning("[ModLivery] game buttons unavailable — use Enter=Download, Esc=Cancel");

        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;
        _locGen = Loc.Generation;
    }

    static GameObject Rect(Transform parent, string name, Vector2 pos, Vector2 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        return go;
    }

    static TMP_Text MkText(Transform parent, string name, Vector2 pos, Vector2 size, float fontSize,
        TextAlignmentOptions align, TMP_FontAsset font, Color col)
    {
        var go = Rect(parent, name, pos, size);
        var t = go.AddComponent<TextMeshProUGUI>();
        if (font != null) t.font = font;
        t.color = col; t.fontSize = fontSize; t.alignment = align;
        t.richText = true; t.raycastTarget = false; t.enableWordWrapping = true;
        return t;
    }

    static UIButtonSelector MkButton(Transform parent, string name, Vector2 pos, Vector2 size)
    {
        try
        {
            var menu = UnityEngine.Object.FindObjectOfType<ContextUIMainMenu>();
            var tpl = menu != null ? menu.MultiplayerButton : null;
            if (tpl == null)
            {
                foreach (var b in UnityEngine.Object.FindObjectsOfType<UIButtonSelector>())
                { tpl = b; break; }
            }
            if (tpl == null) return null;
            var go = UnityEngine.Object.Instantiate(tpl.gameObject, parent);
            go.name = name;
            foreach (var c in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                var tn = c.GetIl2CppType().FullName ?? "";
                if (tn.Contains("Locali")) { c.enabled = false; UnityEngine.Object.Destroy(c); }
            }
            foreach (var ft in go.GetComponentsInChildren<FunTextTMP>(true))
                try { ft.ClearTextChangeHandler(); } catch { }
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            var le = go.GetComponent<LayoutElement>(); if (le != null) le.ignoreLayout = true;
            go.SetActive(true);
            return go.GetComponent<UIButtonSelector>();
        }
        catch (Exception e) { Plugin.L.LogWarning($"[ModLivery] button clone: {e.Message}"); return null; }
    }

    static Il2CppSystem.Action<UIButtonSelector> Click(Action a) =>
        (Il2CppSystem.Action<UIButtonSelector>)((UIButtonSelector _) => { try { a(); } catch (Exception e) { Plugin.L.LogWarning($"[ModLivery] click: {e.Message}"); } });

    static void SetLabel(UIButtonSelector b, string text)
    {
        if (b == null) return;
        foreach (var t in b.GetComponentsInChildren<TMP_Text>(true)) { t.richText = true; t.text = text; }
    }

    static string ListFingerprint()
    {
        var source = (State == St.Downloading || ModLiveryBridge.DownloadPhase == ModLiveryBridge.Phase.Failed)
            && _required.Count > 0
            ? _required
            : _missing;
        var sb = new StringBuilder(source.Count * 24);
        sb.Append((int)State).Append('|').Append((int)ModLiveryBridge.DownloadPhase).Append('|');
        foreach (var m in source)
        {
            var kind = ModLiveryBridge.Classify(m);
            var pct = ModLiveryGateLogic.ItemStatusPercent(kind, ModLiveryBridge.TryGetProgress(m));
            var name = ModLiveryBridge.TryGetDisplayName(m) ?? "";
            sb.Append(m.Id).Append(':').Append(m.SubId).Append(':').Append((int)kind)
                .Append(':').Append(pct).Append(':').Append(name).Append(';');
        }
        return sb.ToString();
    }

    static void RefreshUi()
    {
        if (_root == null || _root.WasCollected) return;
        if (_title != null) _title.text = Loc.T("modGate.title");
        if (_body != null) _body.text = Loc.T("modGate.body");
        if (_list != null)
        {
            // Show the full required set while downloading / after failure so "installed" rows stay visible.
            var source = (State == St.Downloading || ModLiveryBridge.DownloadPhase == ModLiveryBridge.Phase.Failed)
                && _required.Count > 0
                ? _required
                : _missing;
            var sb = new StringBuilder();
            var n = Math.Min(source.Count, 10);
            for (int i = 0; i < n; i++)
            {
                var m = source[i];
                var name = ModLiveryBridge.TryGetDisplayName(m);
                var idFb = Loc.F("modGate.item", m.Id);
                var idSubFb = Loc.F("modGate.itemSub", m.Id, unchecked((uint)m.SubId).ToString());
                var title = ModLiveryGateLogic.ItemTitle(name, idFb, idSubFb, m.SubId);
                var kind = ModLiveryBridge.Classify(m);
                var prog = ModLiveryBridge.TryGetProgress(m);
                var sk = ModLiveryGateLogic.ItemStatusKey(kind, prog);
                var pct = ModLiveryGateLogic.ItemStatusPercent(kind, prog);
                var status = pct >= 0 ? Loc.F(sk, pct) : Loc.T(sk);
                sb.Append("• ").Append(title).Append(" — ").Append(status).Append('\n');
            }
            if (source.Count > 10) sb.Append(Loc.F("modGate.more", source.Count - 10));
            _list.text = sb.ToString();
        }
        _listFp = ListFingerprint();
        var failed = ModLiveryBridge.DownloadPhase == ModLiveryBridge.Phase.Failed;
        SetLabel(_dlBtn, failed ? Loc.T("modGate.retry") : Loc.T("modGate.download"));
        SetLabel(_skipBtn, Loc.T("modGate.joinWithout"));
        SetLabel(_cancelBtn, Loc.T("modGate.cancel"));
        if (_dlBtn != null) _dlBtn.gameObject.SetActive(State == St.Prompt);
        if (_skipBtn != null) _skipBtn.gameObject.SetActive(true); // available during download too
        LinkGateButtons();
        RefreshUiStatusOnly();
    }

    static void RefreshUiStatusOnly()
    {
        if (_status == null) return;
        if (State == St.Downloading)
        {
            var key = ModLiveryBridge.StatusKey;
            var arg = ModLiveryBridge.StatusArg;
            if (string.IsNullOrEmpty(key)) _status.text = Loc.F("modGate.downloading", _missing.Count);
            else if (string.IsNullOrEmpty(arg)) _status.text = Loc.T(key);
            else _status.text = Loc.F(key, arg);
        }
        else if (!string.IsNullOrEmpty(ModLiveryBridge.StatusKey) && ModLiveryBridge.DownloadPhase == ModLiveryBridge.Phase.Failed)
        {
            var key = ModLiveryBridge.StatusKey;
            _status.text = string.IsNullOrEmpty(ModLiveryBridge.StatusArg) ? Loc.T(key) : Loc.F(key, ModLiveryBridge.StatusArg);
        }
        else _status.text = "";
    }

    static void DestroyUi()
    {
        if (_root != null)
        {
            try { UnityEngine.Object.Destroy(_root); } catch { }
            _root = null;
        }
        _title = _body = _list = _status = null;
        _dlBtn = _skipBtn = _cancelBtn = null;
        RestoreMainMenuButtons();
    }
}

using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OthLan;

/// <summary>
/// 0.2.0 T6.7: Canvas update prompt (UiModal + UiNav). Owner of drawing/focus; lifecycle stays in <see cref="Updater"/>.
/// </summary>
internal static class UpdatePrompt
{
    const string ModalOwner = "update";
    static GameObject _root;
    static TMP_Text _title, _body;
    static readonly UIButtonSelector[] _btns = new UIButtonSelector[3];
    static readonly string[] _btnKeys = new string[3];
    static int _btnCount;
    static int _locGen = -1;
    static Updater.St _drawn;
    static string _drawnDetail;
    static float _drawnProgress = -1;
    static GameObject _prevSelected;

    public static bool Visible { get; private set; }
    public static int PromptFrame = -1;
    /// <summary>Self-test: index of EventSystem selection among visible prompt buttons, or -1 if none.</summary>
    public static int TestFocus
    {
        get
        {
            try
            {
                var es = UnityEngine.EventSystems.EventSystem.current;
                var cur = es != null ? es.currentSelectedGameObject : null;
                if (cur == null) return -1;
                for (var i = 0; i < _btnCount; i++)
                    if (_btns[i] != null && _btns[i].gameObject == cur) return i;
            }
            catch { }
            return -1;
        }
    }

    public static void Open()
    {
        if (Visible) { Refresh(); return; }
        EnsureUi();
        if (_root == null) return;
        // Do not hide buttonsContainer: CoopMenu.MainMenuVisible keys off Local Co-op activeInHierarchy
        // (Updater closes the prompt when the menu "disappears"). Dim + raycastTarget blocks clicks instead.
        try
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            _prevSelected = es != null ? es.currentSelectedGameObject : null;
        }
        catch { _prevSelected = null; }
        _root.SetActive(true);
        Visible = true;
        Refresh(force: true);
        FocusInitial();
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;
    }

    public static void Close()
    {
        Visible = false;
        PromptFrame = -1;
        if (_root != null && !_root.WasCollected) _root.SetActive(false);
        UiModal.Close(ModalOwner);
        RestoreMenuFocus();
    }

    static void RestoreMenuFocus()
    {
        try
        {
            if (_prevSelected != null && !_prevSelected.WasCollected && _prevSelected.activeInHierarchy)
                UiNav.Select(_prevSelected);
            else if (CoopMenu.LocalCoopButton != null)
                UiNav.Select(CoopMenu.LocalCoopButton);
        }
        catch { }
        _prevSelected = null;
    }

    public static void Tick()
    {
        if (!Visible) return;
        if (_locGen != Loc.Generation) { _locGen = Loc.Generation; Refresh(force: true); }
        else Refresh(force: false);
        try
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            var gp = UnityEngine.InputSystem.Gamepad.current;
            bool esc = kb != null && kb.escapeKey.wasPressedThisFrame || gp != null && gp.buttonEast.wasPressedThisFrame;
            if (esc) Updater.Later();
        }
        catch { }
        KeepFocus();
        PromptFrame = Time.frameCount;
    }

    /// <summary>Kept for Overlay.OnGUI hook; drawing is Canvas-only.</summary>
    public static void OnGUI() { }

    static void Refresh(bool force = false)
    {
        if (_root == null || _root.WasCollected) return;
        var st = Updater.State;
        var detail = Updater.Detail ?? "";
        var prog = Updater.Progress;
        if (!force && st == _drawn && detail == _drawnDetail && Math.Abs(prog - _drawnProgress) < 0.005f && _locGen == Loc.Generation)
            return;
        _drawn = st; _drawnDetail = detail; _drawnProgress = prog; _locGen = Loc.Generation;

        var latest = Updater.Latest;
        if (_title != null)
            _title.text = st == Updater.St.Offline ? Loc.T("upd.titleOffline") : Loc.F("upd.title", latest?.Version ?? "?");
        if (_body != null)
        {
            _body.text = st switch
            {
                Updater.St.Offline => Loc.F("upd.bodyOffline", string.IsNullOrEmpty(detail) ? "—" : detail),
                Updater.St.Downloading => Loc.F("upd.downloading", prog * 100),
                Updater.St.Ready => Loc.T("upd.ready"),
                Updater.St.Failed => Loc.F("upd.failed", detail),
                _ => Loc.F("upd.body", Plugin.Version, latest?.Version ?? "?") + "\n\n" + UpdateLogic.ShortNotes(latest?.Notes, Loc.Lang),
            };
        }
        RebuildButtons();
    }

    static void RebuildButtons()
    {
        var keys = Updater.ButtonKeys();
        _btnCount = Math.Min(keys.Length, _btns.Length);
        float[] xs = _btnCount switch
        {
            1 => new[] { 0f },
            2 => new[] { -170f, 170f },
            _ => new[] { -310f, 0f, 310f },
        };
        for (var i = 0; i < _btns.Length; i++)
        {
            if (_btns[i] == null) continue;
            var on = i < _btnCount;
            _btns[i].gameObject.SetActive(on);
            if (!on) continue;
            _btnKeys[i] = keys[i];
            SetLabel(_btns[i], Loc.T(keys[i]));
            var rt = _btns[i].GetComponent<RectTransform>();
            if (rt != null) rt.anchoredPosition = new Vector2(xs[i], -210f);
        }
        LinkButtons();
        FocusInitial();
    }

    static void FocusInitial()
    {
        LinkButtons();
        for (var i = 0; i < _btnCount; i++)
            if (_btns[i] != null && _btns[i].gameObject.activeInHierarchy)
            { UiNav.Select(_btns[i].gameObject); return; }
    }

    static void LinkButtons()
    {
        var list = new List<Selectable>(_btnCount);
        for (var i = 0; i < _btnCount; i++)
            if (_btns[i] != null && _btns[i].gameObject.activeInHierarchy) list.Add(_btns[i]);
        if (list.Count > 0) UiNav.LinkRow(list.ToArray());
    }

    static void KeepFocus()
    {
        var list = new List<Selectable>(_btnCount);
        for (var i = 0; i < _btnCount; i++)
            if (_btns[i] != null && _btns[i].gameObject.activeInHierarchy) list.Add(_btns[i]);
        if (list.Count == 0) return;
        UiNav.KeepFocus(list.ToArray(), list[0]);
    }

    static void PressIndex(int i)
    {
        if (i < 0 || i >= _btnCount) return;
        Updater.PressKey(_btnKeys[i]);
    }

    static void EnsureUi()
    {
        if (_root != null && !_root.WasCollected) return;
        _root = new GameObject("OTH-LAN UpdatePrompt");
        UnityEngine.Object.DontDestroyOnLoad(_root);
        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 810;
        var sc = _root.AddComponent<CanvasScaler>();
        sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        sc.referenceResolution = new Vector2(1920, 1080);
        sc.matchWidthOrHeight = 0.5f;
        _root.AddComponent<GraphicRaycaster>();

        var dim = Rect(_root.transform, "Dim", Vector2.zero, new Vector2(4000, 3000));
        var dimImg = dim.AddComponent<Image>();
        dimImg.color = new Color(0f, 0f, 0f, 0.6f);
        dimImg.raycastTarget = true;

        var panel = Rect(_root.transform, "Panel", Vector2.zero, new Vector2(900, 520));
        panel.AddComponent<Image>().color = new Color(0.07f, 0.07f, 0.08f, 0.96f);
        var p = panel.transform;

        TMP_FontAsset font = null; Color col = Color.white;
        try
        {
            var sample = UnityEngine.Object.FindObjectOfType<TMP_Text>();
            if (sample != null) { font = sample.font; col = sample.color; }
        }
        catch { }

        _title = MkText(p, "Title", new Vector2(0, 200), new Vector2(840, 48), 34, TextAlignmentOptions.Center, font, col);
        _body = MkText(p, "Body", new Vector2(0, 20), new Vector2(840, 280), 22, TextAlignmentOptions.Top, font, col);
        _body.color = new Color(col.r, col.g, col.b, 0.9f);

        for (var i = 0; i < 3; i++)
        {
            var idx = i;
            _btns[i] = MkButton(p, "Btn" + i, new Vector2(0, -210), new Vector2(280, 64));
            if (_btns[i] != null)
                _btns[i].add_OnPointerClickOrSubmit(Click(() => PressIndex(idx)));
        }
        _root.SetActive(false);
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
        catch (Exception e) { Plugin.L.LogWarning($"[Update] button clone: {e.Message}"); return null; }
    }

    static void SetLabel(UIButtonSelector b, string text)
    {
        if (b == null) return;
        foreach (var t in b.GetComponentsInChildren<TMP_Text>(true)) { t.richText = true; t.text = text; }
    }

    static Il2CppSystem.Action<UIButtonSelector> Click(Action a) =>
        (Il2CppSystem.Action<UIButtonSelector>)((UIButtonSelector _) => { try { a(); } catch (Exception e) { Plugin.L.LogWarning($"[Update] click: {e.Message}"); } });
}

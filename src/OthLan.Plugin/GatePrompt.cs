using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace OthLan;

/// <summary>
/// 0.2.6.1: Canvas gate dialog for Unverified (Ask) and a visible notice for Incompatible (Off).
/// Buttons are built as plain Unity UI (Image + Button + TMP) so they always appear even when the
/// main-menu UIButtonSelector template is not ready yet (cloning it alone left a dialog with no buttons).
/// </summary>
internal static class GatePrompt
{
    const string ModalOwner = "gate";
    public enum Mode { None, Ask, Off }
    public static Mode Current { get; private set; } = Mode.None;
    public static bool Visible => Current != Mode.None;
    /// <summary>True when at least one answer button exists and is active.</summary>
    public static bool HasButtons
    {
        get
        {
            for (var i = 0; i < _btnCount; i++)
                if (_btnGo[i] != null && !_btnGo[i].WasCollected && _btnGo[i].activeInHierarchy) return true;
            return false;
        }
    }
    public static int PromptFrame = -1;

    static GameObject _root, _panel;
    static TMP_Text _title, _body;
    static readonly GameObject[] _btnGo = new GameObject[2];
    static readonly Selectable[] _btnSel = new Selectable[2];
    static readonly TMP_Text[] _btnLabel = new TMP_Text[2];
    static readonly Button[] _btnUnity = new Button[2];
    static int _btnCount;
    static int _locGen = -1;
    static GameObject _prevSelected;
    static bool _autoOpened;
    static readonly List<Delegate> _keep = new();
    static Action _act0, _act1;
    static Transform _panelTf;
    static TMP_FontAsset _font;
    static Color _col = Color.white;

    public static void Tick()
    {
        if (!Visible && !_autoOpened)
        {
            // Wait until the main menu exists so CoopMenu can also show Local Co-op; buttons themselves no longer need it.
            if (BuildGate.NeedsChoice && MainMenuReady()) { _autoOpened = true; OpenAsk(); }
            else if (BuildGate.Outcome == GateLogic.Outcome.Off && MainMenuReady()) { _autoOpened = true; OpenOff(); }
        }
        if (!Visible) return;

        // If EnsureUi ran before we could build buttons, keep retrying.
        if (!HasButtons) EnsureButtons();

        if (_locGen != Loc.Generation) { _locGen = Loc.Generation; Refresh(); }

        try
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            var gp = UnityEngine.InputSystem.Gamepad.current;
            bool esc = kb != null && kb.escapeKey.wasPressedThisFrame || gp != null && gp.buttonEast.wasPressedThisFrame;
            bool ok = kb != null && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)
                      || gp != null && gp.buttonSouth.wasPressedThisFrame;
            if (Current == Mode.Ask)
            {
                if (ok) { BuildGate.UserChoice(true, "gateprompt-key"); Close(); }
                else if (esc) { BuildGate.UserChoice(false, "gateprompt-esc"); Close(); }
            }
            else if (esc || ok) Close();
        }
        catch { }

        KeepFocus();
        PromptFrame = Time.frameCount;
        // Autopilot "dialog visible" only when buttons exist (a title-only box is not answerable).
        if (HasButtons) Overlay.GateDialogFrame = PromptFrame;
    }

    static bool MainMenuReady()
    {
        try
        {
            var m = UnityEngine.Object.FindObjectOfType<ContextUIMainMenu>();
            return m != null && m.MultiplayerButton != null;
        }
        catch { return false; }
    }

    public static void OpenAsk()
    {
        if (!BuildGate.NeedsChoice) return;
        Open(Mode.Ask);
    }

    public static void OpenOff()
    {
        if (BuildGate.Outcome != GateLogic.Outcome.Off && BuildGate.Outcome != GateLogic.Outcome.DisabledByUser) return;
        Open(Mode.Off);
    }

    static void Open(Mode mode)
    {
        if (Visible && Current == mode) { Refresh(); return; }
        if (UiModal.Current != null && UiModal.Current != ModalOwner)
        {
            Plugin.L.LogInfo($"[Gate] prompt deferred (modal={UiModal.Current})");
            return;
        }
        if (!UiModal.TryOpen(ModalOwner) && UiModal.Current != ModalOwner) return;
        EnsureUi();
        if (_root == null) return;
        try
        {
            var es = EventSystem.current;
            _prevSelected = es != null ? es.currentSelectedGameObject : null;
        }
        catch { _prevSelected = null; }
        Current = mode;
        _root.SetActive(true);
        EnsureButtons();
        Refresh();
        FocusInitial();
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;
        Plugin.L.LogInfo($"[Gate] Canvas prompt shown mode={mode} tier={BuildGate.Tier} outcome={BuildGate.Outcome} buttons={HasButtons}");
    }

    public static void Close()
    {
        var was = Current;
        Current = Mode.None;
        PromptFrame = -1;
        if (_root != null && !_root.WasCollected) _root.SetActive(false);
        UiModal.Close(ModalOwner);
        RestoreMenuFocus();
        if (was != Mode.None) Plugin.L.LogInfo($"[Gate] Canvas prompt closed (was {was})");
    }

    public static void OnGateOutcomeChanged()
    {
        if (Current == Mode.Ask && !BuildGate.NeedsChoice) Close();
    }

    public static void OnGUI() { }

    static void Refresh()
    {
        if (_root == null || _root.WasCollected) return;
        _locGen = Loc.Generation;
        var both = !LangWatch.GameKnown;
        if (_title != null)
        {
            if (Current == Mode.Ask)
                _title.text = both
                    ? Loc.En("dlg.title", Plugin.Version) + "  /  " + Loc.TIn("ru", "dlg.title").Replace("{0}", Plugin.Version)
                    : Loc.F("dlg.title", Plugin.Version);
            else
                _title.text = both
                    ? Loc.En("dlg.offTitle", Plugin.Version) + "  /  " + Loc.TIn("ru", "dlg.offTitle").Replace("{0}", Plugin.Version)
                    : Loc.F("dlg.offTitle", Plugin.Version);
        }
        if (_body != null)
        {
            if (Current == Mode.Ask)
            {
                _body.text = both
                    ? Loc.TIn("ru", "dlg.body") + "\n\n" + Loc.TIn("en", "dlg.body")
                    : Loc.T("dlg.body");
            }
            else
            {
                var reason = BuildGate.Reasons != null && BuildGate.Reasons.Count > 0
                    ? string.Join("; ", BuildGate.Reasons) : BuildGate.Status();
                string OffBody(string lang) =>
                    string.Format(System.Globalization.CultureInfo.InvariantCulture, Loc.TIn(lang, "dlg.offBody"), Plugin.Version, reason);
                _body.text = both
                    ? OffBody("ru") + "\n\n" + OffBody("en")
                    : Loc.F("dlg.offBody", Plugin.Version, reason);
            }
        }
        RebuildButtons();
    }

    static void RebuildButtons()
    {
        EnsureButtons();
        if (Current == Mode.Ask)
        {
            _btnCount = 2;
            SetBtn(0, -190f, bothKey("dlg.continue"), () => { BuildGate.UserChoice(true, "gateprompt"); Close(); });
            SetBtn(1, 190f, bothKey("dlg.disable"), () => { BuildGate.UserChoice(false, "gateprompt"); Close(); });
        }
        else
        {
            _btnCount = 1;
            SetBtn(0, 0f, bothKey("dlg.offOk"), () => Close());
            if (_btnGo[1] != null && !_btnGo[1].WasCollected) _btnGo[1].SetActive(false);
        }
        LinkButtons();
        FocusInitial();
    }

    static string bothKey(string key)
    {
        if (!LangWatch.GameKnown) return Loc.TIn("ru", key) + " / " + Loc.TIn("en", key);
        return Loc.T(key);
    }

    static void SetBtn(int i, float x, string label, Action act)
    {
        if (_btnGo[i] == null || _btnGo[i].WasCollected) return;
        _btnGo[i].SetActive(true);
        var rt = _btnGo[i].GetComponent<RectTransform>();
        if (rt != null) rt.anchoredPosition = new Vector2(x, -220f);
        if (_btnLabel[i] != null) _btnLabel[i].text = label;
        if (i == 0) _act0 = act; else _act1 = act;
    }

    static void FocusInitial()
    {
        LinkButtons();
        for (var i = 0; i < _btnCount; i++)
            if (_btnGo[i] != null && !_btnGo[i].WasCollected && _btnGo[i].activeInHierarchy)
            { UiNav.Select(_btnGo[i]); return; }
    }

    static void LinkButtons()
    {
        var list = new List<Selectable>(_btnCount);
        for (var i = 0; i < _btnCount; i++)
            if (_btnSel[i] != null && _btnGo[i] != null && _btnGo[i].activeInHierarchy) list.Add(_btnSel[i]);
        if (list.Count > 0) UiNav.LinkRow(list.ToArray());
    }

    static void KeepFocus()
    {
        var list = new List<Selectable>(_btnCount);
        for (var i = 0; i < _btnCount; i++)
            if (_btnSel[i] != null && _btnGo[i] != null && _btnGo[i].activeInHierarchy) list.Add(_btnSel[i]);
        if (list.Count == 0) return;
        UiNav.KeepFocus(list.ToArray(), list[0]);
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

    static void EnsureUi()
    {
        if (_root != null && !_root.WasCollected) return;
        _root = new GameObject("OTH-LAN GatePrompt");
        UnityEngine.Object.DontDestroyOnLoad(_root);
        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 820;
        var sc = _root.AddComponent<CanvasScaler>();
        sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        sc.referenceResolution = new Vector2(1920, 1080);
        sc.matchWidthOrHeight = 0.5f;
        _root.AddComponent<GraphicRaycaster>();

        var dim = Rect(_root.transform, "Dim", Vector2.zero, new Vector2(4000, 3000));
        var dimImg = dim.AddComponent<Image>();
        dimImg.color = new Color(0f, 0f, 0f, 0.65f);
        dimImg.raycastTarget = true;

        // Taller panel so body + two buttons never collide.
        _panel = Rect(_root.transform, "Panel", Vector2.zero, new Vector2(960, 580));
        _panel.AddComponent<Image>().color = new Color(0.07f, 0.07f, 0.08f, 0.97f);
        _panelTf = _panel.transform;

        try
        {
            var sample = UnityEngine.Object.FindObjectOfType<TMP_Text>();
            if (sample != null) { _font = sample.font; _col = sample.color; }
        }
        catch { }

        _title = MkText(_panelTf, "Title", new Vector2(0, 230), new Vector2(900, 56), 34, TextAlignmentOptions.Center, _font, _col);
        _body = MkText(_panelTf, "Body", new Vector2(0, 40), new Vector2(900, 300), 22, TextAlignmentOptions.Top, _font, _col);
        _body.color = new Color(_col.r, _col.g, _col.b, 0.9f);

        EnsureButtons();
        _root.SetActive(false);
    }

    static void EnsureButtons()
    {
        if (_panelTf == null) return;
        for (var i = 0; i < 2; i++)
        {
            if (_btnGo[i] != null && !_btnGo[i].WasCollected) continue;
            var idx = i;
            _btnGo[i] = MkFallbackButton(_panelTf, "Btn" + i, new Vector2(i == 0 ? -190f : 190f, -220f), new Vector2(340, 72));
            if (_btnGo[i] == null) continue;
            _btnUnity[i] = _btnGo[i].GetComponent<Button>();
            _btnSel[i] = _btnUnity[i];
            _btnLabel[i] = _btnGo[i].GetComponentInChildren<TMP_Text>(true);
            if (_btnUnity[i] != null)
            {
                // Il2Cpp Button.onClick
                try
                {
                    _btnUnity[i].onClick.AddListener((UnityEngine.Events.UnityAction)(() =>
                    {
                        try { (idx == 0 ? _act0 : _act1)?.Invoke(); }
                        catch (Exception e) { Plugin.L.LogWarning($"[Gate] click: {e.Message}"); }
                    }));
                }
                catch (Exception e)
                {
                    Plugin.L.LogWarning($"[Gate] onClick wire: {e.Message}");
                    // Fallback: also attach via a tiny click proxy using EventTrigger if needed
                    WirePointerClick(_btnGo[i], () => { (idx == 0 ? _act0 : _act1)?.Invoke(); });
                }
            }
            else WirePointerClick(_btnGo[i], () => { (idx == 0 ? _act0 : _act1)?.Invoke(); });
            Plugin.L.LogInfo($"[Gate] button[{i}] ready (fallback UI)");
        }
    }

    static void WirePointerClick(GameObject go, Action a)
    {
        try
        {
            var et = go.GetComponent<EventTrigger>() ?? go.AddComponent<EventTrigger>();
            var entry = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
            entry.callback.AddListener((UnityEngine.Events.UnityAction<BaseEventData>)(_ => { try { a(); } catch { } }));
            et.triggers.Add(entry);
        }
        catch (Exception e) { Plugin.L.LogWarning($"[Gate] EventTrigger: {e.Message}"); }
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

    /// <summary>Always-visible answer button: solid Image + Button + TMP label (no game template).</summary>
    static GameObject MkFallbackButton(Transform parent, string name, Vector2 pos, Vector2 size)
    {
        try
        {
            var go = Rect(parent, name, pos, size);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.18f, 0.45f, 0.85f, 1f);
            img.raycastTarget = true;
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            var colors = btn.colors;
            colors.normalColor = new Color(0.18f, 0.45f, 0.85f, 1f);
            colors.highlightedColor = new Color(0.28f, 0.55f, 0.95f, 1f);
            colors.pressedColor = new Color(0.12f, 0.32f, 0.7f, 1f);
            colors.selectedColor = new Color(0.28f, 0.55f, 0.95f, 1f);
            colors.disabledColor = new Color(0.3f, 0.3f, 0.3f, 0.6f);
            btn.colors = colors;
            btn.transition = Selectable.Transition.ColorTint;

            var labelGo = Rect(go.transform, "Label", Vector2.zero, size);
            var t = labelGo.AddComponent<TextMeshProUGUI>();
            if (_font != null) t.font = _font;
            t.color = Color.white;
            t.fontSize = 26;
            t.alignment = TextAlignmentOptions.Center;
            t.raycastTarget = false;
            t.enableWordWrapping = true;
            t.text = "…";
            go.SetActive(true);
            return go;
        }
        catch (Exception e)
        {
            Plugin.L.LogWarning($"[Gate] fallback button: {e.Message}");
            return null;
        }
    }
}

using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OthLan;

/// <summary>
/// 0.2.0 T6.2: connection progress without F7 — bottom toast + error modal (UiModal + UiNav).
/// Text owner is LanState; this only draws.
/// </summary>
internal static class JoinToast
{
    const string ModalOwner = "joinToast";
    const float HideAfterConnectedS = 3f;
    static readonly HashSet<string> ProgressKeys = new(StringComparer.Ordinal)
    {
        "status.querying", "status.joining", "status.connectingSlot", "status.connected",
        "status.freezeRejoin", "status.modsMissing", "status.modsSkipped",
    };
    static readonly HashSet<string> ErrorKeys = new(StringComparer.Ordinal)
    {
        "status.hostSilent", "status.modMismatch", "status.gameMismatch", "status.full",
        "status.noArea", "status.noSuchArea", "status.joinNotStarted", "status.connectFailed",
        "status.modsCancelled", "status.badAddress", "status.noAddress",
        "recover.clientTimeout", "recover.gameSaid", "recover.hostTimeout", "recover.netNotStarted", "recover.status",
    };

    static GameObject _root, _toastGo, _modalGo;
    static TMP_Text _toastText, _modalBody;
    static UIButtonSelector _okBtn;
    static float _hideAt = -1;
    static bool _hooked;

    public static void Tick()
    {
        EnsureHook();
        if (_hideAt > 0 && Time.realtimeSinceStartup >= _hideAt)
        {
            _hideAt = -1;
            SetToast(false, null);
        }
        if (_modalGo != null && _modalGo.activeSelf)
            KeepModalFocus();
    }

    static void EnsureHook()
    {
        if (_hooked) return;
        _hooked = true;
        LanState.StatusChanged += OnStatus;
    }

    static void OnStatus(Msg msg)
    {
        if (msg == null || string.IsNullOrEmpty(msg.Key)) return;
        var key = msg.Key;
        if (ErrorKeys.Contains(key) || key.StartsWith("recover.", StringComparison.Ordinal))
        {
            ShowError(msg);
            return;
        }
        if (ProgressKeys.Contains(key))
        {
            SetToast(true, msg.ToString());
            if (key == "status.connected")
                _hideAt = Time.realtimeSinceStartup + HideAfterConnectedS;
            else
                _hideAt = -1;
        }
    }

    static void ShowError(Msg msg)
    {
        SetToast(false, null);
        _hideAt = -1;
        if (!UiModal.TryOpen(ModalOwner)) return;
        EnsureUi();
        if (_modalGo != null) _modalGo.SetActive(true);
        if (_modalBody != null) _modalBody.text = msg.ToString() + "\n\n" + Loc.T("joinToast.detailsF7");
        if (_okBtn != null) UiNav.Select(_okBtn.gameObject);
    }

    static void CloseError()
    {
        if (_modalGo != null) _modalGo.SetActive(false);
        UiModal.Close(ModalOwner);
        try
        {
            if (CoopMenu.LocalCoopButton != null) UiNav.Select(CoopMenu.LocalCoopButton);
        }
        catch { }
    }

    /// <summary>Short note above the session panel (save / resync / host diag). Does not open the error modal.</summary>
    public static void Show(string text, float seconds = 4f)
    {
        EnsureUi();
        SetToast(true, text ?? "");
        _hideAt = Time.realtimeSinceStartup + Math.Max(1f, seconds);
    }

    static void SetToast(bool on, string text)
    {
        EnsureUi();
        if (_toastGo != null) _toastGo.SetActive(on);
        if (on && _toastText != null) _toastText.text = text ?? "";
    }

    static void KeepModalFocus()
    {
        if (_okBtn == null) return;
        try
        {
            var sel = _okBtn.GetComponent<Selectable>() ?? _okBtn as Selectable;
            if (sel != null) UiNav.KeepFocus(new[] { sel }, sel);
        }
        catch { }
        try
        {
            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape) || UnityEngine.Input.GetKeyDown(KeyCode.Return)
                || UnityEngine.Input.GetKeyDown(KeyCode.JoystickButton0))
                CloseError();
        }
        catch { }
    }

    static void EnsureUi()
    {
        if (_root != null && !_root.WasCollected) return;
        _root = new GameObject("OTH-LAN JoinToast");
        UnityEngine.Object.DontDestroyOnLoad(_root);
        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 3100; // above SessionPanel (3000), still above the game HUD
        var sc = _root.AddComponent<CanvasScaler>();
        sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        sc.referenceResolution = new Vector2(1920, 1080);
        sc.matchWidthOrHeight = 0.5f;
        // Toast must not steal input; modal raycaster only when open.
        _root.AddComponent<GraphicRaycaster>();

        TMP_FontAsset font = null; Color col = Color.white;
        try
        {
            var sample = UnityEngine.Object.FindObjectOfType<TMP_Text>();
            if (sample != null) { font = sample.font; col = sample.color; }
        }
        catch { }

        _toastGo = Rect(_root.transform, "Toast", new Vector2(0, -460), new Vector2(1100, 64));
        var toastBg = _toastGo.AddComponent<Image>();
        toastBg.color = new Color(0.05f, 0.05f, 0.06f, 0.85f);
        toastBg.raycastTarget = false;
        _toastText = MkText(_toastGo.transform, "Text", Vector2.zero, new Vector2(1060, 56), 26, TextAlignmentOptions.Center, font, col);
        _toastGo.SetActive(false);

        _modalGo = new GameObject("ErrorModal");
        _modalGo.transform.SetParent(_root.transform, false);
        var mrt = _modalGo.AddComponent<RectTransform>();
        mrt.anchorMin = Vector2.zero; mrt.anchorMax = Vector2.one;
        mrt.offsetMin = mrt.offsetMax = Vector2.zero;

        var dim = Rect(_modalGo.transform, "Dim", Vector2.zero, new Vector2(4000, 3000));
        var dimImg = dim.AddComponent<Image>();
        dimImg.color = new Color(0f, 0f, 0f, 0.55f);
        dimImg.raycastTarget = true;

        var panel = Rect(_modalGo.transform, "Panel", Vector2.zero, new Vector2(720, 320));
        panel.AddComponent<Image>().color = new Color(0.07f, 0.07f, 0.08f, 0.96f);
        _modalBody = MkText(panel.transform, "Body", new Vector2(0, 40), new Vector2(660, 200), 24, TextAlignmentOptions.Center, font, col);
        _okBtn = MkButton(panel.transform, "OK", new Vector2(0, -110), new Vector2(220, 56));
        if (_okBtn != null)
        {
            try
            {
                foreach (var t in _okBtn.GetComponentsInChildren<TMP_Text>(true))
                    t.text = Loc.T("joinToast.ok");
            }
            catch { }
            _okBtn.add_OnPointerClickOrSubmit(Click(CloseError));
        }
        _modalGo.SetActive(false);
    }

    static Il2CppSystem.Action<UIButtonSelector> Click(Action a) =>
        (Il2CppSystem.Action<UIButtonSelector>)((UIButtonSelector _) => { try { a(); } catch (Exception e) { Plugin.L.LogWarning($"[JoinToast] click: {e.Message}"); } });

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
            var rt = go.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = pos;
                rt.sizeDelta = size;
            }
            return go.GetComponent<UIButtonSelector>();
        }
        catch { return null; }
    }
}

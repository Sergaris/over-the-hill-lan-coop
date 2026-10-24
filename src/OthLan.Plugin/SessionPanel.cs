using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OthLan;

/// <summary>
/// 0.2.0 T6.4: Canvas roster and actions. Gamepad order is <see cref="UiNav"/>; one modal slot via <see cref="UiModal"/>.
/// </summary>
internal static class SessionPanel
{
    const string Owner = "session";
    static GameObject _root, _diagGo;
    static TMP_Text _title, _header, _list;
    static UIButtonSelector _resync, _save, _diag, _leave, _close;
    static Selectable[] _col = Array.Empty<Selectable>();
    static Selectable _first;
    static bool _open;
    static int _locGen = -1;
    static string _body = "";
    static readonly List<Delegate> _keep = new();

    public static bool IsOpen => _open && _root != null && !_root.WasCollected && _root.activeSelf;

    public static bool Open(UIButtonSelector template, bool host)
    {
        if (IsOpen) return true;
        if (!UiModal.TryOpen(Owner))
        {
            Plugin.L.LogInfo("[Session] panel busy: another dialog is open");
            return false;
        }
        if (!Ensure(template)) { UiModal.Close(Owner); return false; }
        if (_diagGo != null) _diagGo.SetActive(host);
        Relink();
        ApplyLabels();
        SetBody(_body);
        _root.SetActive(true);
        _open = true;
        if (_first != null) UiNav.Select(_first.gameObject);
        return true;
    }

    public static void Close()
    {
        if (!_open && (_root == null || _root.WasCollected || !_root.activeSelf)) return;
        _open = false;
        if (_root != null && !_root.WasCollected) _root.SetActive(false);
        UiModal.Close(Owner);
        CoopSessionPause.Focus();
    }

    public static void SetBody(string text)
    {
        _body = text ?? "";
        if (_list == null || _list.WasCollected) return;
        _list.text = _body.Length == 0 ? Loc.T("session.empty") : _body;
    }

    public static void Tick()
    {
        if (!IsOpen) return;
        if (_locGen != Loc.Generation) ApplyLabels();
        if (_col.Length > 0 && _first != null) UiNav.KeepFocus(_col, _first);
        try
        {
            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape) || UnityEngine.Input.GetKeyDown(KeyCode.JoystickButton1))
                Close();
        }
        catch { }
    }

    static bool Ensure(UIButtonSelector template)
    {
        if (_root != null && !_root.WasCollected && _resync != null) return true;
        if (template == null)
        {
            Plugin.L.LogWarning("[Session] no button template for the session panel");
            return false;
        }
        Build(template);
        return _root != null && _resync != null;
    }

    static void Build(UIButtonSelector template)
    {
        _root = new GameObject("OTH-LAN SessionPanel");
        UnityEngine.Object.DontDestroyOnLoad(_root);
        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 3000;
        var sc = _root.AddComponent<CanvasScaler>();
        sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        sc.referenceResolution = new Vector2(1920, 1080);
        sc.matchWidthOrHeight = 0.5f;
        _root.AddComponent<GraphicRaycaster>();
        var font = FontOf(template);
        Dim();
        var panel = Box("Panel", Vector2.zero, new Vector2(920, 860), new Color(0.07f, 0.07f, 0.08f, 0.96f));
        _title = Text(panel.transform, "Title", new Vector2(0, 380), new Vector2(860, 48), 32, font);
        _header = Text(panel.transform, "Header", new Vector2(0, 320), new Vector2(860, 36), 22, font);
        _list = Text(panel.transform, "Players", new Vector2(0, 80), new Vector2(860, 400), 24, font);
        _list.alignment = TextAlignmentOptions.TopLeft;
        _resync = ActionButton(template, panel.transform, "Resync", new Vector2(0, -180));
        _save = ActionButton(template, panel.transform, "Save", new Vector2(0, -244));
        _diag = ActionButton(template, panel.transform, "DiagAll", new Vector2(0, -308));
        _diagGo = _diag != null ? _diag.gameObject : null;
        _leave = ActionButton(template, panel.transform, "Leave", new Vector2(0, -372));
        _close = ActionButton(template, panel.transform, "Close", new Vector2(0, -436));
        Wire(_resync, CoopSession.Resync);
        Wire(_save, CoopSession.SaveDiag);
        Wire(_diag, CoopSession.DiagAll);
        Wire(_leave, CoopSession.Leave);
        Wire(_close, Close);
        _root.SetActive(false);
    }

    static void Dim()
    {
        var go = new GameObject("Dim");
        go.transform.SetParent(_root.transform, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        var img = go.AddComponent<Image>();
        img.color = new Color(0f, 0f, 0f, 0.55f);
        img.raycastTarget = true;
    }

    static GameObject Box(string name, Vector2 pos, Vector2 size, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_root.transform, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        var img = go.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = true;
        return go;
    }

    static TMP_Text Text(Transform parent, string name, Vector2 pos, Vector2 size, float fontSize, TMP_FontAsset font)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        var t = go.AddComponent<TextMeshProUGUI>();
        if (font != null) t.font = font;
        t.fontSize = fontSize;
        t.alignment = TextAlignmentOptions.Center;
        t.color = Color.white;
        t.richText = true;
        t.raycastTarget = false;
        t.enableWordWrapping = true;
        return t;
    }

    static UIButtonSelector ActionButton(UIButtonSelector template, Transform parent, string name, Vector2 pos)
    {
        var b = CoopSessionPause.Clone(template, parent, name);
        if (b == null) return null;
        var rt = b.GetComponent<RectTransform>();
        if (rt != null)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(780, 56);
        }
        return b;
    }

    static void Wire(UIButtonSelector b, Action a)
    {
        if (b == null) return;
        var d = new Action<UIButtonSelector>(_ => { try { a(); } catch (Exception e) { Plugin.L.LogWarning($"[Session] action: {e.Message}"); } });
        _keep.Add(d);
        b.add_OnPointerClickOrSubmit(DelegateSupport.ConvertDelegate<Il2CppSystem.Action<UIButtonSelector>>(d));
    }

    static void Relink()
    {
        var list = new List<Selectable>(5);
        AddCol(list, _resync);
        AddCol(list, _save);
        if (_diagGo != null && _diagGo.activeSelf) AddCol(list, _diag);
        AddCol(list, _leave);
        AddCol(list, _close);
        _col = list.ToArray();
        _first = _col.Length > 0 ? _col[0] : null;
        UiNav.LinkColumn(_col);
    }

    static void AddCol(List<Selectable> list, UIButtonSelector b)
    {
        if (b == null) return;
        list.Add(b);
    }

    static void ApplyLabels()
    {
        _locGen = Loc.Generation;
        if (_title != null) _title.text = Loc.T("session.title");
        if (_header != null) _header.text = Loc.T("session.header");
        Paint(_resync, Loc.T("session.resync"));
        Paint(_save, Loc.T("session.saveDiag"));
        Paint(_diag, Loc.T("session.diagAll"));
        Paint(_leave, Loc.T("session.leave"));
        Paint(_close, Loc.T("session.close"));
        SetBody(_body);
    }

    static void Paint(UIButtonSelector b, string text)
    {
        if (b == null) return;
        foreach (var t in b.GetComponentsInChildren<TMP_Text>(true))
        {
            t.richText = true;
            t.enableWordWrapping = false;
            t.alignment = TextAlignmentOptions.Center;
            t.text = text;
        }
    }

    static TMP_FontAsset FontOf(UIButtonSelector template)
    {
        try
        {
            var t = template != null ? template.GetComponentInChildren<TMP_Text>(true) : null;
            return t != null ? t.font : null;
        }
        catch { return null; }
    }
}

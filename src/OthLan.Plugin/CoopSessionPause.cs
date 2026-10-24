using System;
using System.Collections.Generic;
using FunCore.Managers;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OthLan;

/// <summary>
/// 0.2.0 T6.4: "Local Co-op" clone on <see cref="ContextUIPauseMenu"/> (same idea as <see cref="CoopMenu"/> on the main menu).
/// Does not touch the main-menu button.
/// </summary>
internal static class CoopSessionPause
{
    static ContextUIPauseMenu _menu;
    static UIButtonSelector _template, _ours;
    static GameObject _button;
    static TMP_Text _label;
    static string _want;
    static bool _upper, _broken, _warned;
    static float _next;
    static int _openReq, _navLogs;
    static Selectable _savedDown;
    static bool _spliced;
    static readonly List<Delegate> _keep = new();

    public static bool MenuVisible => _menu != null && !_menu.WasCollected && _menu.gameObject.activeInHierarchy;
    public static ContextUIPauseMenu Menu => MenuVisible ? _menu : null;
    public static UIButtonSelector Template => _template != null && !_template.WasCollected ? _template : null;

    public static void Tick()
    {
        if (_broken) return;
        try { Inner(); }
        catch (Exception e)
        {
            _broken = true;
            Plugin.L.LogError($"[Session] pause button disabled: {e}");
            Remove();
        }
    }

    public static bool ConsumeOpen()
    {
        if (_openReq == 0) return false;
        _openReq = 0;
        return true;
    }

    public static void Focus()
    {
        try { if (_button != null && !_button.WasCollected) UiNav.Select(_button); } catch { }
    }

    public static void Remove()
    {
        Unlink();
        try { if (_button != null && !_button.WasCollected) UnityEngine.Object.Destroy(_button); } catch { }
        _button = null; _ours = null; _label = null; _menu = null; _template = null;
        _savedDown = null; _spliced = false;
    }

    public static UIButtonSelector Clone(UIButtonSelector template, Transform parent, string name)
    {
        var go = UnityEngine.Object.Instantiate(template.gameObject, parent);
        go.name = name;
        go.SetActive(true);
        StripLoc(go);
        var b = go.GetComponent<UIButtonSelector>();
        ClearClicks(b);
        return b;
    }

    static void Inner()
    {
        if (_label != null && !_label.WasCollected) GuardLabel();
        if (Time.realtimeSinceStartup < _next && _button != null && !_button.WasCollected && MenuVisible)
        {
            CheckNav();
            return;
        }
        _next = Time.realtimeSinceStartup + 0.5f;
        var menu = FindMenu();
        if (menu == null) { Remove(); return; }
        _menu = menu;
        if (_button == null || _button.WasCollected) AddButton(menu);
        else CheckNav();
    }

    static ContextUIPauseMenu FindMenu()
    {
        try
        {
            var st = HybridSingleton<GameStateManager>.Instance?.PeekState()?.TryCast<StatePauseMenu>();
            var ui = st != null ? st.contextUIPauseMenu : null;
            if (Alive(ui)) return ui;
        }
        catch { }
        try
        {
            var m = UnityEngine.Object.FindObjectOfType<ContextUIPauseMenu>();
            if (Alive(m)) return m;
        }
        catch { }
        return null;
    }

    static bool Alive(ContextUIPauseMenu m) => m != null && !m.WasCollected && m.gameObject.activeInHierarchy;

    static void AddButton(ContextUIPauseMenu menu)
    {
        _template = menu.SettingsMenuButton != null ? menu.SettingsMenuButton : menu.ResumeButton;
        if (_template == null || _template.transform.parent == null)
        {
            if (!_warned) Plugin.L.LogWarning("[Session] pause menu has no Settings/Resume button; session panel stays closed");
            _warned = true;
            return;
        }
        var sample = _template.GetComponentInChildren<TMP_Text>(true);
        var raw = sample != null ? sample.text ?? "" : "";
        _upper = raw.Length > 0 && raw == raw.ToUpperInvariant();
        _ours = Clone(_template, _template.transform.parent, "OTH-LAN SessionButton");
        _ours.transform.SetSiblingIndex(_template.transform.GetSiblingIndex() + 1);
        _button = _ours.gameObject;
        _label = _ours.GetComponentInChildren<TMP_Text>(true);
        _want = Want();
        Paint(_ours, _want);
        _ours.add_OnPointerClickOrSubmit(Click(OnClick));
        Splice();
        Plugin.L.LogInfo("[Session] pause button added on ContextUIPauseMenu");
    }

    static void OnClick() { _openReq++; }

    static void GuardLabel()
    {
        var want = Want();
        if (_label.text != want) _label.text = want;
        _want = want;
    }

    static string Want()
    {
        var s = Loc.T("coop.button");
        return _upper ? s.ToUpperInvariant() : s;
    }

    static void CheckNav()
    {
        if (_template == null || _ours == null || !_button.activeInHierarchy || !_template.gameObject.activeInHierarchy) return;
        if (_template.navigation.selectOnDown != _ours) Splice();
    }

    static void Splice()
    {
        if (!_spliced)
        {
            var prev = _template.navigation.selectOnDown;
            _savedDown = prev != null && prev != _ours ? prev : null;
            _spliced = true;
        }
        var down = _savedDown;
        var n = _template.navigation;
        n.mode = Navigation.Mode.Explicit;
        n.selectOnDown = _ours;
        _template.navigation = n;
        var o = _ours.navigation;
        o.mode = Navigation.Mode.Explicit;
        o.selectOnUp = _template;
        o.selectOnDown = down != null && down != _ours ? down : null;
        _ours.navigation = o;
        if (down != null && down != _ours)
        {
            var d = down.navigation;
            d.mode = Navigation.Mode.Explicit;
            d.selectOnUp = _ours;
            down.navigation = d;
        }
        InsertSelectable();
        if (_navLogs++ < 3) Plugin.L.LogInfo("[Session] pause-menu navigation linked");
    }

    static void Unlink()
    {
        if (_spliced && _template != null && !_template.WasCollected)
        {
            var n = _template.navigation;
            if (_ours != null && n.selectOnDown == _ours) { n.selectOnDown = _savedDown; _template.navigation = n; }
            if (_savedDown != null && !_savedDown.WasCollected && _savedDown.navigation.selectOnUp == _ours)
            {
                var d = _savedDown.navigation;
                d.selectOnUp = _template;
                _savedDown.navigation = d;
            }
        }
        try
        {
            var st = HybridSingleton<GameStateManager>.Instance?.PeekState()?.TryCast<StatePauseMenu>();
            var gl = st != null ? st.selectables : null;
            if (gl != null && _ours != null && gl.Contains(_ours)) gl.Remove(_ours);
        }
        catch { }
        _spliced = false;
    }

    static void InsertSelectable()
    {
        try
        {
            var st = HybridSingleton<GameStateManager>.Instance?.PeekState()?.TryCast<StatePauseMenu>();
            var gl = st != null ? st.selectables : null;
            if (gl == null || gl.Contains(_ours)) return;
            var at = gl.IndexOf(_template);
            if (at >= 0) gl.Insert(at + 1, _ours); else gl.Add(_ours);
        }
        catch (Exception e) { if (_navLogs == 0) Plugin.L.LogInfo($"[Session] pause selectables not updated ({e.Message})"); }
    }

    static void StripLoc(GameObject go)
    {
        foreach (var c in go.GetComponentsInChildren<MonoBehaviour>(true))
        {
            var tn = c.GetIl2CppType().FullName ?? "";
            if (!tn.Contains("Locali")) continue;
            c.enabled = false;
            UnityEngine.Object.Destroy(c);
        }
        foreach (var ft in go.GetComponentsInChildren<global::FunTextTMP>(true))
            try { ft.ClearTextChangeHandler(); } catch { }
    }

    static void ClearClicks(UIButtonSelector b)
    {
        if (b == null) return;
        try { b.OnPointerClickOrSubmit = null; } catch { }
        try
        {
            var btn = b.GetComponent<Button>();
            if (btn != null) btn.onClick.RemoveAllListeners();
        }
        catch { }
    }

    static void Paint(UIButtonSelector b, string text)
    {
        foreach (var t in b.GetComponentsInChildren<TMP_Text>(true)) { t.richText = true; t.text = text; }
    }

    static Il2CppSystem.Action<UIButtonSelector> Click(Action a)
    {
        var d = new Action<UIButtonSelector>(_ => { try { a(); } catch (Exception e) { Plugin.L.LogWarning($"[Session] click: {e.Message}"); } });
        _keep.Add(d);
        return DelegateSupport.ConvertDelegate<Il2CppSystem.Action<UIButtonSelector>>(d);
    }
}

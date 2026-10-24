using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace OthLan;

/// <summary>
/// 0.2.0 T1.3: gamepad/keyboard focus helpers for our Canvas dialogs (same conventions as CoopMenu).
/// </summary>
internal static class UiNav
{
    public static void Select(GameObject go)
    {
        try { EventSystem.current?.SetSelectedGameObject(go); } catch { }
    }

    public static void LinkRow(Selectable[] row)
    {
        if (row == null || row.Length == 0) return;
        for (var i = 0; i < row.Length; i++)
        {
            if (row[i] == null) continue;
            var n = new Navigation { mode = Navigation.Mode.Explicit };
            n.selectOnLeft = row[(i - 1 + row.Length) % row.Length];
            n.selectOnRight = row[(i + 1) % row.Length];
            n.selectOnUp = null;
            n.selectOnDown = null;
            row[i].navigation = n;
        }
    }

    public static void LinkColumn(Selectable[] col)
    {
        if (col == null || col.Length == 0) return;
        for (var i = 0; i < col.Length; i++)
        {
            if (col[i] == null) continue;
            var n = new Navigation { mode = Navigation.Mode.Explicit };
            n.selectOnUp = i > 0 ? col[i - 1] : null;
            n.selectOnDown = i + 1 < col.Length ? col[i + 1] : null;
            n.selectOnLeft = null;
            n.selectOnRight = null;
            col[i].navigation = n;
        }
    }

    /// <summary>If focus left the dialog, put it back on <paramref name="fallback"/>.</summary>
    public static void KeepFocus(Selectable[] allowed, Selectable fallback)
    {
        if (fallback == null) return;
        try
        {
            var es = EventSystem.current;
            if (es == null) { Select(fallback.gameObject); return; }
            var cur = es.currentSelectedGameObject;
            if (cur == null) { Select(fallback.gameObject); return; }
            if (allowed != null)
            {
                for (var i = 0; i < allowed.Length; i++)
                {
                    var a = allowed[i];
                    if (a != null && a.gameObject == cur) return;
                }
            }
            Select(fallback.gameObject);
        }
        catch { try { Select(fallback.gameObject); } catch { } }
    }
}

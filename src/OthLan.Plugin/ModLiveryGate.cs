using System;
using System.Collections.Generic;
using UnityEngine;

namespace OthLan;

/// <summary>
/// Pre-join UI: host requires Modio liveries the client lacks → list + Download / Cancel.
/// Download calls <see cref="ModLiveryBridge.RequestDownload"/>; when all ready, resumes join via callback.
/// </summary>
internal static class ModLiveryGate
{
    public enum St { Idle, Prompt, Downloading }
    public static St State { get; private set; } = St.Idle;
    static List<ModLiveryGateLogic.ModRef> _missing = new();
    static List<ModLiveryGateLogic.ModRef> _required = new();
    static Action _onReady;
    static Action _onCancel;
    static float _deadline;
    static string _status = "";

    public static bool Active => State != St.Idle;

    /// <summary>Show the gate. <paramref name="onReady"/> runs on the main thread when every required mod is local.</summary>
    public static void Begin(IReadOnlyList<ModLiveryGateLogic.ModRef> required, IReadOnlyList<ModLiveryGateLogic.ModRef> missing,
        Action onReady, Action onCancel)
    {
        _required = required != null ? new List<ModLiveryGateLogic.ModRef>(required) : new();
        _missing = missing != null ? new List<ModLiveryGateLogic.ModRef>(missing) : new();
        _onReady = onReady; _onCancel = onCancel;
        State = St.Prompt;
        _status = "";
        _deadline = 0;
        Plugin.L.LogInfo($"[ModLivery] gate: {_missing.Count} missing of {_required.Count} required");
        Overlay.Show();
    }

    public static void Cancel()
    {
        if (State == St.Idle) return;
        State = St.Idle;
        var c = _onCancel; Clear();
        try { c?.Invoke(); } catch (Exception e) { Plugin.L.LogWarning($"[ModLivery] cancel: {e.Message}"); }
    }

    /// <summary>Self-test / automation: same as pressing Download in the dialog.</summary>
    public static void StartDownload()
    {
        if (State != St.Prompt || _missing.Count == 0) return;
        _status = "";
        State = St.Downloading;
        _deadline = Time.realtimeSinceStartup + 180f;
        ModLiveryBridge.RequestDownload(_missing);
    }

    static void Clear()
    {
        _missing.Clear(); _required.Clear(); _onReady = null; _onCancel = null; _status = ""; _deadline = 0;
    }

    public static void Tick()
    {
        if (State != St.Downloading) return;
        ModLiveryBridge.TickRefresh();
        _missing = ModLiveryBridge.MissingOf(_required);
        if (_missing.Count == 0)
        {
            State = St.Idle;
            var ok = _onReady; Clear();
            Plugin.L.LogInfo("[ModLivery] gate: all mods ready — continuing join");
            try { ok?.Invoke(); } catch (Exception e) { Plugin.L.LogError($"[ModLivery] resume join: {e}"); }
            return;
        }
        if (Time.realtimeSinceStartup > _deadline)
        {
            _status = Loc.T("modGate.timeout");
            State = St.Prompt; // allow retry
            Plugin.L.LogWarning("[ModLivery] gate: download timeout — still missing " + ModLiveryGateLogic.Format(_missing));
        }
    }

    public static void OnGUI()
    {
        // Self-test still draws the dialog (Autopilot clicks via StartDownload/Cancel); depth keeps it above the menu.
        if (State == St.Idle) return;
        GUI.depth = -1000;
        var w = 520f; var h = 220f + Math.Min(_missing.Count, 8) * 18f;
        var x = (Screen.width - w) / 2f; var y = (Screen.height - h) / 2f;
        GUI.Box(new Rect(x, y, w, h), Loc.T("modGate.title"));
        var cx = x + 16; var cy = y + 28; var cw = w - 32;
        GUI.Label(new Rect(cx, cy, cw, 40), Loc.T("modGate.body")); cy += 44;
        var n = Math.Min(_missing.Count, 8);
        for (int i = 0; i < n; i++)
        {
            var m = _missing[i];
            var label = m.SubId == 0 ? Loc.F("modGate.item", m.Id) : Loc.F("modGate.itemSub", m.Id, m.SubId);
            GUI.Label(new Rect(cx, cy, cw, 18), "• " + label); cy += 18;
        }
        if (_missing.Count > 8) { GUI.Label(new Rect(cx, cy, cw, 18), Loc.F("modGate.more", _missing.Count - 8)); cy += 18; }
        if (!string.IsNullOrEmpty(_status)) { GUI.Label(new Rect(cx, cy, cw, 36), _status); cy += 38; }
        else cy += 8;
        if (State == St.Downloading)
        {
            GUI.Label(new Rect(cx, cy, cw, 24), Loc.F("modGate.downloading", _missing.Count));
            cy += 28;
            if (GUI.Button(new Rect(cx, cy, cw, 32), Loc.T("modGate.cancel"))) Cancel();
            return;
        }
        if (GUI.Button(new Rect(cx, cy, cw / 2 - 6, 36), Loc.T("modGate.download")))
        {
            _status = "";
            State = St.Downloading;
            _deadline = Time.realtimeSinceStartup + 180f;
            ModLiveryBridge.RequestDownload(_missing);
        }
        if (GUI.Button(new Rect(cx + cw / 2 + 6, cy, cw / 2 - 6, 36), Loc.T("modGate.cancel"))) Cancel();
    }
}

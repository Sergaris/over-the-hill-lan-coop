using System;
using System.Globalization;
using UnityEngine.Localization.Settings;

namespace OthLan;

/// <summary>
/// Follows the game's language (Unity Localization: LocalizationSettings.SelectedLocale, changed by the game's
/// Settings → Language menu at runtime). Polled at 4 Hz — one property read — instead of subscribing the native
/// SelectedLocaleChanged event: no Il2Cpp delegate lifetime to manage, and it works whenever the mod starts. Reads only
/// after the game's localization finished initialising (reading SelectedLocale earlier can force a synchronous init).
/// Before that: the last language seen (cfg General.LastGameLanguage), else the OS UI language.
/// </summary>
internal static class LangWatch
{
    static float _next;
    static bool _broken, _initLogged;
    public static string GameCode = "-";
    /// <summary>True once the game's own language has been read (or remembered from the last run).</summary>
    public static bool GameKnown => GameCode != "-" || _fromCfg;
    static bool _fromCfg;

    public static void Init()
    {
        var last = Plugin.LastGameLanguage?.Value;
        var start = !string.IsNullOrEmpty(last) ? last : CultureInfo.CurrentUICulture.Name;
        _fromCfg = !string.IsNullOrEmpty(last);
        Loc.SetLanguage(start);
        Plugin.L.LogInfo($"[Lang] start language {Loc.Lang} (from {(string.IsNullOrEmpty(last) ? "OS " + CultureInfo.CurrentUICulture.Name : "cfg " + last)}); tables: {string.Join(", ", Loc.Languages)}");
    }

    public static void Tick()
    {
        if (_broken) return;
        var now = UnityEngine.Time.realtimeSinceStartup;
        if (now < _next) return;
        _next = now + 0.25f;
        try
        {
            if (!LocalizationSettings.HasSettings) return;
            var op = LocalizationSettings.InitializationOperation;
            if (!op.IsValid() || !op.IsDone) return;
            var loc = LocalizationSettings.SelectedLocale;
            if (loc == null) return;
            var code = loc.Identifier.Code;
            if (!_initLogged)
            {
                _initLogged = true;
                string avail = "?";
                try { var l = LocalizationSettings.AvailableLocales?.Locales; if (l != null) { var parts = new System.Collections.Generic.List<string>(); for (int i = 0; i < l.Count; i++) parts.Add(l[i].Identifier.Code); avail = string.Join(",", parts); } } catch { }
                Plugin.L.LogInfo($"[Lang] game localization ready: selected '{code}', game offers [{avail}]");
            }
            if (code == GameCode) return;
            GameCode = code;
            var before = Loc.Lang;
            if (Loc.SetLanguage(code)) Plugin.L.LogInfo($"[Lang] game language '{code}': mod texts {before} -> {Loc.Lang}");
            if (Plugin.LastGameLanguage != null && Plugin.LastGameLanguage.Value != code) Plugin.LastGameLanguage.Value = code;
        }
        catch (Exception e) { _broken = true; Plugin.L.LogWarning($"[Lang] cannot read the game's language ({e.Message}); mod texts stay {Loc.Lang}"); }
    }
}

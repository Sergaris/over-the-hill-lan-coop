using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace OthLan;

/// <summary>
/// String tables for every user-visible text of the mod (Unity-free, unit-tested). One embedded JSON file per language
/// (Lang/en.json, Lang/ru.json: flat "key": "text", placeholders {0} {1:0.0} in string.Format syntax, invariant culture).
/// Lookup: current language -> English -> the key itself. The current language follows the game's own language setting
/// (LangWatch); <see cref="Generation"/> grows on every change so cached UI (TMP texts of the Local Co-op screen) can
/// rebuild. IMGUI (F7, labels) re-resolves every frame through <see cref="Msg"/>.
/// To add a language: add Lang/xx.json with the same keys (missing keys fall back to English).
/// </summary>
public static class Loc
{
    public const string Fallback = "en";
    static readonly Dictionary<string, Dictionary<string, string>> _tables = new(StringComparer.OrdinalIgnoreCase);
    static string _lang = Fallback;
    static bool _loaded;

    public static int Generation { get; private set; }
    public static string Lang { get { EnsureLoaded(); return _lang; } }
    public static bool IsRu => Lang == "ru";
    public static event Action Changed;

    public static IEnumerable<string> Languages { get { EnsureLoaded(); return _tables.Keys; } }
    public static IReadOnlyDictionary<string, string> Table(string lang) { EnsureLoaded(); return _tables.TryGetValue(lang, out var t) ? t : null; }

    /// <summary>"ru-RU" / "ru" / "Russian (ru)" -> "ru"; "en-US" -> "en"; "zh-Hans" -> "zh-hans"; null/empty -> null.</summary>
    public static string Normalize(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var c = code.Trim();
        var p = c.IndexOf('('); var q = c.IndexOf(')');
        if (p >= 0 && q > p) c = c.Substring(p + 1, q - p - 1);
        c = c.Replace('_', '-').ToLowerInvariant();
        if (c.StartsWith("zh")) return c.Contains("hant") || c.Contains("tw") || c.Contains("hk") ? "zh-hant" : "zh-hans";
        var dash = c.IndexOf('-');
        return dash > 0 ? c.Substring(0, dash) : c;
    }

    /// <summary>The table actually used for a game language (unknown languages -> English).</summary>
    public static string Resolve(string code)
    {
        EnsureLoaded();
        var n = Normalize(code);
        return n != null && _tables.ContainsKey(n) ? n : Fallback;
    }

    /// <summary>Switch language; returns true if the effective table changed.</summary>
    public static bool SetLanguage(string code)
    {
        var r = Resolve(code);
        if (r == _lang) return false;
        _lang = r; Generation++;
        try { Changed?.Invoke(); } catch { }
        return true;
    }

    public static string T(string key)
    {
        EnsureLoaded();
        if (_tables.TryGetValue(_lang, out var t) && t.TryGetValue(key, out var s)) return s;
        if (_tables.TryGetValue(Fallback, out var e) && e.TryGetValue(key, out var s2)) return s2;
        return key;
    }

    public static string F(string key, params object[] args) => FormatWith(T(key), args, false);

    /// <summary>English text regardless of the current language (for the BepInEx log).</summary>
    public static string En(string key, params object[] args) => FormatWith(TIn(Fallback, key), args, true);

    public static string TIn(string lang, string key)
    {
        EnsureLoaded();
        if (_tables.TryGetValue(lang, out var t) && t.TryGetValue(key, out var s)) return s;
        if (_tables.TryGetValue(Fallback, out var e) && e.TryGetValue(key, out var s2)) return s2;
        return key;
    }

    static string FormatWith(string fmt, object[] args, bool en)
    {
        if (args == null || args.Length == 0) return fmt;
        var a = new object[args.Length];
        for (int i = 0; i < args.Length; i++) a[i] = args[i] is Msg m ? (en ? m.En() : m.ToString()) : args[i];
        try { return string.Format(CultureInfo.InvariantCulture, fmt, a); }
        catch (FormatException) { return fmt; }
    }

    /// <summary>Load one table from JSON (tests and the embedded resources use this).</summary>
    public static void Load(string lang, string json)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && !p.Name.StartsWith("_")) d[p.Name] = p.Value.GetString();
        _tables[lang] = d;
        _loaded = true;
    }

    static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        var asm = typeof(Loc).Assembly;
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith("lang.") || !name.EndsWith(".json")) continue;
            using var s = asm.GetManifestResourceStream(name);
            using var r = new StreamReader(s, Encoding.UTF8);
            Load(name.Substring(5, name.Length - 10), r.ReadToEnd());
        }
    }

    /// <summary>Placeholder indices used in a format string ({0}, {1:0.0} -> {0, 1}); "{{" escapes ignored.</summary>
    public static SortedSet<int> Placeholders(string fmt)
    {
        var set = new SortedSet<int>();
        for (int i = 0; i < fmt.Length; i++)
        {
            if (fmt[i] == '{') { if (i + 1 < fmt.Length && fmt[i + 1] == '{') { i++; continue; }
                int j = i + 1, n = 0; bool any = false;
                while (j < fmt.Length && char.IsDigit(fmt[j])) { n = n * 10 + (fmt[j] - '0'); j++; any = true; }
                if (any) set.Add(n); }
            else if (fmt[i] == '}' && i + 1 < fmt.Length && fmt[i + 1] == '}') i++;
        }
        return set;
    }
}

/// <summary>A text that is resolved when shown (so it follows a language change): key + arguments (Msg arguments nest).</summary>
public sealed class Msg
{
    public readonly string Key; public readonly object[] Args;
    public Msg(string key, params object[] args) { Key = key; Args = args; }
    public static Msg Raw(string text) => new("_raw", text);
    public override string ToString() => Key == "_raw" ? Args[0]?.ToString() ?? "" : Key == "_list" ? Join(false) : Loc.F(Key, Args);
    public string En() => Key == "_raw" ? Args[0]?.ToString() ?? "" : Key == "_list" ? Join(true) : Loc.En(Key, Args);
    /// <summary>A list of parts shown separated by two spaces.</summary>
    public static Msg List(params object[] parts) => new("_list", parts);
    string Join(bool en)
    {
        var sb = new StringBuilder();
        foreach (var a in Args) { if (sb.Length > 0) sb.Append("  "); sb.Append(a is Msg m ? (en ? m.En() : m.ToString()) : a?.ToString()); }
        return sb.ToString();
    }
}

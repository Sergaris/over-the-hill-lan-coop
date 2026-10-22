using System;
using System.Globalization;

namespace OthLan.Installer;

/// <summary>
/// Installer language. The installer runs before the game, so it cannot follow the game's language setting: it follows the
/// Windows display language (Russian if it is Russian, English otherwise), overridable with --lang ru|en. The mod itself
/// follows the game's language at runtime (see the plugin's Loc/LangWatch).
/// </summary>
static class UiText
{
    public static bool Ru = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";
    public static void FromArgs(string[] args)
    {
        var i = Array.IndexOf(args, "--lang");
        if (i >= 0 && i + 1 < args.Length) Ru = args[i + 1].StartsWith("ru", StringComparison.OrdinalIgnoreCase);
    }
    public static string T(string ru, string en) => Ru ? ru : en;
}

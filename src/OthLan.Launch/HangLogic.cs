using System;

namespace OthLan.Launch;

/// <summary>
/// Pure hang detector for BepInEx first boot when InteropManager downloads Unity base libraries
/// (unity.bepinex.dev). Used by OTH-LAN-Launch.exe. No I/O.
/// </summary>
public static class HangLogic
{
    public const double DefaultHangSeconds = 10.0;

    public enum LineKind
    {
        Other,
        /// <summary>Downloading / resolving unity base libraries (the blocked stage without VPN).</summary>
        UnityLibsDownload,
        /// <summary>OTH-LAN / BepInEx got past interop generation.</summary>
        BootReady,
    }

    /// <summary>Classify one BepInEx LogOutput line.</summary>
    public static LineKind Classify(string line)
    {
        if (string.IsNullOrEmpty(line)) return LineKind.Other;
        // Real BepInEx 6 be.788 (the test PC): "[Message:InteropManager] Downloading unity base libraries from https://unity.bepinex.dev/..."
        if (ContainsIgnoreCase(line, "Downloading unity base libraries")
            || ContainsIgnoreCase(line, "unity.bepinex.dev/libraries/")
            || (ContainsIgnoreCase(line, "InteropManager") && ContainsIgnoreCase(line, "Download")))
            return LineKind.UnityLibsDownload;
        if (ContainsIgnoreCase(line, "[BuildLock] OK")
            || ContainsIgnoreCase(line, "4 native detours applied")
            || ContainsIgnoreCase(line, "OTH-LAN") && ContainsIgnoreCase(line, "loaded"))
            return LineKind.BootReady;
        return LineKind.Other;
    }

    /// <summary>
    /// True when we have seen the unity-libs download stage, have not reached BootReady, and there has been
    /// no further log progress for at least <paramref name="hangSeconds"/> (default 10).
    /// </summary>
    public static bool IsHung(bool sawUnityLibsDownload, bool sawBootReady, double secondsSinceLogProgress,
        double hangSeconds = DefaultHangSeconds)
    {
        if (sawBootReady || !sawUnityLibsDownload) return false;
        if (hangSeconds < 0) hangSeconds = DefaultHangSeconds;
        return secondsSinceLogProgress >= hangSeconds;
    }

    static bool ContainsIgnoreCase(string hay, string needle) =>
        hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
}

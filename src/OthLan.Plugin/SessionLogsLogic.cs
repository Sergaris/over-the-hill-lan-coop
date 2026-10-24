using System;
using System.Collections.Generic;
using System.Globalization;

namespace OthLan;

/// <summary>
/// 0.2.0 T0.2: which rotated session-log files to delete so at most <see cref="KeepCount"/> remain.
/// Pure logic (unit-tested). IO lives in <c>SessionLogs</c>.
/// </summary>
public static class SessionLogsLogic
{
    public const int KeepCount = 10;
    public const string FilePrefix = "LogOutput-";
    public const string FileSuffix = ".log";

    /// <summary>Config sections whose live Bind values go into session.txt (T0.2).</summary>
    public static readonly string[] SessionCfgSections = { "Netcode", "CarSync", "Outage", "OwnCar" };

    public static bool IsSessionCfgSection(string section)
    {
        if (string.IsNullOrEmpty(section)) return false;
        foreach (var s in SessionCfgSections)
            if (string.Equals(s, section, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Sanitize a free-text reason so it is safe inside a file name (letters, digits, dash, underscore).</summary>
    public static string SanitizeWhy(string why)
    {
        if (string.IsNullOrWhiteSpace(why)) return "snap";
        var chars = why.Trim().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '-' && c != '_')
                chars[i] = '-';
        }
        var s = new string(chars);
        while (s.Contains("--", StringComparison.Ordinal)) s = s.Replace("--", "-", StringComparison.Ordinal);
        s = s.Trim('-');
        if (s.Length == 0) return "snap";
        return s.Length <= 40 ? s : s.Substring(0, 40);
    }

    /// <summary>Build <c>LogOutput-yyyyMMdd-HHmmss-&lt;why&gt;.log</c>.</summary>
    public static string SnapshotFileName(DateTime localNow, string why)
        => $"{FilePrefix}{localNow:yyyyMMdd-HHmmss}-{SanitizeWhy(why)}{FileSuffix}";

    /// <summary>
    /// Among <paramref name="names"/> in the logs directory, return the names that should be deleted so at most
    /// <paramref name="keep"/> rotated snapshots remain. Non-matching names are ignored (never deleted).
    /// Oldest by parseable stamp in the name first; unparseable stamps sort as oldest.
    /// </summary>
    public static List<string> PickExcess(IEnumerable<string> names, int keep = KeepCount)
    {
        if (keep < 0) keep = 0;
        var snaps = new List<(string name, DateTime stamp, string raw)>();
        foreach (var n in names ?? Array.Empty<string>())
        {
            if (string.IsNullOrEmpty(n)) continue;
            var baseName = n;
            var slash = Math.Max(n.LastIndexOf('/'), n.LastIndexOf('\\'));
            if (slash >= 0) baseName = n.Substring(slash + 1);
            if (!TryParseStamp(baseName, out var stamp)) continue;
            snaps.Add((baseName, stamp, baseName));
        }
        if (snaps.Count <= keep) return new List<string>();
        snaps.Sort((a, b) =>
        {
            var c = a.stamp.CompareTo(b.stamp);
            return c != 0 ? c : string.CompareOrdinal(a.name, b.name);
        });
        var drop = snaps.Count - keep;
        var result = new List<string>(drop);
        for (var i = 0; i < drop; i++) result.Add(snaps[i].name);
        return result;
    }

    /// <summary>True when <paramref name="fileName"/> matches <c>LogOutput-yyyyMMdd-HHmmss-….log</c>.</summary>
    public static bool TryParseStamp(string fileName, out DateTime stamp)
    {
        stamp = default;
        if (string.IsNullOrEmpty(fileName) || !fileName.StartsWith(FilePrefix, StringComparison.Ordinal)
            || !fileName.EndsWith(FileSuffix, StringComparison.Ordinal))
            return false;
        var mid = fileName.Substring(FilePrefix.Length, fileName.Length - FilePrefix.Length - FileSuffix.Length);
        // mid = "yyyyMMdd-HHmmss-why" or at least "yyyyMMdd-HHmmss"
        if (mid.Length < 15) return false;
        var stampPart = mid.Substring(0, 15); // yyyyMMdd-HHmmss
        return DateTime.TryParseExact(stampPart, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out stamp);
    }
}

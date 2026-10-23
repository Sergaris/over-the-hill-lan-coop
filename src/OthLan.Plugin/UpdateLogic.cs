using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace OthLan;

/// <summary>
/// 0.1.8 update check, pure and unit-tested (Updater.cs does the I/O). Source: the public repo's
/// GitHub API "releases/latest" (GitHub itself never returns drafts or prereleases there; we check again).
/// Trust rules: HTTPS only, hosts api.github.com / github.com / objects.githubusercontent.com /
/// release-assets.githubusercontent.com, asset URLs only under this repo's releases/download/, the zip's sha256 must
/// match the SHA256SUMS.txt asset of the same release, and nothing outside the verified zip is ever started.
/// </summary>
public static class UpdateLogic
{
    /// <summary>owner/repo as one literal (the public-tree name scrub leaves "owner/repo" paths alone; a separate
    /// owner constant was rewritten and broke the update URLs, caught by LogicTests on the regenerated tree).</summary>
    public const string RepoPath = "Sergaris/over-the-hill-lan-coop";
    public const string LatestApi = "https://api.github.com/repos/" + RepoPath + "/releases/latest";
    public const string AssetPrefix = "https://github.com/" + RepoPath + "/releases/download/";
    public const string SumsName = "SHA256SUMS.txt";
    public const int MaxNotes = 600;
    static readonly string[] Hosts = { "api.github.com", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com" };

    /// <summary>MAJOR.MINOR.PATCH with optional leading v; a "-suffix" (prerelease) or anything else is rejected.</summary>
    public static bool TryParseVersion(string s, out (int Major, int Minor, int Patch) v)
    {
        v = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
        var p = s.Split('.');
        if (p.Length != 3) return false;
        var n = new int[3];
        for (int i = 0; i < 3; i++)
        {
            if (p[i].Length == 0 || p[i].Length > 5) return false;
            foreach (var c in p[i]) if (c < '0' || c > '9') return false;
            n[i] = int.Parse(p[i], CultureInfo.InvariantCulture);
        }
        v = (n[0], n[1], n[2]);
        return true;
    }

    /// <summary>-1 / 0 / 1; unparsable sorts lowest (never "newer").</summary>
    public static int Compare(string a, string b)
    {
        var okA = TryParseVersion(a, out var va); var okB = TryParseVersion(b, out var vb);
        if (!okA || !okB) return okA == okB ? 0 : okA ? 1 : -1;
        return va.CompareTo(vb);
    }

    public static bool IsNewer(string candidate, string running) =>
        TryParseVersion(candidate, out _) && TryParseVersion(running, out _) && Compare(candidate, running) > 0;

    /// <summary>"sha256  name" or "sha256 *name" lines (sha256sum format). Bad lines are ignored.</summary>
    public static Dictionary<string, string> ParseSums(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (text == null) return d;
        foreach (var raw in text.Split('\n'))
        {
            var l = raw.Trim();
            if (l.Length < 66) continue;
            var h = l.Substring(0, 64);
            if (!IsHex64(h) || (l[64] != ' ' && l[64] != '\t')) continue;
            var name = l.Substring(65).TrimStart(' ', '\t', '*').Trim();
            if (name.Length == 0 || name.Contains("/") || name.Contains("\\")) continue;
            d[name] = h.ToLowerInvariant();
        }
        return d;
    }

    public static bool IsHex64(string h)
    {
        if (h == null || h.Length != 64) return false;
        foreach (var c in h) if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F')) return false;
        return true;
    }

    public static bool HashEquals(string a, string b) =>
        IsHex64(a) && IsHex64(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>HTTPS + allowed host. In self-test builds a loopback http:// URL is allowed when testLoopback is set.</summary>
    public static bool AllowedUrl(string url, bool testLoopback = false)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (testLoopback && u.Scheme == "http" && (u.Host == "127.0.0.1" || u.Host == "localhost")) return true;
        if (u.Scheme != "https" || !u.IsDefaultPort || u.UserInfo.Length > 0) return false;
        return Array.IndexOf(Hosts, u.Host.ToLowerInvariant()) >= 0;
    }

    /// <summary>An asset URL from the release JSON must point into this repo's releases.</summary>
    public static bool AllowedAssetUrl(string url, string testPrefix = null) =>
        url != null && (testPrefix != null ? url.StartsWith(testPrefix, StringComparison.Ordinal) && AllowedUrl(url, true)
                                           : url.StartsWith(AssetPrefix, StringComparison.Ordinal) && AllowedUrl(url));

    public sealed class Release
    {
        public string Tag = "", Version = "", Name = "", Notes = "", ZipName = "", ZipUrl = "", SumsUrl = "";
        public long ZipSize;
    }

    /// <summary>Release from the GitHub JSON, or null with a reason. Picks OTH-LAN-&lt;version&gt;.zip (installer zip).</summary>
    public static Release ParseRelease(string json, out string why, string testPrefix = null)
    {
        why = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            bool B(string k) => r.TryGetProperty(k, out var e) && e.ValueKind == JsonValueKind.True;
            string S(JsonElement o, string k) => o.TryGetProperty(k, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
            if (B("draft")) { why = "draft"; return null; }
            if (B("prerelease")) { why = "prerelease"; return null; }
            var rel = new Release { Tag = S(r, "tag_name"), Name = S(r, "name"), Notes = S(r, "body") };
            if (!TryParseVersion(rel.Tag, out var v)) { why = $"tag '{Trunc(rel.Tag, 40)}' is not a version"; return null; }
            rel.Version = $"{v.Major}.{v.Minor}.{v.Patch}";
            rel.ZipName = $"OTH-LAN-{rel.Version}.zip";
            if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    var n = S(a, "name"); var u = S(a, "browser_download_url");
                    if (!AllowedAssetUrl(u, testPrefix)) continue;
                    if (n == rel.ZipName) { rel.ZipUrl = u; if (a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number) rel.ZipSize = sz.GetInt64(); }
                    else if (n == SumsName) rel.SumsUrl = u;
                }
            if (rel.ZipUrl.Length == 0) { why = $"no {rel.ZipName} asset"; return null; }
            if (rel.SumsUrl.Length == 0) { why = $"no {SumsName} asset"; return null; }
            rel.Notes = Trunc(rel.Notes, 20000);   // raw body; the prompt shows ShortNotes(Notes, language)
            return rel;
        }
        catch (Exception e) { why = "bad JSON: " + Trunc(e.Message, 80); return null; }
    }

    /// <summary>Release notes for the prompt: bullets of the first "## ... x.y.z" section in the wanted language
    /// (Cyrillic heading = Russian), plain text, capped.</summary>
    /// <summary>"b8b08fa1…05864c72e0": both ends, so two hashes that differ only at the end do not look equal.</summary>
    public static string ShortHash(string h) => string.IsNullOrEmpty(h) || h.Length <= 20 ? h ?? "" : h[..8] + "…" + h[^8..];

    public static string ShortNotes(string body, string lang = null)
    {
        if (string.IsNullOrEmpty(body)) return "";
        var lines = body.Replace("\r", "").Split('\n');
        var sb = new System.Text.StringBuilder();
        bool inSec = false, any = false;
        foreach (var raw in lines)
        {
            var l = raw.TrimEnd();
            if (l.StartsWith("## "))
            {
                if (inSec) break;
                var ru = HasCyr(l);
                var wantRu = lang == "ru";
                if (System.Text.RegularExpressions.Regex.IsMatch(l, @"\d+\.\d+\.\d+") && ru == wantRu) { inSec = true; any = true; }   // "What's in 0.1.9" / its translation
                continue;
            }
            if (!inSec) continue;
            if (l.StartsWith("- ") || l.StartsWith("* ")) sb.Append("• ").Append(Strip(l.Substring(2))).Append('\n');
            else if (l.StartsWith("  ") && sb.Length > 0 && l.Trim().Length > 0) { sb.Length--; sb.Append(' ').Append(Strip(l.Trim())).Append('\n'); }
            if (sb.Length > MaxNotes) break;
        }
        var s = any ? sb.ToString().Trim() : "";
        return Trunc(s, MaxNotes);
    }

    static bool HasCyr(string s) { foreach (var c in s) if (c >= 'А' && c <= 'я' || c == 'ё' || c == 'Ё') return true; return false; }
    static string Strip(string s) => s.Replace("**", "").Replace("`", "");
    public static string Trunc(string s, int n) => s == null ? "" : s.Length <= n ? s : s.Substring(0, n - 1) + "…";

    /// <summary>"Skip this version": skipped versions are remembered; a newer one is offered again.</summary>
    public static bool ShouldOffer(string latest, string running, string skipped) =>
        IsNewer(latest, running) && !(TryParseVersion(skipped, out _) && Compare(latest, skipped) <= 0);
}

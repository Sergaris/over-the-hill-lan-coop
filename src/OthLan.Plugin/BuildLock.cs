using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using BepInEx;

namespace OthLan;

internal sealed class SupportedBuild
{
    public string BuildId;
    public Dictionary<string, string> Files = new();
    public Dictionary<string, string> Rva = new();
    public Dictionary<string, string> CodeSig = new();
    public Dictionary<string, int> Offsets = new();
    public Dictionary<string, string> OptionalRva = new(), OptionalSig = new();   // 0.1.7 optional game hooks (OptionalHooks)
    public string FusionVersion;
}

/// <summary>Build identification: file hashes against supported-builds.json (the gate's first tier, see BuildGate).</summary>
internal static class BuildLock
{
    public static string CurrentBuildId;
    /// <summary>SHA-256 (hex, lower case) of GameAssembly.dll; identifies the build whatever the manifest says.</summary>
    public static string GameAssemblyHash;
    public static readonly List<SupportedBuild> Known = new();

    public static string Check(out SupportedBuild match)
    {
        match = null;
        using var s = typeof(BuildLock).Assembly.GetManifestResourceStream("supported-builds.json");
        using var doc = JsonDocument.Parse(s);
        var actual = new Dictionary<string, string>();
        string Hash(string rel)
        {
            if (actual.TryGetValue(rel, out var h)) return h;
            var p = Path.Combine(Paths.GameRootPath, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(p)) return actual[rel] = "missing";
            using var f = File.OpenRead(p);
            using var sha = SHA256.Create();
            return actual[rel] = Convert.ToHexString(sha.ComputeHash(f)).ToLowerInvariant();
        }
        GameAssemblyHash = Hash("GameAssembly.dll");
        Known.Clear();
        foreach (var b in doc.RootElement.GetProperty("builds").EnumerateArray())
        {
            var sb = new SupportedBuild { BuildId = b.GetProperty("buildId").GetString() };
            foreach (var f in b.GetProperty("files").EnumerateObject()) sb.Files[f.Name] = f.Value.GetString();
            foreach (var r in b.GetProperty("rva").EnumerateObject()) sb.Rva[r.Name] = r.Value.GetString();
            if (b.TryGetProperty("codeSig", out var cs)) foreach (var r in cs.EnumerateObject()) sb.CodeSig[r.Name] = r.Value.GetString();
            if (b.TryGetProperty("offsets", out var os)) foreach (var r in os.EnumerateObject()) sb.Offsets[r.Name] = Convert.ToInt32(r.Value.GetString(), 16);
            if (b.TryGetProperty("fusionVersion", out var fv)) sb.FusionVersion = fv.GetString();
            if (b.TryGetProperty("optionalRva", out var orv)) foreach (var r in orv.EnumerateObject()) sb.OptionalRva[r.Name] = r.Value.GetString();
            if (b.TryGetProperty("optionalSig", out var osg)) foreach (var r in osg.EnumerateObject()) sb.OptionalSig[r.Name] = r.Value.GetString();
            Known.Add(sb);
            var ok = true;
            foreach (var kv in sb.Files)
                if (!string.Equals(Hash(kv.Key), kv.Value, StringComparison.OrdinalIgnoreCase)) { ok = false; break; }
            if (ok) { match = sb; CurrentBuildId = sb.BuildId; return $"OK: supported build {sb.BuildId} (Steam buildid from acf: {SteamBuildId() ?? "?"})"; }
        }
        return $"NO MATCH: GameAssembly.dll={Hash("GameAssembly.dll")} acf buildid={SteamBuildId() ?? "?"}";
    }

    static string SteamBuildId()
    {
        try
        {
            // <lib>/steamapps/common/over the hill -> <lib>/steamapps/appmanifest_2929250.acf
            var acf = Path.GetFullPath(Path.Combine(Paths.GameRootPath, "..", "..", "appmanifest_2929250.acf"));
            if (!File.Exists(acf)) return null;
            foreach (var line in File.ReadLines(acf))
            {
                var t = line.Trim();
                if (t.StartsWith("\"buildid\"", StringComparison.OrdinalIgnoreCase))
                    return t.Split('"', StringSplitOptions.RemoveEmptyEntries)[^1];
            }
        }
        catch { }
        return null;
    }
}

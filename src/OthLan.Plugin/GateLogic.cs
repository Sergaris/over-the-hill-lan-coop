using System;
using System.Collections.Generic;
using System.Linq;

namespace OthLan;

/// <summary>
/// Version gate (0.1.6): pure decision logic, unit-tested in tests/LogicTests.
///  Supported    – game files hash-match a build in supported-builds.json: run normally.
///  Unverified   – files differ, but Fusion is the expected version, every hooked method's code signature (CodeSig)
///                 matches and every struct offset resolved by name at runtime matches: ask once (per GameAssembly
///                 hash) "continue at own risk / disable for now"; remember the answer.
///  Incompatible – anything else: LAN stays off ("incompatible, wait for an update").
/// </summary>
public static class GateLogic
{
    public enum Tier { Supported, Unverified, Incompatible }
    public enum Outcome { Run, Ask, DisabledByUser, Off }
    public const string Continue = "continue", Disable = "disable";

    public sealed class Probe
    {
        public bool HashMatch;
        public string FusionVersion, ExpectedFusion;
        public Dictionary<string, string> Sigs = new(), ExpectedSigs = new();
        public Dictionary<string, int> Offsets = new(), ExpectedOffsets = new();
        public string Error;   // resolution failed (missing method/field, exception)
    }

    /// <summary>Tier plus a list of human-readable reasons (empty for Supported/Unverified).</summary>
    public static Tier Classify(Probe p, out List<string> reasons)
    {
        reasons = new List<string>();
        if (p.HashMatch) return Tier.Supported;
        if (!string.IsNullOrEmpty(p.Error)) reasons.Add(p.Error);
        if (string.IsNullOrEmpty(p.ExpectedFusion) || p.FusionVersion != p.ExpectedFusion)
            reasons.Add($"Fusion {p.FusionVersion ?? "?"} (expected {p.ExpectedFusion ?? "?"})");
        if (p.ExpectedSigs.Count == 0) reasons.Add("no code signatures in manifest");
        foreach (var kv in p.ExpectedSigs)
            if (!p.Sigs.TryGetValue(kv.Key, out var s) || s != kv.Value) reasons.Add($"code changed: {kv.Key} ({s ?? "missing"} != {kv.Value})");
        if (p.ExpectedOffsets.Count == 0) reasons.Add("no offsets in manifest");
        foreach (var kv in p.ExpectedOffsets)
            if (!p.Offsets.TryGetValue(kv.Key, out var o) || o != kv.Value) reasons.Add($"layout changed: {kv.Key} ({(p.Offsets.ContainsKey(kv.Key) ? $"0x{o:X}" : "missing")} != 0x{kv.Value:X})");
        return reasons.Count == 0 ? Tier.Unverified : Tier.Incompatible;
    }

    public static Outcome Decide(Tier t, string storedChoice) => t switch
    {
        Tier.Supported => Outcome.Run,
        Tier.Incompatible => Outcome.Off,
        _ => storedChoice == Continue ? Outcome.Run : storedChoice == Disable ? Outcome.DisabledByUser : Outcome.Ask,
    };

    /// <summary>Choices are stored as "hash=continue;hash=disable" (hash = GameAssembly.dll SHA-256 prefix).</summary>
    public static Dictionary<string, string> ParseChoices(string s)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(s)) return d;
        foreach (var part in s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = part.IndexOf('=');
            if (i <= 0) continue;
            var k = part[..i].Trim(); var v = part[(i + 1)..].Trim().ToLowerInvariant();
            if (k.Length > 0 && (v == Continue || v == Disable)) d[k] = v;
        }
        return d;
    }

    /// <summary>Set (or replace) one choice; keeps at most <paramref name="keep"/> most recent entries.</summary>
    public static string WithChoice(string s, string hash, string choice, int keep = 8)
    {
        var list = ParseChoices(s).Where(kv => !kv.Key.Equals(hash, StringComparison.OrdinalIgnoreCase)).Select(kv => $"{kv.Key}={kv.Value}").ToList();
        list.Add($"{hash}={choice}");
        return string.Join(";", list.Skip(Math.Max(0, list.Count - keep)));
    }

    /// <summary>Key that host and client compare: the supported buildId, else "ga:" + GameAssembly hash prefix.</summary>
    public static string BuildKey(string supportedBuildId, string gameAssemblyHash) =>
        !string.IsNullOrEmpty(supportedBuildId) ? supportedBuildId : "ga:" + (gameAssemblyHash ?? "?").Substring(0, Math.Min(16, (gameAssemblyHash ?? "?").Length));

    /// <summary>Host/client build check. Host values may be missing (older host) → mismatch only if both are known.</summary>
    public static bool SameBuild(string hostKey, string hostGaHash, string myKey, string myGaHash)
    {
        if (!string.IsNullOrEmpty(hostGaHash) && !string.IsNullOrEmpty(myGaHash))
            return hostGaHash.Equals(myGaHash, StringComparison.OrdinalIgnoreCase);
        return string.Equals(hostKey, myKey, StringComparison.OrdinalIgnoreCase);
    }
}

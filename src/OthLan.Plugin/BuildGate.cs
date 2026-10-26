using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace OthLan;

/// <summary>
/// 0.1.6 version gate (replaces the hard build lock and the unsafe StrictBuildLock=false bypass). Runs once in
/// Plugin.Load; tiers and outcomes in <see cref="GateLogic"/>. The detours are applied only when the outcome is Run,
/// or later when the user picks "Continue at own risk" in the one-time dialog (Overlay draws it).
/// </summary>
internal static unsafe class BuildGate
{
    public static GateLogic.Tier Tier = GateLogic.Tier.Incompatible;
    public static GateLogic.Outcome Outcome = GateLogic.Outcome.Off;
    public static List<string> Reasons = new();
    public static string FusionVersion;
    public static string HookError;            // detours failed to apply
    public static string BuildKey;             // what host and client compare
    public static string GaHash => BuildLock.GameAssemblyHash;
    public static string ChoiceKey;            // GameAssembly hash prefix (or "sim-..." in a self-test)
    public static string SimMode;              // self-test only: "unverified" | "incompatible"
    public static SupportedBuild Match;        // hash-matched manifest build (null = unknown files)

    [StructLayout(LayoutKind.Sequential)] struct RuntimeFunction { public uint Begin, End, Unwind; }
    [DllImport("kernel32")] static extern RuntimeFunction* RtlLookupFunctionEntry(ulong pc, out ulong imageBase, IntPtr history);

    /// <summary>CodeSig of the function at <paramref name="fn"/>, bounded by its x64 unwind entry (.pdata).</summary>
    public static string SigAt(IntPtr fn)
    {
        var rf = RtlLookupFunctionEntry((ulong)fn, out var image, IntPtr.Zero);
        var leaf = rf == null || image + rf->Begin != (ulong)fn;
        var len = leaf ? CodeSig.LeafMaxBytes : (int)Math.Min(rf->End - rf->Begin, (uint)CodeSig.MaxBytes);
        var buf = new byte[len];
        Marshal.Copy(fn, buf, 0, len);
        return CodeSig.Compute(buf, len, leaf);
    }

    public static void Run()
    {
#if SELFTEST
        SimMode = Autopilot.ArgValue("--othlan-simbuild");
#endif
        var lockResult = BuildLock.Check(out var match);
        Plugin.L.LogInfo($"[Gate] {lockResult}");
        if (SimMode != null) { Plugin.L.LogWarning($"[Gate] SELF-TEST: simulating an unknown game build ({SimMode}); game files untouched"); match = null; }
#if SELFTEST
        if (Autopilot.ArgValue("--othlan-gahash") is { } fake) { Plugin.L.LogWarning($"[Gate] SELF-TEST: reporting GameAssembly hash {fake}"); BuildLock.GameAssemblyHash = fake; }
#endif
        var gaPrefix = (GaHash ?? "unknown").Substring(0, Math.Min(16, (GaHash ?? "unknown").Length));
        ChoiceKey = SimMode != null ? "sim-" + gaPrefix : gaPrefix;
        BuildKey = GateLogic.BuildKey(match?.BuildId, GaHash);
        Match = match;

        var probe = new GateLogic.Probe { HashMatch = match != null };
        try
        {
            FusionLayout.Resolve();
            FusionHooks.Resolve(match);
        }
        catch (Exception e) { probe.Error = "resolve: " + e.Message; Plugin.L.LogError($"[Gate] resolve failed: {e}"); }
        try { FusionVersion = ReadFusionVersion(); } catch (Exception e) { Plugin.L.LogWarning($"[Gate] Fusion version unreadable: {e.Message}"); }
        probe.FusionVersion = FusionVersion;
        foreach (var kv in FusionLayout.All) probe.Offsets[kv.Key] = kv.Value;
        foreach (var kv in FusionHooks.Pointers)
            try { probe.Sigs[kv.Key] = SigAt(kv.Value); } catch (Exception e) { Plugin.L.LogWarning($"[Gate] sig {kv.Key}: {e.Message}"); }
        if (SimMode == "incompatible" && probe.Sigs.Count > 0) { var k = probe.Sigs.Keys.First(); probe.Sigs[k] = "0:simulated-change"; }

        if (match != null) { Tier = GateLogic.Classify(probe, out Reasons); LogDiff(probe, match); }
        else
        {
            // Unknown files: Unverified if ANY known build matches Fusion version + offsets (CodeSig diffs are soft).
            List<string> firstReasons = null;
            Tier = GateLogic.Tier.Incompatible;
            foreach (var kb in BuildLock.Known)
            {
                probe.ExpectedFusion = kb.FusionVersion; probe.ExpectedSigs = kb.CodeSig; probe.ExpectedOffsets = kb.Offsets;
                var t = GateLogic.Classify(probe, out var rs);
                if (t == GateLogic.Tier.Unverified)
                {
                    Tier = t; Reasons = rs;
                    var soft = rs.Count > 0 ? $" (soft: {string.Join("; ", rs)})" : "";
                    Plugin.L.LogWarning($"[Gate] unknown files, Fusion+layout match build {kb.BuildId}{soft}");
                    break;
                }
                firstReasons ??= rs;
            }
            if (Tier == GateLogic.Tier.Incompatible) Reasons = firstReasons ?? new List<string> { "no known build in manifest" };
        }
        if (Tier != GateLogic.Tier.Supported && !string.IsNullOrEmpty(probe.Error) && !Reasons.Contains(probe.Error)) Reasons.Insert(0, probe.Error);
        if (match != null && !string.IsNullOrEmpty(probe.Error)) { Tier = GateLogic.Tier.Incompatible; Reasons = new List<string> { probe.Error }; }

        var choices = GateLogic.ParseChoices(Plugin.UnverifiedChoices.Value);
        choices.TryGetValue(ChoiceKey, out var stored);
        Outcome = GateLogic.Decide(Tier, stored);
        Plugin.L.LogWarning($"[Gate] tier={Tier} outcome={Outcome} fusion={FusionVersion} buildKey={BuildKey} choiceKey={ChoiceKey} stored={stored ?? "-"}" +
                            (Reasons.Count > 0 ? "\n  " + string.Join("\n  ", Reasons) : ""));
        Enact();
    }

    static void LogDiff(GateLogic.Probe p, SupportedBuild b)
    {
        // Known build: signatures/offsets are informational (the RVAs were already verified). A difference means the
        // manifest is stale, not that the game is unsafe; the detours use the offsets resolved at runtime anyway.
        foreach (var kv in b.CodeSig) if (p.Sigs.TryGetValue(kv.Key, out var s) && s != kv.Value) Plugin.L.LogWarning($"[Gate] manifest sig differs for {kv.Key}: {s} != {kv.Value}");
        foreach (var kv in b.Offsets) if (p.Offsets.TryGetValue(kv.Key, out var o) && o != kv.Value) Plugin.L.LogWarning($"[Gate] manifest offset differs for {kv.Key}: 0x{o:X} != 0x{kv.Value:X}");
        Plugin.L.LogInfo($"[Gate] offsets by name: {string.Join(", ", p.Offsets.Select(kv => $"{kv.Key}=0x{kv.Value:X}"))}");
        Plugin.L.LogInfo($"[Gate] code signatures: {string.Join(", ", p.Sigs.Select(kv => $"{kv.Key.Split("::")[^1]}={kv.Value}"))}");
    }

    static string ReadFusionVersion()
    {
        // The running assembly's own metadata, e.g. "Fusion.Runtime, Version=2.1.2.0, Culture=neutral, ...".
        var full = Il2CppInterop.Runtime.Il2CppType.Of<Fusion.NetworkRunner>().Assembly.FullName;
        var i = full.IndexOf("Version=", StringComparison.Ordinal);
        if (i < 0) return null;
        var v = full[(i + 8)..]; var j = v.IndexOf(','); return j < 0 ? v : v[..j];
    }

    static void Enact()
    {
        if (Outcome == GateLogic.Outcome.Run) ApplyHooks();
        else LanState.Disabled = true;
    }

    static void ApplyHooks()
    {
        try { FusionHooks.Apply(); LanState.Disabled = false; HookError = null; OptionalFeatures(); }
        catch (Exception e) { HookError = e.Message; LanState.Disabled = true; Plugin.L.LogError($"[Hooks] install failed, LAN disabled: {e}"); }
    }

    /// <summary>0.1.7 optional game hooks (each disables only its own feature on failure).</summary>
    static void OptionalFeatures()
    {
        try { OwnCar.Install(); } catch (Exception e) { Plugin.L.LogError($"[OwnCar] install: {e}"); }
        try { OwnRender.Install(); } catch (Exception e) { Plugin.L.LogError($"[OwnRender] install: {e}"); }
    }

    /// <summary>The dialog's buttons (and F7's "enable anyway"). Persists the answer for this GameAssembly hash.</summary>
    public static void UserChoice(bool proceed, string source)
    {
        if (Tier != GateLogic.Tier.Unverified) return;
        Plugin.UnverifiedChoices.Value = GateLogic.WithChoice(Plugin.UnverifiedChoices.Value, ChoiceKey, proceed ? GateLogic.Continue : GateLogic.Disable);
        Outcome = proceed ? GateLogic.Outcome.Run : GateLogic.Outcome.DisabledByUser;
        Plugin.L.LogWarning($"[Gate] user chose {(proceed ? "CONTINUE at own risk" : "DISABLE for now")} for {ChoiceKey} ({source}); saved");
        Enact();
    }

    public static bool NeedsChoice => Outcome == GateLogic.Outcome.Ask;

    /// <summary>One line for F7 when LAN is not available (empty when it is).</summary>
    public static Msg StatusMsg()
    {
        if (HookError != null) return new Msg("gate.hooksFailed");
        return Outcome switch
        {
            GateLogic.Outcome.Off => new Msg("gate.incompatible", Plugin.Version),
            GateLogic.Outcome.DisabledByUser => new Msg("gate.disabledByUser"),
            GateLogic.Outcome.Ask => new Msg("gate.ask"),
            _ => Tier == GateLogic.Tier.Unverified ? new Msg("gate.unverifiedOn") : Msg.Raw(""),
        };
    }
    public static string Status() => StatusMsg().ToString();
}

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP.Hook;

namespace OthLan;

/// <summary>
/// 0.1.7: game-side detours that are NOT needed for LAN itself (own-car authority, main-menu button, self-test outage).
/// Each one is resolved by name through the interop MethodInfo* and is only applied if it is safe for this build:
///  * supported build (hash match): the RVA must equal the manifest's "optionalRva" entry;
///  * unverified build: the code signature must equal the manifest's "optionalSig" entry.
/// Anything else (missing method, other code) just disables that feature: no crash, LAN keeps working, F7 stays.
/// </summary>
internal static unsafe class OptionalHooks
{
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);

    public static readonly Dictionary<string, string> Status = new();   // key -> "ok" | reason (for logs / F7)
    static readonly List<INativeDetour> _detours = new();

    /// <summary>Address of the method behind a generated MethodInfo* field, or IntPtr.Zero (reason in Status).</summary>
    public static IntPtr Resolve(Type t, string field, string key)
    {
        try
        {
            var f = t.GetField(field, BindingFlags.NonPublic | BindingFlags.Static);
            if (f == null) { Status[key] = "method not found"; return IntPtr.Zero; }
            var mi = (IntPtr)f.GetValue(null);
            if (mi == IntPtr.Zero) { Status[key] = "null MethodInfo"; return IntPtr.Zero; }
            var fn = *(IntPtr*)mi;
            var rva = (long)fn - (long)GetModuleHandleW("GameAssembly.dll");
            var b = BuildGate.Match;
            if (b != null)
            {
                if (!b.OptionalRva.TryGetValue(key, out var exp)) { Status[key] = "not in manifest"; return IntPtr.Zero; }
                if (Convert.ToInt64(exp, 16) != rva) { Status[key] = $"RVA 0x{rva:X} != {exp}"; return IntPtr.Zero; }
            }
            else
            {
                string expSig = null;
                foreach (var kb in BuildLock.Known) if (kb.OptionalSig.TryGetValue(key, out expSig)) break;
                var sig = BuildGate.SigAt(fn);
                if (expSig == null || sig != expSig) { Status[key] = $"code changed ({sig} != {expSig ?? "none"})"; return IntPtr.Zero; }
            }
            Status[key] = "ok";
            Plugin.L.LogInfo($"[Hooks] optional {key} -> RVA 0x{rva:X}");
            return fn;
        }
        catch (Exception e) { Status[key] = e.GetType().Name + ": " + e.Message; return IntPtr.Zero; }
    }

    public static bool Apply<T>(IntPtr fn, T hook, out T orig, string key) where T : Delegate
    {
        orig = null;
        if (fn == IntPtr.Zero) { Plugin.L.LogWarning($"[Hooks] optional {key} unavailable: {(Status.TryGetValue(key, out var s) ? s : "?")}"); return false; }
        try { _detours.Add(INativeDetour.CreateAndApply(fn, hook, out orig)); return true; }
        catch (Exception e) { Status[key] = "apply failed: " + e.Message; Plugin.L.LogError($"[Hooks] optional {key}: {e}"); return false; }
    }
}

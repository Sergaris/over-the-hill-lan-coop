using System;
using System.Collections.Generic;
using System.Linq;
using Fusion;
using UnityEngine;

namespace OthLan;

#if SELFTEST
/// <summary>
/// Self-test only (--othlan-census): which networked objects make up the state the host replicates. <see cref="Begin"/>
/// logs object counts and state sizes by object name, then <see cref="Tick"/> copies every object's state words on each
/// new Fusion tick for <see cref="SampleS"/> and logs the words that changed per tick by object name and behaviour type
/// (what a full-consistency snapshot has to carry, before Fusion's compression).
/// </summary>
internal static unsafe class StateCensus
{
    const float SampleS = 3f;    sealed class Obj { public NetworkObject O; public string Name; public int[] Prev; public (string type, int from, int to)[] Behaviours; public long Changed; public int[] Hits; }
    static readonly List<Obj> _objs = new();
    static readonly Dictionary<string, long> _byName = new(), _byBehaviour = new();
    static float _until = -1; static int _lastTick, _ticks; static string _why;

    public static void Begin(string why)
    {
        var r = LanState.Runner();
        if (r == null) { Plugin.L.LogWarning($"[Census] {why}: no LAN runner"); return; }
        _objs.Clear(); _byName.Clear(); _byBehaviour.Clear(); _ticks = 0; _lastTick = r.Tick.Raw; _why = why;
        var sizes = new Dictionary<string, (int n, int words)>();
        int totalWords = 0;
        foreach (var o in r.GetAllNetworkObjects())
        {
            if (o == null || o.Ptr == null) continue;
            var name = (o.gameObject != null ? o.gameObject.name : "?").Replace("(Clone)", "").Trim();
            var words = NetworkObject.GetWordCount(o);
            sizes.TryGetValue(name, out var s); sizes[name] = (s.n + 1, s.words + words); totalWords += words;
            var bs = new List<(string, int, int)>();
            foreach (var b in o.NetworkedBehaviours)
                if (b != null && b.Ptr != null) { var from = (int)(b.Ptr - o.Ptr); bs.Add((b.GetIl2CppType().Name, from, from + b.WordCount)); }
            _objs.Add(new Obj { O = o, Name = name, Prev = Copy(o.Ptr, words), Behaviours = bs.ToArray(), Hits = new int[words] });
            if (name.StartsWith("Broken"))
                Plugin.L.LogWarning($"[Census] {why} {DateTime.Now:HH:mm:ss.f} pose {name}#{o.Id.Raw} {o.transform.position.ToString("F2")}");
        }
        Plugin.L.LogWarning($"[Census] {why}: {_objs.Count} objects, {totalWords * 4} B state; {ParkedCars.Describe()}");
        foreach (var kv in sizes.OrderByDescending(kv => kv.Value.words).Take(12))
            Plugin.L.LogWarning($"[Census]   size {kv.Key}: n={kv.Value.n} {kv.Value.words * 4} B");
        _until = Time.realtimeSinceStartup + SampleS;
    }

    public static void Tick()
    {
        if (_until < 0) return;
        var r = LanState.Runner();
        if (r == null) { _until = -1; return; }
        if (r.Tick.Raw != _lastTick)
        {
            _lastTick = r.Tick.Raw; _ticks++;
            foreach (var x in _objs) Diff(x);
        }
        if (Time.realtimeSinceStartup < _until) return;
        _until = -1;
        var total = _byName.Values.Sum();
        Plugin.L.LogWarning($"[Census] {_why}: {_ticks} ticks, changed {total * 4.0 / Math.Max(1, _ticks):0} B/tick");
        foreach (var kv in _byName.OrderByDescending(kv => kv.Value).Take(12))
            Plugin.L.LogWarning($"[Census]   changed {kv.Key}: {kv.Value * 4.0 / Math.Max(1, _ticks):0} B/tick");
        foreach (var kv in _byBehaviour.OrderByDescending(kv => kv.Value).Take(12))
            Plugin.L.LogWarning($"[Census]   changed by {kv.Key}: {kv.Value * 4.0 / Math.Max(1, _ticks):0} B/tick");
        foreach (var x in _objs.Where(x => x.Changed > 0 && x.O != null && !x.O.WasCollected).OrderByDescending(x => x.Changed).Take(10))
        {
            var rb = x.O.GetComponent<Rigidbody>();
            var body = rb == null ? "no rigidbody" : $"sleeping={rb.IsSleeping()} kinematic={rb.isKinematic} v={rb.linearVelocity.magnitude:0.000} w={rb.angularVelocity.magnitude:0.000}";
            Plugin.L.LogWarning($"[Census]   object {x.Name}#{x.O.Id.Raw}: {x.Changed * 4.0 / Math.Max(1, _ticks):0} B/tick, {body}");
        }
        var top = _objs.Where(x => x.Name.StartsWith("Broken")).OrderByDescending(x => x.Changed).FirstOrDefault();
        if (top != null) Plugin.L.LogWarning($"[Census]   word map {top.Name} (behaviour:word=changes of {_ticks} ticks): {WordMap(top)}");
    }

    static string WordMap(Obj x)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (type, from, to) in x.Behaviours)
        {
            var hot = Enumerable.Range(from, Math.Max(0, Math.Min(to, x.Hits.Length) - from)).Where(i => x.Hits[i] > 0).ToList();
            if (hot.Count == 0) continue;
            sb.Append($"{type}[{from}..{to}) {hot.Count} words: ");
            sb.Append(string.Join(",", hot.Select(i => $"{i - from}={x.Hits[i]}")));
            sb.Append(" | ");
        }
        return sb.ToString();
    }

    static void Diff(Obj x)
    {
        if (x.O == null || x.O.WasCollected || x.O.Ptr == null) return;
        var p = x.O.Ptr; var n = Math.Min(x.Prev.Length, NetworkObject.GetWordCount(x.O)); var changed = 0;
        for (var i = 0; i < n; i++)
        {
            if (p[i] == x.Prev[i]) continue;
            x.Prev[i] = p[i]; changed++; x.Hits[i]++;
            var type = "header";
            foreach (var b in x.Behaviours) if (i >= b.from && i < b.to) { type = b.type; break; }
            _byBehaviour.TryGetValue(type, out var c); _byBehaviour[type] = c + 1;
        }
        _byName.TryGetValue(x.Name, out var t); _byName[x.Name] = t + changed; x.Changed += changed;
    }

    static int[] Copy(int* p, int n) { var a = new int[n]; for (var i = 0; i < n; i++) a[i] = p[i]; return a; }
}
#endif

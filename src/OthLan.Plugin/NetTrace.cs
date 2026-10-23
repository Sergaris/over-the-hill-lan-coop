using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Fusion;
using Fusion.Addons.Physics;
using UnityEngine;

namespace OthLan;

/// <summary>
/// 0.1.8 v3 self-test only (Autopilot active): dense per-frame traces of the OTHER players' cars, so remote-car jumps
/// can be measured instead of guessed (docs/NETCODE-v3-RESEARCH.md section 3).
///  * {tag}-remote.csv (every role): per frame and remote car, the drawn pose (InterpolationTarget, sampled after
///    rendering), the rigidbody, the per-frame hitch, and on a client Fusion's interpolation state (remote render time,
///    time since the last host snapshot, latest server tick).
///  * {tag}-follow.csv (host): per frame and client car, CarSync's input (rigidbody before, target, error, gap) and
///    output (action, rigidbody after, contact).
/// Never written in normal play.
/// </summary>
internal static class NetTrace
{
    static StreamWriter _remote, _follow, _own;
    static bool _tried; static int _lines;
    const int MaxLines = 400000;
    static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
#if SELFTEST
    public static bool On => Autopilot.Active && Plugin.NetStatsLog.Value;
#else
    public static bool On => false; // public build: no self-test output
#endif

    static void Open()
    {
        if (_tried) return; _tried = true;
#if SELFTEST
        try
        {
            Directory.CreateDirectory(Autopilot.OutDir);
            _remote = new StreamWriter(Path.Combine(Autopilot.OutDir, $"{Autopilot.Tag}-remote.csv"), false, new UTF8Encoding(false));
            _remote.WriteLine("rt,frame,dt,car,dx,dy,dz,bx,by,bz,hitch,speed,smooth,offX,offY,offZ,snapAge,serverTick,tick,remoteRT,alpha");
            _follow = new StreamWriter(Path.Combine(Autopilot.OutDir, $"{Autopilot.Tag}-follow.csv"), false, new UTF8Encoding(false));
            _follow.WriteLine("rt,frame,dt,slot,preX,preY,preZ,tgX,tgY,tgZ,err,errDeg,age,gap,contact,action,postX,postY,postZ,vx,vy,vz,cvx,cvy,cvz");
            _own = new StreamWriter(Path.Combine(Autopilot.OutDir, $"{Autopilot.Tag}-own.csv"), false, new UTF8Encoding(false));
            _own.WriteLine("rt,frame,kind,tick,capTick,n,serverTick,renderTime,x,y,z,rbx,rby,rbz,vx,vy,vz,a,b");
        }
        catch (Exception e) { Plugin.L.LogWarning($"[NetTrace] {e.Message}"); _remote = _follow = null; }
#endif
    }

    static string F(float v) => v.ToString("0.000", Ci);

    public static void Remote(float dt, int car, Vector3 drawn, Vector3 body, double hitch, float speed, int smooth, Vector3 off,
                              double snapAge, int serverTick, int tick, float remoteRt, float alpha)
    {
        if (!On) return; Open(); if (_remote == null || _lines++ > MaxLines) return;
        _remote.WriteLine(string.Join(",", Time.realtimeSinceStartup.ToString("0.0000", Ci), Time.frameCount.ToString(Ci), F(dt), car.ToString(Ci),
            F(drawn.x), F(drawn.y), F(drawn.z), F(body.x), F(body.y), F(body.z), hitch.ToString("0.000", Ci), F(speed), smooth.ToString(Ci),
            F(off.x), F(off.y), F(off.z), snapAge.ToString("0.000", Ci), serverTick.ToString(Ci), tick.ToString(Ci), F(remoteRt), F(alpha)));
    }

    public struct FollowIn { public Rigidbody Rb; public Vector3 Pre, Target, ClientVel; public double Err, ErrDeg, Age, Gap; public bool Contact; }
    public static readonly Dictionary<int, FollowIn> Pending = new();

    /// <summary>Host, after CarSync's per-frame pass: one line per client car with what CarSync did.</summary>
    public static void FollowFlush(Dictionary<int, string> actions, float dt)
    {
        if (Pending.Count == 0) return;
        if (On) foreach (var kv in Pending) { try { FollowOut(kv.Key, actions.TryGetValue(kv.Key, out var a) ? a : "", kv.Value, dt); } catch { } }
        Pending.Clear();
    }

    static void FollowOut(int slot, string action, FollowIn i, float dt)
    {
        var rb = i.Rb; if (rb == null) return;
        Open(); if (_follow == null || _lines++ > MaxLines) return;
        Vector3 p = rb.position, v = rb.linearVelocity;
        _follow.WriteLine(string.Join(",", Time.realtimeSinceStartup.ToString("0.0000", Ci), Time.frameCount.ToString(Ci), F(dt), slot.ToString(Ci),
            F(i.Pre.x), F(i.Pre.y), F(i.Pre.z), F(i.Target.x), F(i.Target.y), F(i.Target.z), i.Err.ToString("0.000", Ci), i.ErrDeg.ToString("0.0", Ci),
            i.Age.ToString("0.000", Ci), i.Gap.ToString("0.000", Ci), i.Contact ? "1" : "0", action ?? "",
            F(p.x), F(p.y), F(p.z), F(v.x), F(v.y), F(v.z), F(i.ClientVel.x), F(i.ClientVel.y), F(i.ClientVel.z)));
    }

    public static void Flush() { try { _remote?.Flush(); _follow?.Flush(); _own?.Flush(); } catch { } }

    /// <summary>Own car, per event (reset/resim/restore/fwd tick) and per rendered frame. kind: R reset, S resim done,
    /// P restore, L late restore, F forward tick (BeforeTick), D drawn frame. a/b: kind-specific (hitch, err, ...).</summary>
    public static void Own(string kind, int tick, int capTick, int n, int serverTick, float renderTime, Vector3 p, Vector3 rb, Vector3 v, double a, double b)
    {
        if (!On) return; Open(); if (_own == null || _lines++ > MaxLines) return;
        _own.WriteLine(string.Join(",", Time.realtimeSinceStartup.ToString("0.0000", Ci), Time.frameCount.ToString(Ci), kind, tick.ToString(Ci), capTick.ToString(Ci), n.ToString(Ci),
            serverTick.ToString(Ci), renderTime.ToString("0.0000", Ci), F(p.x), F(p.y), F(p.z), F(rb.x), F(rb.y), F(rb.z), F(v.x), F(v.y), F(v.z), a.ToString("0.0000", Ci), b.ToString("0.0000", Ci)));
    }

    static readonly HashSet<string> _dumped = new();
    /// <summary>One-time log of a car's hierarchy under its InterpolationTarget (which visuals move with it).</summary>
    public static void DumpHierarchy(FusionPlayer fp)
    {
        if (!On) return;
        try
        {
            var name = fp.gameObject.name; if (!_dumped.Add(name)) return;
            var nrb = fp.GetComponent<NetworkRigidbody3D>(); var it = nrb != null ? nrb.InterpolationTarget : null;
            var sb = new StringBuilder();
            sb.Append($"[NetTrace] car {name}: root={fp.transform.name} rb on {(fp.rigidbody != null ? fp.rigidbody.transform.name : "-")} interpTarget={(it != null ? it.name : "-")} ");
            sb.Append($"rootChildren={fp.transform.childCount} itChildren={(it != null ? it.childCount : -1)} | ");
            int n = 0;
            for (int c = 0; c < fp.transform.childCount && n < 40; c++, n++)
            {
                var t = fp.transform.GetChild(c);
                sb.Append($"{t.name}[{t.childCount}]{(it != null && (t == it || t.IsChildOf(it)) ? "*" : "")}; ");
            }
            if (it != null) { sb.Append(" | under it: "); for (int c = 0; c < it.childCount && c < 40; c++) { var t = it.GetChild(c); sb.Append($"{t.name}[{t.childCount}]; "); } }
            var rends = fp.GetComponentsInChildren<Renderer>(); int under = 0, outside = 0; var outNames = new List<string>();
            foreach (var r in rends) { if (it != null && r.transform.IsChildOf(it)) under++; else { outside++; if (outNames.Count < 12) outNames.Add(r.transform.name); } }
            sb.Append($" | renderers under interpTarget={under} outside={outside} ({string.Join(",", outNames)})");
            try { var rb = fp.rigidbody; if (rb != null) sb.Append($" | rb mass={rb.mass:0} kinematic={rb.isKinematic} interp={rb.interpolation} maxDepen={rb.maxDepenetrationVelocity:0.##} ccd={rb.collisionDetectionMode} sleep={rb.sleepThreshold:0.###}"); } catch { }
            Plugin.L.LogInfo(sb.ToString());
        }
        catch (Exception e) { Plugin.L.LogWarning($"[NetTrace] hierarchy: {e.Message}"); }
    }
}

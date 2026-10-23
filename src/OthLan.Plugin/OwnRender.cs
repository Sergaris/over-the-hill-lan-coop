using System;
using System.Runtime.InteropServices;
using Fusion;
using Fusion.Addons.Physics;
using UnityEngine;

namespace OthLan;

/// <summary>
/// 0.1.8 own-car render continuity (docs/DEV-0.1.8-LOG.md 11:35). Fusion 2.1 never lets a client predict more than
/// TickRate ticks (1 s) past the latest host snapshot. When host data stalls (lag spike, 600 ms ping + hiccup) the client
/// reaches that cap and stops ticking; physics is stepped only by Fusion ticks, so the own car stands still, then the
/// next snapshot runs 7-14 ticks in one frame and the car lurches forward. This happens with and without ping
/// compensation (it is Fusion core), it is not a rollback.
///
/// Render-only fix, client, own car: right after the NetworkRigidbody's Render() placed the root (before LateUpdate,
/// so the camera follows), while Fusion is frozen the drawn pose is extrapolated with the car's last velocity and spin;
/// when ticks resume, the small difference between where the car was drawn and the simulated pose is carried as a
/// visual offset that decays in ~0.1 s. The offset always decays toward OUR simulated state, never toward the host;
/// anything above SnapMeters (respawn, rescue, host pose taken) snaps. Physics never sees the offset: the root is marked
/// "dirty from interpolation" exactly like the NetworkRigidbody's own render move, and Fusion restores it before ticks.
/// </summary>
internal static unsafe class OwnRender
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void InstFn(IntPtr self, IntPtr mi);
    static InstFn _orig, _hook;
    public static bool HookOk;
    const string K_R = "Fusion.Addons.Physics.NetworkRigidbody`2::Render()";

    public static IntPtr OwnNrb;                  // set by OwnCar (main thread)
    public static int LocalLerps, Frames, FrozenFrames, ExtrapCapped, Carries, Snaps, BeforeRenderApplies, Restores;
    public static double MaxExtrap, MaxCarry;
    static int _lastTick = int.MinValue, _frame = -1;
    static double _lastAdvanceAt = -1, _lastFrameAt = -1;
    static Vector3 _v, _w, _shownP, _shownV, _carryP; static Quaternion _shownQ = Quaternion.identity, _carryQ = Quaternion.identity;
    static bool _hasShown, _wasFrozen, _applied; static Vector3 _appliedP; static Quaternion _appliedQ;

    public static bool Enabled => Plugin.OwnFreezeSmoothing.Value;

    public static void Install()
    {
        if (HookOk || !Enabled) return;
        var p = OptionalHooks.Resolve(typeof(NetworkRigidbody<Rigidbody, RunnerSimulatePhysics3D>), "NativeMethodInfoPtr_Render_Public_Virtual_Void_0", K_R);
        _hook = RenderDetour;
        HookOk = OptionalHooks.Apply(p, _hook, out _orig, K_R);
        Plugin.L.LogInfo($"[OwnRender] own-car freeze smoothing {(HookOk ? "installed (after NetworkRigidbody render)" : "hook unavailable: before-render fallback")}");
    }

    static void RenderDetour(IntPtr self, IntPtr mi)
    {
        _orig(self, mi);
        if (self == IntPtr.Zero || self != OwnNrb) return;
        try { Apply(false); } catch (Exception e) { if (Frames < 3) Plugin.L.LogWarning($"[OwnRender] {e.Message}"); }
    }

    /// <summary>Fallback when the render hook is unavailable (camera then lags the offset by a frame).</summary>
    public static void BeforeRender() { if (!HookOk && Enabled && OwnNrb != IntPtr.Zero) { try { BeforeRenderApplies++; Apply(true); } catch { } } }

    public static void Forget() { OwnNrb = IntPtr.Zero; _lastTick = int.MinValue; _hasShown = false; _wasFrozen = false; _applied = false; _carryP = Vector3.zero; _carryQ = Quaternion.identity; _frame = -1; }

    /// <summary>OwnCar (any tick of the own car / prediction reset): physics must never see a drawn pose.</summary>
    public static void BeforeSim(Transform root, Rigidbody rb)
    {
        if (!_applied || root == null || rb == null) return;
        _applied = false;
        if ((root.position - _appliedP).sqrMagnitude < 1e-8f) { root.SetPositionAndRotation(rb.position, rb.rotation); Restores++; }
    }

    static void Apply(bool late)
    {
        if (Time.frameCount == _frame) return; _frame = Time.frameCount;
        OwnLocal.RestoreSkip();
        var r = OwnCar.Runner; var rb = OwnCar.Rb; var own = OwnCar.Own;
        if (r == null || rb == null || own == null || LanState.Active != LanRole.Client) { _hasShown = false; return; }
        var root = own.transform;
        var now = Time.realtimeSinceStartupAsDouble;
        var dt = _lastFrameAt < 0 ? 0 : now - _lastFrameAt; _lastFrameAt = now;
        Frames++;
        int tick; try { tick = OwnLocal.Enabled ? (int)OwnLocal.StepCount : r.Tick.Raw; } catch { return; }
        var tickDt = r.DeltaTime > 0 ? r.DeltaTime : 0.02f;
        int adv = _lastTick == int.MinValue ? 0 : tick - _lastTick;
        if (adv != 0 || _lastTick == int.MinValue) { _lastTick = tick; _lastAdvanceAt = now; _v = rb.linearVelocity; _w = rb.angularVelocity; }
        var baseP = root.position; var baseQ = root.rotation;
        if (OwnLocal.Enabled && OwnLocal.HasPrev && OwnLocal.ActiveNow)
        {
            // Own car on the local clock: draw between the pose before and after the last own step (one step behind,
            // like Fusion's own render interpolation), alpha = the part of the next step already elapsed.
            // Drawn time = real time - 1 step. Debt = real time not yet simulated (in steps); it is negative while Fusion's
            // kept ticks run ahead of the wall clock (up to -0.5) and >1 when local steps are capped. Clamp01 froze the
            // pose in the first case (v3l p4L: judder at 400 ms), so interpolate/extrapolate along the last step instead.
            var a = Mathf.Clamp((float)OwnLocal.Debt, -1f, 2f);
            var lp = Vector3.LerpUnclamped(OwnLocal.PrevP, rb.position, a); var lq = Quaternion.SlerpUnclamped(OwnLocal.PrevQ, rb.rotation, a);
            if ((lp - rb.position).sqrMagnitude > 25f) { lp = rb.position; lq = rb.rotation; Snaps++; }   // teleport: no lerp
            LocalLerps++;
            root.SetPositionAndRotation(lp, lq); OwnCar.MarkRootDirty();
            _applied = true; _appliedP = lp; _appliedQ = lq;
            Reset(lp, lq, dt); return;
        }
        if (dt <= 0 || dt > 0.25 || rb.isKinematic || adv < 0 || OwnCar.AcceptingHostPose(now))
        { Reset(baseP, baseQ, dt); return; }

        // 1) Fusion frozen: extrapolate from the last simulated motion.
        var tf = now - _lastAdvanceAt - tickDt - 0.004;
        var cap = Plugin.OwnFreezeExtrapSeconds.Value;
        Vector3 exP = Vector3.zero; Quaternion exQ = Quaternion.identity; var frozen = tf > 0 && adv == 0;
        if (frozen)
        {
            if (tf > cap) { if (!_wasCapped) ExtrapCapped++; _wasCapped = true; tf = cap; } else _wasCapped = false;
            exP = _v * (float)tf;
            var wm = _w.magnitude; if (wm > 1e-3f) exQ = Quaternion.AngleAxis(wm * Mathf.Rad2Deg * (float)tf, _w / wm);
            FrozenFrames++; if (tf > MaxExtrap) MaxExtrap = tf;
        }
        var targetP = baseP + exP; var targetQ = exQ * baseQ;

        // 2) Ticks resumed after a freeze (or a multi-tick catch-up): carry the drawn-vs-simulated difference.
        if (_hasShown && (_wasFrozen && adv > 0 || adv >= 3 && adv * tickDt > dt * 1.5))
        {
            var expected = _shownP + _shownV * (float)dt;   // constant velocity from what was on screen
            var c = expected - targetP;
            if (c.magnitude > Plugin.OwnFreezeSnapMeters.Value) { Snaps++; _carryP = Vector3.zero; _carryQ = Quaternion.identity; }
            else if (c.magnitude > 0.01f) { _carryP = c; _carryQ = _shownQ * Quaternion.Inverse(targetQ); Carries++; if (c.magnitude > MaxCarry) MaxCarry = c.magnitude; }
        }
        _wasFrozen = frozen;
        // 3) decay the carry toward our own simulated pose
        var k = (float)Math.Exp(-dt / Math.Max(0.02, Plugin.OwnFreezeDecaySeconds.Value));
        _carryP *= k; _carryQ = Quaternion.Slerp(Quaternion.identity, _carryQ, k);
        if (_carryP.sqrMagnitude < 1e-6f) _carryP = Vector3.zero;
        var shownP = targetP + _carryP; var shownQ = _carryQ * targetQ;
        if (_hasShown && dt > 0) _shownV = (shownP - _shownP) / (float)dt; else _shownV = _v;
        _shownP = shownP; _shownQ = shownQ; _hasShown = true;
        if ((shownP - baseP).sqrMagnitude > 1e-8f || Quaternion.Angle(shownQ, baseQ) > 0.01f)
        {
            root.SetPositionAndRotation(shownP, shownQ);
            OwnCar.MarkRootDirty();
            _applied = true; _appliedP = shownP; _appliedQ = shownQ;
        }
    }
    static bool _wasCapped;

    static void Reset(Vector3 p, Quaternion q, double dt)
    {
        _carryP = Vector3.zero; _carryQ = Quaternion.identity; _wasFrozen = false;
        if (_hasShown && dt > 0 && dt < 0.25) _shownV = (p - _shownP) / (float)dt;
        _shownP = p; _shownQ = q; _hasShown = true;
    }

    public static string Describe() =>
        $"freezeSmooth={(Enabled ? HookOk ? "on" : "fallback" : "off")} frozenFrames={FrozenFrames}/{Frames} maxExtrap={MaxExtrap * 1000:0}ms capped={ExtrapCapped} carries={Carries} maxCarry={MaxCarry * 100:0}cm snaps={Snaps} restores={Restores} localLerps={LocalLerps}";
}

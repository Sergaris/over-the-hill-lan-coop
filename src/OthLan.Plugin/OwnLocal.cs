using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Fusion;
using Fusion.Addons.Physics;
using UnityEngine;

namespace OthLan;

/// <summary>
/// 0.1.8 experimental "own car on the local clock" (docs/NETCODE-v4-LOCAL-OWN-CAR.md). The client's own car is stepped by
/// wall-clock time like in single player, not by Fusion's tick pacing:
///  * Fusion frozen (prediction cap reached while host data stalls): the own car is stepped locally (VPP
///    FixedUpdateNetwork + one physics step + NetworkRigidbody buffer) with the last input;
///  * Fusion catching up (several forward ticks in one frame) beyond the wall-clock budget: those ticks are undone for
///    the own car (state captured before the tick and put back), so the car never moves faster than real time.
/// Resimulations stay neutralised by OwnCar (restore after resim). Phase 2 (client-authoritative) only.
/// </summary>
internal static unsafe class OwnLocal
{
    public static bool Enabled => Plugin.OwnLocalClock.Value;
    public static double Debt; public static int LocalSteps, FusionSteps, SkippedTicks, StepErrors, Capped;
    public static long StepCount;                 // every own-car step (Fusion forward tick kept or local), for OwnRender
    public static double LastStepAt = -1;
    public static Vector3 PrevP; public static Quaternion PrevQ; public static bool HasPrev;   // own pose before the last step
    static void MarkPrev() { var rb = OwnCar.Rb; if (rb == null) return; PrevP = rb.position; PrevQ = rb.rotation; HasPrev = true; }
    public static bool ActiveNow => Active(NetClock.Now);
    static int _tickFrame = -1; static double _lastFrameAt = -1;
    static bool _skipPending; static readonly OwnCar.Snap _skip = new();
    static MethodInfo _copyToBuffer; static readonly object[] _false = { false };

    // ---- fresh input for local steps: ask the game's VehicleInput for the current controls (same code Fusion's
    // OnInput uses), keep only steering/throttle/brake, and put the VehicleInput's button/edge state back so no press
    // is consumed. Written into FusionPlayer.CurrentFrameInput (words 1..3) which VPP reads during the step.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void OnInputFn(IntPtr self, IntPtr runner, IntPtr input, IntPtr mi);
    static OnInputFn _onInput; static IntPtr _onInputMi; static bool _inputTried;
    const int ViSaveFrom = 0x68, ViSaveLen = 0x38;   // VehicleInput.buttons .. selectedGear (button/edge state)
    const string K_In = "VehicleInput::OnInput(NetworkRunner,NetworkInput)";
    static IntPtr _inBuf, _inStruct; static VehicleInput _vi; static IntPtr _viFor;
    public static int InputRefreshes, InputErrors;
    public static float LastSteer;   // steering of the latest refresh (self-test: does the wheel move during an outage)
    // VehicleInput keeps its own steering/throttle/brake smoothing in prevFrame*; those must advance with every refresh
    // (0.2.4 live: restoring them with the button state froze the wheel angle during outages, throttle still worked).
    static int _smoothFrom = -1, _smoothTo = -1;
    static void InitInput()
    {
        if (_inputTried) return; _inputTried = true;
        try
        {
            var f = typeof(VehicleInput).GetField("NativeMethodInfoPtr_Fusion_INetworkRunnerCallbacks_OnInput_Private_Virtual_Final_New_Void_NetworkRunner_NetworkInput_0", BindingFlags.NonPublic | BindingFlags.Static);
            _onInputMi = f != null ? (IntPtr)f.GetValue(null) : IntPtr.Zero;
            var fn = OptionalHooks.Resolve(typeof(VehicleInput), "NativeMethodInfoPtr_Fusion_INetworkRunnerCallbacks_OnInput_Private_Virtual_Final_New_Void_NetworkRunner_NetworkInput_0", K_In);
            if (fn == IntPtr.Zero) { Plugin.L.LogWarning("[OwnLocal] fresh input unavailable: local steps keep the last input"); return; }
            _onInput = Marshal.GetDelegateForFunctionPointer<OnInputFn>(fn);
            _inBuf = Marshal.AllocHGlobal(4 * 64); _inStruct = Marshal.AllocHGlobal(16);
            var s = FusionLayout.TryField("Assembly-CSharp.dll", "", "VehicleInput", "prevFrameSteering");
            var b = FusionLayout.TryField("Assembly-CSharp.dll", "", "VehicleInput", "prevFrameBrake");
            if (s >= ViSaveFrom && b > s && b + 4 <= ViSaveFrom + ViSaveLen) { _smoothFrom = s; _smoothTo = b + 4; }
            if (_smoothFrom >= 0) Plugin.L.LogInfo($"[OwnLocal] fresh input: steering smoothing 0x{s:X}..0x{b + 4:X} kept");
            else Plugin.L.LogWarning($"[OwnLocal] fresh input: steering smoothing NOT found (0x{s:X}..0x{b + 4:X}): wheel may not turn in local steps");
        }
        catch (Exception e) { _onInput = null; Plugin.L.LogWarning($"[OwnLocal] input init: {e.Message}"); }
    }
    static void RefreshInput()
    {
        if (!Plugin.OwnLocalFreshInput.Value) return;
        InitInput(); if (_onInput == null) return;
        var own = OwnCar.Own; var r = OwnCar.Runner; if (own == null || r == null || own.Ptr == null) return;
        try
        {
            if (_viFor != own.Pointer) { _vi = own.GetComponentInChildren<VehicleInput>() ?? UnityEngine.Object.FindObjectOfType<VehicleInput>(); _viFor = own.Pointer; }
            if (_vi == null) return;
            var vi = (byte*)_vi.Pointer;
            var save = stackalloc byte[ViSaveLen]; for (int i = 0; i < ViSaveLen; i++) save[i] = vi[ViSaveFrom + i];   // buttons .. selectedGear
            var buf = (uint*)_inBuf; for (int i = 0; i < 64; i++) buf[i] = 0;
            *(IntPtr*)_inStruct = _inBuf; *(int*)((byte*)_inStruct + 8) = 32; *(int*)((byte*)_inStruct + 12) = 0;
            _onInput(_vi.Pointer, r.Pointer, _inStruct, _onInputMi);
            for (int i = 0; i < ViSaveLen; i++)
                if (ViSaveFrom + i < _smoothFrom || ViSaveFrom + i >= _smoothTo) vi[ViSaveFrom + i] = save[i];
            var data = (uint*)(buf + 1); var w = (uint*)own.Ptr;
            w[1] = data[1]; w[2] = data[2]; w[3] = data[3];   // steering, throttle, brake
            LastSteer = *(float*)(data + 1);
            InputRefreshes++;
        }
        catch (Exception e) { InputErrors++; if (InputErrors < 4) Plugin.L.LogWarning($"[OwnLocal] input: {e.Message}"); }
    }

    /// <summary>0.1.8.1 live toggle (F7, main thread). Safe mid-session: a pending skipped tick is put back first, then
    /// the local-clock debt and OwnRender's interpolation (whose tick basis changes with Enabled) start over.</summary>
    public static void SetEnabled(bool on, string why)
    {
        if (Plugin.OwnLocalClock.Value == on) return;
        try { RestoreSkip(); } catch (Exception e) { Plugin.L.LogWarning($"[OwnLocal] toggle restore: {e.Message}"); }
        Forget(); OwnRender.Forget();
        Plugin.OwnLocalClock.Value = on;
        Plugin.L.LogInfo($"[OwnLocal] own car on the local clock {(on ? "on" : "off")} ({why})");
    }

    public static void Forget() { HasPrev = false; Debt = 0; _skipPending = false; LastStepAt = -1; _lastFrameAt = -1; }

    static bool Active(double now) => Enabled && OwnCar.Phase2Active(now) && !OwnCar.InteractionSuspended
        && OwnCar.Rb != null && OwnCar.Own != null && !OwnCar.Rb.isKinematic && !OwnCar.AcceptingHostPose(now);

    /// <summary>Own car, forward tick, from OwnCar.BeforeTickDetour (after the after-resim restore).</summary>
    public static void OnForwardTick()
    {
        var now = NetClock.Now;
        RestoreSkip();
        if (!Active(now)) { StepCount++; LastStepAt = now; Debt = 0; return; }
        if (Debt >= -0.5) { MarkPrev(); Debt -= 1; FusionSteps++; StepCount++; LastStepAt = now; _tickFrame = Time.frameCount; return; }
        // over the real-time budget: this tick must not move our car
        OwnCar.CaptureInto(_skip); _skipPending = _skip.Ok; SkippedTicks++;
    }

    /// <summary>Before anything reads/writes the own state outside a tick: reset capture, render, next tick.</summary>
    public static void RestoreSkip()
    {
        if (!_skipPending) return;
        _skipPending = false;
        // The NetworkRigidbody words must follow: NRB copies them back into the rigidbody (CopyToEngine) at the next
        // tick when the root is dirty, and the captured words can be older than the rigidbody (NRB writes its words
        // once per update, not per tick) -> v3k fl1L showed 20 cm steps back to the pose before the catch-up.
        try { OwnCar.RestoreFrom(_skip, true); SyncWords(); } catch (Exception e) { StepErrors++; if (StepErrors < 4) Plugin.L.LogWarning($"[OwnLocal] skip restore: {e.Message}"); }
    }

    /// <summary>Main thread, after Fusion sim (OwnClock.FrameAfterFusion via NetFrame.AfterFusion / T2.3).</summary>
    public static void Frame()
    {
        var now = NetClock.Now;
        var dt = _lastFrameAt < 0 ? 0 : now - _lastFrameAt; _lastFrameAt = now;
        var r = OwnCar.Runner;
        if (r == null || !Active(now) || dt <= 0 || dt > 0.5) { Debt = 0; return; }
        RestoreSkip();
        var step = r.DeltaTime > 0 ? r.DeltaTime : 0.02f;
        Debt += dt / step;
        if (Debt > 25) { Debt = 25; Capped++; }
        if (Debt < -3) Debt = -3;
        // Pure accumulator (single-player style): whatever real time Fusion's kept ticks did not cover is stepped here.
        if (LastStepAt < 0) return;
        // 0.1.9 R2: never pile local Physics.Simulate on top of a Fusion tick in the same frame (double scene step),
        // and cap at 2 local steps/frame (was 4) to bound frame-time spikes on weaker PCs.
        if (_tickFrame == Time.frameCount) return;
        // 0.2.5.2: if this frame is already over ~28 ms, do not add local Physics.Simulate (live: friend frameMs≈30
        // with Debt capped and LocalStep piling on → death spiral vs pre-0.2).
        if (!OwnCarLogic.AllowLocalPhysicsStep(dt, 0.028)) return;
        int n = 0;
        if (Debt >= 1) RefreshInput();
        while (Debt >= 1 && n < 2) { if (!LocalStep(step)) break; Debt -= 1; n++; }
    }

    public static int WordSyncs;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void CopyToBufferFn(IntPtr self, byte force, IntPtr mi);
    static CopyToBufferFn _copyFn; static IntPtr _copyMi; static bool _copyTried;
    const string K_Copy = "NetworkRigidbody::CopyToBuffer(bool)";
    static void InitCopy()
    {
        if (_copyTried) return; _copyTried = true;
        try
        {
            var f = typeof(NetworkRigidbody<Rigidbody, RunnerSimulatePhysics3D>).GetField(
                "NativeMethodInfoPtr_CopyToBuffer_Protected_Virtual_New_Void_Boolean_0", BindingFlags.NonPublic | BindingFlags.Static);
            if (f != null) _copyMi = (IntPtr)f.GetValue(null);
            // Prefer a direct IL2CPP call (R2): skip OptionalHooks RVA gate — CopyToBuffer is not a detour target, we only
            // invoke it. Fall back to managed reflection if the MethodInfo* is missing.
            if (_copyMi != IntPtr.Zero)
            {
                unsafe { var fn = *(IntPtr*)_copyMi; if (fn != IntPtr.Zero) _copyFn = Marshal.GetDelegateForFunctionPointer<CopyToBufferFn>(fn); }
                if (_copyFn != null) Plugin.L.LogInfo("[OwnLocal] CopyToBuffer: direct IL2CPP call");
            }
            if (_copyFn == null)
            {
                _copyToBuffer = typeof(NetworkRigidbody<Rigidbody, RunnerSimulatePhysics3D>).GetMethod("CopyToBuffer", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                Plugin.L.LogInfo($"[OwnLocal] CopyToBuffer: reflection fallback ({(_copyToBuffer != null ? "ok" : "missing")})");
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[OwnLocal] CopyToBuffer init: {e.Message}"); }
    }
    static void SyncWords()
    {
        var nrb = OwnCar.Nrb; if (nrb == null) return;
        InitCopy();
        if (_copyFn != null) { try { _copyFn(nrb.Pointer, 0, _copyMi); WordSyncs++; return; } catch { } }
        _copyToBuffer?.Invoke(nrb, _false); WordSyncs++;
    }

    static bool LocalStep(float step)
    {
        var rb = OwnCar.Rb; var own = OwnCar.Own; var vb = own.vehicleBase; var nrb = OwnCar.Nrb;
        if (vb == null || nrb == null) return false;
        using var _cost = FrameCost.Measure(FrameCost.LocalStep);
        try
        {
            own.transform.SetPositionAndRotation(rb.position, rb.rotation);   // undo the render pose
            MarkPrev();
            vb.FixedUpdateNetwork();
            // T3.7: kinematic remotes at step-end pose so contacts see velocity, not a teleport.
            if (LanState.Active == LanRole.Client)
                RemoteCarView.PrePhysicsStep(NetClock.Now + step);
            Physics.Simulate(step);
            if (LanState.Active == LanRole.Client)
                RemoteCarView.PostPhysicsStep();
            InitCopy();
            if (_copyFn != null) _copyFn(nrb.Pointer, 0, _copyMi);
            else { _copyToBuffer ??= typeof(NetworkRigidbody<Rigidbody, RunnerSimulatePhysics3D>).GetMethod("CopyToBuffer", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public); _copyToBuffer?.Invoke(nrb, _false); }
            LocalSteps++; StepCount++; LastStepAt = NetClock.Now;
            return true;
        }
        catch (Exception e) { StepErrors++; if (StepErrors < 4) Plugin.L.LogWarning($"[OwnLocal] local step: {e}"); return false; }
    }

    public static string Describe() => $"localClock={(Enabled ? "on" : "off")} localSteps={LocalSteps} fusionSteps={FusionSteps} skipped={SkippedTicks} errors={StepErrors} wordSyncs={WordSyncs} capped={Capped} input={InputRefreshes}/{InputErrors} debt={Debt:0.0}";
}

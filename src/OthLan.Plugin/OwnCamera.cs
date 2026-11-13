using Cinemachine;

namespace OthLan;

/// <summary>
/// 0.2.7 follow camera on the own car's draw clock. The game's Cinemachine camera (CinemachineManualUpdateDriver,
/// LateUpdate) damps by Unity's deltaTime, which is the previous frame's length; on the client the own car is drawn by
/// <see cref="OwnClock.FrameSeconds"/> (this frame's real time). With uneven frames (0/1/2 Fusion ticks, resims) the
/// camera under- then overshoots the car every few frames (live 0.2.7 lowoff: camera step / car step tracked
/// prevFrame/thisFrame with corr 0.83, slope 0.95; car vs camera p95 6-11 cm, car in world &lt;1 cm).
/// Single player keeps both on Unity time, so it is client only.
/// Do not call Brain.ManualUpdate from Late: that double-steps damping when LateUpdate already ran (0.2.8 regression).
/// </summary>
internal static class OwnCamera
{
    const double MaxStepSeconds = 0.25;   // hitch: leave Unity's deltaTime (capped by Time.maximumDeltaTime)
    static bool _set; static float _orig;
    public static int Frames;

    /// <summary>NetFrame.AfterFusion, every frame, before the camera's LateUpdate.</summary>
    public static void Frame()
    {
        var dt = OwnClock.FrameSeconds;
        if (!Plugin.CameraOnCarClock.Value || LanState.Active != LanRole.Client || OwnCar.Own == null || dt <= 0 || dt > MaxStepSeconds)
        { Release(); return; }
        if (!_set) { _orig = CinemachineCore.UniformDeltaTimeOverride; _set = true; }
        CinemachineCore.UniformDeltaTimeOverride = (float)dt;
        Frames++;
    }

    static void Release()
    {
        if (!_set) return;
        _set = false;
        CinemachineCore.UniformDeltaTimeOverride = _orig;
    }

    public static string Describe() => $"cameraOnCarClock={(Plugin.CameraOnCarClock.Value ? "on" : "off")} frames={Frames}";
}

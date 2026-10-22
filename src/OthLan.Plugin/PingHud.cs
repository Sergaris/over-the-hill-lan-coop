using System;
using System.Collections.Generic;
using Fusion;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace OthLan;

/// <summary>
/// "local NN ms" next to the game's own connection-quality icon (HUDManager.pingObject / pingIcon, which the game drives
/// from Simulation.GetPlayerRtt(LocalPlayer)*1000 in HUDManager.UpdatePingIcon RVA 0x9E4D20; tiers 50/100/150/200 ms).
/// We never touch that icon: we only read its RectTransform to place an IMGUI label to its right.
/// Shown only while a LAN session (our redirected runner) is running.
/// Value: OUR UDP echo over the real Radmin path only (client -> host:Port+1, 5/s, reply to the source port);
/// Fusion's RTT is shown only as a comparison line in F7. Host: worst client; per-player list in F7.
/// 0.1.7: no reply for 0.6 s => "no link N s" (RU "нет связи N с") in red; normal colour again after 3 replies in a row.
/// Colours: green &lt;= 200 ms, yellow 200..300, red &gt; 300 (cfg PingIndicator.YellowAboveMs / RedAboveMs).
/// </summary>
internal static class PingHud
{
    static double _smoothed = double.NaN;  // already smoothed by EchoTracker
    static float _nextPing, _nextFind;
    static HUDManager _hud;
    public static string Source = "-";
    public static readonly Dictionary<int, double> PerSlotMs = new();
    static GUIStyle _style;
    static readonly Il2CppStructArray<Vector3> Corners = new(4);

    public static double SmoothedMs => _smoothed;
    /// <summary>Echo loss in % (client: to the host; host: worst client), last 20 pings.</summary>
    public static double LossPct;

    static NetworkRunner LanRunner()
    {
        if (LanState.Active == LanRole.Off || LanState.LanRunner == IntPtr.Zero) return null;
        var r = OverlandNetworkRunner.Instance?.NetworkRunner;
        if (r == null || r.Pointer != LanState.LanRunner || !r.IsRunning) return null;
        return r;
    }

    public static bool TimedOut;
    /// <summary>Seconds without any echo (client: from the host; host: the worst client), -1 while echoes arrive.</summary>
    public static double NoLinkS = -1;
    static bool _wasActive;
    public static Msg FusionCompare = Msg.Raw("");

    /// <summary>Label value: ONLY our UDP echo over the real Radmin path (never Fusion/cloud).
    /// Client: RTT to host:Port+1. Host: worst client (a timed-out client wins => "local --" red).</summary>
    public static void Tick()
    {
        var now = Time.realtimeSinceStartup;
        var r = LanRunner();
        if (r == null)
        {
            if (_wasActive) { _wasActive = false; ClientLink.Stop(); HostPings.Clear(); }
            _smoothed = double.NaN; TimedOut = false; NoLinkS = -1; LossPct = 0; PerSlotMs.Clear(); Source = "-"; return;
        }
        _wasActive = true;
        if (now < _nextPing) return;
        _nextPing = now + 1f / Mathf.Clamp(Plugin.PingUpdateHz.Value, 1f, 30f);

        try
        {
            if (LanState.Active == LanRole.Client)
            {
                ClientLink.Ensure(LanState.TargetIp, LanState.TargetPort + 1);
                ClientLink.Tracker.Alpha = Plugin.PingSmoothing.Value;
                ClientLink.SendPing((byte)LanState.ClientSlot);
                double v, nl; bool to; double loss;
                lock (ClientLink.Tracker) { var t = ClientLink.Now; v = ClientLink.Tracker.Value(t); to = ClientLink.Tracker.TimedOut(t); nl = ClientLink.Tracker.NoLinkSeconds(t); loss = ClientLink.Tracker.LossPercent; }
                _smoothed = v; TimedOut = to; NoLinkS = nl; LossPct = loss;
                Source = $"udp echo {LanState.TargetIp}:{LanState.TargetPort + 1}, loss {loss:0}%";
                var fus = r.GetPlayerRtt(r.LocalPlayer) * 1000.0;
                FusionCompare = new Msg("f7.fusionPing", new Msg("unit.ms", fus));
            }
            else
            {
                HostPings.SendAll();
                PerSlotMs.Clear();
                double worst = double.NaN, worstLoss = 0, worstNoLink = -1; bool anyTimeout = false; var fus = new List<Msg>();
                var snap = HostPings.Snapshot();
                try { LinkLabels.HostUpdate(snap, r); } catch { }
                foreach (var kv in snap)
                {
                    if (!r.IsPlayerValid(PlayerRef.FromIndex(kv.Key))) continue;
                    var (ms, to, ls, nl) = kv.Value;
                    if (ls > worstLoss) worstLoss = ls;
                    if (nl > worstNoLink) worstNoLink = nl;
                    PerSlotMs[kv.Key] = nl >= 0 ? double.NaN : ms;
                    if (to) anyTimeout = true;
                    if (!double.IsNaN(ms) && nl < 0 && (double.IsNaN(worst) || ms > worst)) worst = ms;
                    fus.Add(new Msg("f7.playerPing", kv.Key, (r.GetPlayerRtt(PlayerRef.FromIndex(kv.Key)) * 1000.0).ToString("0")));
                }
                TimedOut = anyTimeout; NoLinkS = worstNoLink; LossPct = worstLoss;
                _smoothed = worstNoLink >= 0 ? double.NaN : worst;
                Source = PerSlotMs.Count == 0 ? "udp echo: no clients seen yet" : "udp echo, worst client";
                FusionCompare = new Msg("f7.fusionPing", Msg.List(fus.ToArray()));
            }
        }
        catch (Exception e) { Source = "error: " + e.Message; }
    }

    // Last drawn state, GUI coordinates (y down), for the self-test log.
    public static Rect LastLabel, LastIcon;
    public static string LastText = "", LastAnchor = "none";
    public static PingLogic.Level LastLevel = PingLogic.Level.Waiting;
    public static bool LastIconActive;
    public static int LastFrame = -1;

    public static void OnGUI(bool f7Open = false)
    {
        if (!Plugin.PingShow.Value || LanRunner() == null) { LastAnchor = "none"; return; }
        if (f7Open) { LastAnchor = "f7-open"; return; }   // the F7 window shows the same figure; no overlap with it
        var now = Time.realtimeSinceStartup;
        if (_hud == null && now >= _nextFind)
        {
            _nextFind = now + 2f;
            try { _hud = UnityEngine.Object.FindObjectOfType<HUDManager>(); } catch { _hud = null; }
        }

        var scale = Screen.height / 1080f;
        float x = Plugin.PingFallbackX.Value * scale, y = Plugin.PingFallbackY.Value * scale, h = 28 * scale;
        var anchor = "fallback";
        if (_hud != null)
        {
            try
            {
                if (!_hud.isActiveAndEnabled) { LastAnchor = "hud-inactive"; return; }
                var cg = _hud.canvasGroup;
                if (cg != null && cg.alpha < 0.05f) { LastAnchor = "hud-hidden"; return; }   // HUD hidden (menus, photo mode...)
                var go = _hud.pingObject;
                var rt = go != null ? go.transform.TryCast<RectTransform>() : null;
                if (rt != null)
                {
                    rt.GetWorldCorners(Corners);
                    var canvas = go.GetComponentInParent<Canvas>();
                    Camera cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
                    var bl = RectTransformUtility.WorldToScreenPoint(cam, Corners[0]);
                    var tr = RectTransformUtility.WorldToScreenPoint(cam, Corners[2]);
                    if (tr.x > bl.x && tr.y > bl.y)
                    {
                        LastIcon = new Rect(bl.x, Screen.height - tr.y, tr.x - bl.x, tr.y - bl.y);
                        LastIconActive = go.activeInHierarchy;
                        anchor = "icon";
                        h = Mathf.Max(18 * scale, tr.y - bl.y);
                        x = tr.x + 6 * scale;
                        y = Screen.height - tr.y;                          // GUI y grows downwards
                    }
                }
            }
            catch { _hud = null; }
        }

        var ms = _smoothed;
        var lvl = PingLogic.ClassifyEcho(ms, TimedOut, Plugin.PingYellowAboveMs.Value, Plugin.PingRedAboveMs.Value);
        var col = lvl switch
        {
            PingLogic.Level.Waiting => new Color(0.8f, 0.8f, 0.8f),
            PingLogic.Level.Red => new Color(0.95f, 0.25f, 0.2f),
            PingLogic.Level.Yellow => new Color(1f, 0.86f, 0.15f),
            _ => new Color(0.35f, 0.9f, 0.35f),
        };
        _style ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold };
        _style.fontSize = Mathf.RoundToInt(18 * scale);
        var text = PingLogic.LabelText(ms, LossPct, Plugin.PingShowLossFromPct.Value, NoLinkS);
        var rect = new Rect(x, y, 320 * scale, h);
        var shadow = new Rect(x + 1.5f * scale, y + 1.5f * scale, rect.width, h);
        _style.normal.textColor = new Color(0, 0, 0, 0.8f);
        GUI.Label(shadow, text, _style);
        _style.normal.textColor = col;
        GUI.Label(rect, text, _style);
        LastLabel = rect; LastText = text; LastLevel = lvl; LastAnchor = anchor; LastFrame = Time.frameCount;
    }
}

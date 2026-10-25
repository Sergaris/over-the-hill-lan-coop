using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace OthLan;

/// <summary>
/// 0.1.7: "⚠ no link N s" over the car of a player the HOST has lost the link to (ROADMAP 1.3, physics-only phase and
/// later), for everyone else: the host draws it from its own echo tracker, and sends the list to the clients ("OTST",
/// 5/s while anyone is affected) so they draw it too. The player who lost the link sees "no link" on their own HUD.
/// </summary>
internal static class LinkLabels
{
    static readonly Dictionary<int, double> _noLink = new();   // slot -> seconds (host: own measurement; client: from host)
    static double _statusAt = -1;                               // client: when the last status arrived (ClientLink clock)
    static readonly List<(byte, double)> _rx = new();
    static bool _sentNonEmpty;
    static GUIStyle _style;
    static FusionPlayer[] _players = Array.Empty<FusionPlayer>();
    public static string Last = "";                             // self-test log: "slot:seconds;..."
    static readonly OutageLogic.Cfg Ocfg = new();

    /// <summary>Host, from PingHud.Tick (5/s): remember and forward to the clients.</summary>
    public static void HostUpdate(Dictionary<int, (double ms, bool timedOut, double loss, double noLinkS)> snap, NetworkRunner r)
    {
        Ocfg.ExtrapolateS = Plugin.OutExtrapolateSeconds.Value;
        var items = new List<(byte, double)>();
        lock (_noLink)
        {
            _noLink.Clear();
            foreach (var kv in snap)
            {
                if (!r.IsPlayerValid(PlayerRef.FromIndex(kv.Key))) continue;
                if (OutageLogic.ShowCarLabel(kv.Value.noLinkS, Ocfg)) { _noLink[kv.Key] = kv.Value.noLinkS; items.Add(((byte)kv.Key, kv.Value.noLinkS)); }
            }
        }
        if (items.Count > 0 || _sentNonEmpty)
        {
            var b = OutageLogic.EncodeStatus(items);
            HostPings.Broadcast(b);
            _sentNonEmpty = items.Count > 0;
        }
    }

    /// <summary>Client network thread.</summary>
    public static void OnStatus(byte[] d, double now)
    {
        lock (_rx)
        {
            if (!OutageLogic.TryDecodeStatus(d, _rx)) return;
            lock (_noLink) { _noLink.Clear(); foreach (var (s, t) in _rx) _noLink[s] = t; _statusAt = now; }
        }
    }

    public static void OnGUI()
    {
        if (LanState.Active == LanRole.Off) { _noLink.Clear(); Last = ""; return; }
        KeyValuePair<int, double>[] list;
        lock (_noLink)
        {
            if (LanState.Active == LanRole.Client && NetClock.Now - _statusAt > 1.0) _noLink.Clear();   // host stopped saying it
            if (_noLink.Count == 0) { Last = ""; return; }
            list = new KeyValuePair<int, double>[_noLink.Count]; int i = 0; foreach (var kv in _noLink) list[i++] = kv;
        }
        var cam = Camera.main; if (cam == null) return;
        _players = CarRegistry.Players;
        var scale = Screen.height / 1080f;
        _style ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        _style.fontSize = Mathf.RoundToInt(20 * scale);
        var sb = new System.Text.StringBuilder();
        foreach (var kv in list)
        {
            sb.Append(kv.Key).Append(':').Append(kv.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(';');
            foreach (var fp in _players)
            {
                try
                {
                    if (fp == null || fp.Object == null || fp.Object.HasInputAuthority) continue;
                    if (fp.Object.InputAuthority != PlayerRef.FromIndex(kv.Key)) continue;
                    var sp = cam.WorldToScreenPoint(fp.transform.position + Vector3.up * 2.2f);
                    if (sp.z <= 0) break;
                    var text = Loc.F("ping.noLinkCar", PingLogic.NoLinkText(kv.Value));
                    var w = 260 * scale; var h = 30 * scale;
                    var rect = new Rect(sp.x - w / 2, Screen.height - sp.y - h, w, h);
                    _style.normal.textColor = new Color(0, 0, 0, 0.85f);
                    GUI.Label(new Rect(rect.x + 1.5f * scale, rect.y + 1.5f * scale, w, h), text, _style);
                    _style.normal.textColor = new Color(1f, 0.35f, 0.25f);
                    GUI.Label(rect, text, _style);
                    break;
                }
                catch { }
            }
        }
        Last = sb.ToString();
    }

    public static void Clear() { lock (_noLink) _noLink.Clear(); _sentNonEmpty = false; }
}

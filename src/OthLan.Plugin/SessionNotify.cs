using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace OthLan;

/// <summary>
/// 0.2.0 T6.5: session QoL toasts via <see cref="JoinToast"/>. Reads PingHud / runner / slots;
/// does not own no-link thresholds. Freeze rejoin already goes through <see cref="LanState.Status"/>.
/// Multiple events in one frame are merged into a single <see cref="JoinToast.Show"/> (one line).
/// </summary>
internal static class SessionNotify
{
    static bool _wasTimedOut;
    static bool _hadRunner;
    static bool _wasClientSession;
    static bool _hostGoneShown;
    static float _nextNoLinkRefresh = -1;
    static readonly bool[] _wasPresent = new bool[16];
    static bool _slotSnapReady;
    static readonly List<string> _pending = new();

    public static void Tick()
    {
        var client = LanState.Active == LanRole.Client;
        var host = LanState.Active == LanRole.Host;
        var r = Runner();
        var hasRunner = r != null;

        if (client)
        {
            if (SessionNotifyLogic.HostSessionEnded(_wasClientSession, _hadRunner, hasRunner) && !_hostGoneShown)
            {
                _hostGoneShown = true;
                Enqueue(Loc.T("session.notifyHostGone"));
            }
            if (hasRunner)
            {
                _hostGoneShown = false;
                var to = PingHud.TimedOut;
                if (SessionNotifyLogic.BecameNoLink(_wasTimedOut, to))
                {
                    EnqueueNoLink();
                    _nextNoLinkRefresh = Time.realtimeSinceStartup + 5f;
                }
                else if (to && Time.realtimeSinceStartup >= _nextNoLinkRefresh)
                {
                    EnqueueNoLink();
                    _nextNoLinkRefresh = Time.realtimeSinceStartup + 5f;
                }
                else if (SessionNotifyLogic.BecameLinked(_wasTimedOut, to))
                    _nextNoLinkRefresh = -1;
                _wasTimedOut = to;
            }
            else
            {
                _wasTimedOut = false;
                _nextNoLinkRefresh = -1;
            }
        }
        else
        {
            _wasTimedOut = false;
            _nextNoLinkRefresh = -1;
            _hostGoneShown = false;
        }

        if (host && hasRunner)
            TickPlayerPresence(r);

        Flush();

        _hadRunner = hasRunner;
        _wasClientSession = client;
        if (!client && !host)
        {
            _hadRunner = false;
            _wasClientSession = false;
            _slotSnapReady = false;
            Array.Clear(_wasPresent, 0, _wasPresent.Length);
            _pending.Clear();
        }
    }

    /// <summary>Host: slots released by <see cref="SlotAllocator.Sync"/> (player left). Queued until <see cref="Tick"/> flushes.</summary>
    public static void OnSlotsReleased(IEnumerable<int> slots)
    {
        if (LanState.Active != LanRole.Host || slots == null) return;
        foreach (var sl in slots)
        {
            if (sl < 0 || sl >= _wasPresent.Length) continue;
            _wasPresent[sl] = false;
            Enqueue(Loc.F("session.notifyLeave", DisplayName(sl)));
        }
    }

    static void TickPlayerPresence(NetworkRunner r)
    {
        var n = Math.Min(_wasPresent.Length, Math.Max(LanState.EffectivePlayers, 2));
        for (var i = 0; i < n; i++)
        {
            if (i == LanState.HostIndex) { _wasPresent[i] = true; continue; }
            bool present = false;
            try { present = r.IsPlayerValid(PlayerRef.FromIndex(i)); } catch { }
            if (_slotSnapReady && SessionNotifyLogic.BecamePresent(_wasPresent[i], present))
                Enqueue(Loc.F("session.notifyJoin", DisplayName(i)));
            // leave: Overlay → OnSlotsReleased (SlotAllocator), not here (avoids double toast)
            _wasPresent[i] = present;
        }
        _slotSnapReady = true;
    }

    static void EnqueueNoLink() =>
        Enqueue(Loc.F("session.notifyNoLink", PingLogic.NoLinkText(PingHud.NoLinkS)));

    static void Enqueue(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        _pending.Add(line);
    }

    static void Flush()
    {
        if (_pending.Count == 0) return;
        var text = SessionNotifyLogic.JoinLines(_pending);
        _pending.Clear();
        if (text.Length > 0) JoinToast.Show(text, 4f);
    }

    static string DisplayName(int slot) => Loc.F("session.playerN", slot);

    static NetworkRunner Runner()
    {
        if (LanState.Active == LanRole.Off || LanState.LanRunner == IntPtr.Zero) return null;
        var r = OverlandNetworkRunner.Instance?.NetworkRunner;
        return r != null && r.Pointer == LanState.LanRunner && r.IsRunning ? r : null;
    }
}

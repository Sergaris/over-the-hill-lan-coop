using System;
using System.Collections.Generic;
using UnityEngine;

namespace OthLan;

/// <summary>
/// 0.2.0 T6.4: session roster (host broadcasts OTPL at 1 Hz) and the panel actions.
/// Pause-menu button: <see cref="CoopSessionPause"/>. Drawing: <see cref="SessionPanel"/>.
/// </summary>
internal static class CoopSession
{
    static readonly List<CoopSessionLogic.Row> _rows = new();
    static readonly object _gate = new();
    static string _body = "";
    static byte[] _pending;
    static float _nextPub;
    static int _diag;

    public static void Tick()
    {
        if (LanState.Active == LanRole.Off) { Idle(); return; }
        CoopSessionPause.Tick();
        if (SessionPanel.IsOpen && !CoopSessionPause.MenuVisible) SessionPanel.Close();
        if (CoopSessionPause.ConsumeOpen()) OpenPanel();
        if (LanState.Active == LanRole.Host)
        {
            if (Time.realtimeSinceStartup >= _nextPub) { _nextPub = Time.realtimeSinceStartup + 1f; Publish(); }
        }
        else ApplyList();
        DrainDiag();
        SessionResync.Tick();
        SessionPanel.Tick();
        if (SessionPanel.IsOpen) { Cursor.visible = true; Cursor.lockState = CursorLockMode.None; }
    }

    public static void OnList(byte[] d)
    {
        if (d == null || d.Length == 0) return;
        var copy = new byte[d.Length];
        Array.Copy(d, copy, d.Length);
        lock (_gate) _pending = copy;
    }

    public static void OnDiag() => System.Threading.Interlocked.Exchange(ref _diag, 1);

    internal static void Resync()
    {
        CarStreamSender.BumpEpoch("user");
        if (LanState.Active == LanRole.Client && OwnCar.OwnSlot >= 0 && OwnCar.OwnSlot <= 254)
            ClientLink.Send(CoopSessionLogic.EncodeResync((byte)OwnCar.OwnSlot));
        JoinToast.Show(Loc.T("session.resyncDone"));
    }

    internal static void SaveDiag() => ToastExport(Diagnostics.ExportZip("session panel"), "session.saved");

    internal static void DiagAll()
    {
        if (LanState.Active != LanRole.Host) return;
        HostPings.Broadcast(CoopSessionLogic.EncodeDiag());
        ToastExport(Diagnostics.ExportZip("host request"), "session.saved");
    }

    internal static void Leave()
    {
        SessionPanel.Close();
        try { OverlandNetworkRunner.Instance?.QuitToMenuAsync(MainMenuContext.Reason.PlayerChoice, "OTH-LAN: leave session"); }
        catch (Exception e) { Plugin.L.LogWarning($"[Session] leave: {e.Message}"); }
    }

    static void Idle()
    {
        CoopSessionPause.Remove();
        SessionPanel.Close();
        SessionResync.Clear();
        _body = "";
        lock (_gate) _pending = null;
    }

    static void OpenPanel()
    {
        if (SessionPanel.IsOpen) { SessionPanel.Close(); return; }
        if (LanState.Active == LanRole.Host) Publish();
        var host = LanState.Active == LanRole.Host;
        if (!SessionPanel.Open(CoopSessionPause.Template, host)) return;
        SessionPanel.SetBody(string.IsNullOrEmpty(_body) ? Loc.T("session.empty") : _body);
    }

    static void Publish()
    {
        Fill(_rows);
        HostPings.Broadcast(CoopSessionLogic.EncodeList(_rows));
        Remember();
    }

    static void ApplyList()
    {
        byte[] pkt;
        lock (_gate) { pkt = _pending; _pending = null; }
        if (pkt == null) return;
        if (!CoopSessionLogic.TryDecodeList(pkt, _rows)) return;
        Remember();
    }

    static void Remember()
    {
        _body = _rows.Count == 0 ? Loc.T("session.empty") : CoopSessionLogic.FormatBlock(_rows);
        SessionPanel.SetBody(_body);
    }

    static void DrainDiag()
    {
        if (System.Threading.Interlocked.Exchange(ref _diag, 0) != 1) return;
        if (LanState.Active != LanRole.Client) return;
        ToastExport(Diagnostics.ExportZip("host request"), "session.diagSent");
    }

    static void ToastExport(string path, string okKey)
    {
        if (string.IsNullOrEmpty(path)) JoinToast.Show(Loc.T("session.saveFailed"));
        else JoinToast.Show(Loc.F(okKey, System.IO.Path.GetFileName(path)));
    }

    static void Fill(List<CoopSessionLogic.Row> dst)
    {
        dst.Clear();
        var local = LocalSlot();
        var localName = LocalName();
        var pause = PauseNames();
        var slots = new List<int>();
        if (local >= 0 && local <= 254) slots.Add(local);
        foreach (var kv in HostPings.Snapshot()) AddSlot(slots, kv.Key);
        var cars = CarRegistry.Cars;
        for (var i = 0; i < cars.Count; i++) AddSlot(slots, cars[i].Slot);
        slots.Sort();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var snap = HostPings.Snapshot();
        for (var i = 0; i < slots.Count && dst.Count < CoopSessionLogic.MaxRows; i++)
            dst.Add(MakeRow(slots[i], local, localName, pause, used, snap));
    }

    static void AddSlot(List<int> slots, int slot)
    {
        if (slot < 0 || slot > 254 || slots.Contains(slot)) return;
        slots.Add(slot);
    }

    static CoopSessionLogic.Row MakeRow(int slot, int local, string localName, List<string> pause, HashSet<string> used,
        Dictionary<int, (double ms, bool timedOut, double loss, double noLinkS)> snap)
    {
        var self = slot == local;
        var name = self ? localName : NextName(pause, used, localName);
        used.Add(name);
        if (self && LanState.Active == LanRole.Host) name = Loc.F("session.hostName", name);
        name = CoopSessionLogic.ClipName(name);
        if (name.Length == 0) name = CoopSessionLogic.ClipName(Loc.F("session.playerN", slot));
        var shared = CoopSessionLogic.SharedFromFlags(CarSync.HostFlagsFor(slot)) || (self && OwnCar.InteractionSuspended);
        var timedOut = false;
        var loss = 0;
        var ping = -1;
        var noLinkS = 0;
        if (!self && snap.TryGetValue(slot, out var p))
        {
            timedOut = p.timedOut;
            loss = (int)Math.Round(Math.Clamp(p.loss, 0, 100));
            ping = double.IsNaN(p.ms) || p.ms <= 0 ? -1 : (int)Math.Round(Math.Clamp(p.ms, 0, 65534));
            noLinkS = timedOut ? (int)Math.Clamp(Math.Ceiling(Math.Max(0, p.noLinkS)), 1, 255) : 0;
        }
        return new CoopSessionLogic.Row
        {
            Slot = (byte)slot,
            Name = name,
            PingMs = ping,
            LossPct = (byte)loss,
            Status = CoopSessionLogic.StatusOf(timedOut, shared),
            NoLinkS = (byte)noLinkS,
        };
    }

    static string NextName(List<string> pause, HashSet<string> used, string localName)
    {
        for (var i = 0; i < pause.Count; i++)
        {
            var n = pause[i];
            if (n.Length == 0 || used.Contains(n)) continue;
            if (localName.Length > 0 && string.Equals(n, localName, StringComparison.Ordinal)) continue;
            return n;
        }
        return "";
    }

    static List<string> PauseNames()
    {
        var list = new List<string>();
        var menu = CoopSessionPause.Menu;
        if (menu == null || menu.PlayerInfos == null) return list;
        var infos = menu.PlayerInfos;
        for (var i = 0; i < infos.Count; i++)
        {
            var info = infos[i];
            if (info == null || info.WasCollected || info.NameText == null) continue;
            var n = info.NameText.text;
            if (!string.IsNullOrWhiteSpace(n)) list.Add(n.Trim());
        }
        return list;
    }

    static int LocalSlot()
    {
        if (OwnCar.OwnSlot >= 0) return OwnCar.OwnSlot;
        try
        {
            var r = OverlandNetworkRunner.Instance?.NetworkRunner;
            if (r != null && r.IsRunning) return r.LocalPlayer.AsIndex;
        }
        catch { }
        return -1;
    }

    static string LocalName()
    {
        try
        {
            var n = Steamworks.SteamFriends.GetPersonaName();
            if (!string.IsNullOrWhiteSpace(n)) return n.Trim();
        }
        catch { }
        return "";
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

namespace OthLan;

/// <summary>
/// Session CarSync flag (the host decides) and the contact box measured from colliders.
/// Host follow lives in <see cref="HostFollower"/>.
/// </summary>
internal static class CarSync
{
    static readonly Dictionary<int, byte> _slotSuspend = new();

    /// <summary>Self-test only (--othlan-contactbox): replaces the car half-extents to force the contact pause.</summary>
    public static float ContactBoxOverride;
    /// <summary>Self-test override (command line --othlan-carsync): null = config, "off", "measure", "on".</summary>
    public static string Override;
    /// <summary>This PC's own setting (used when it hosts).</summary>
    public static bool LocalSetting => Override switch { "off" => false, "measure" => true, "on" => true, _ => Plugin.CarSyncEnabled.Value };
    /// <summary>Host: session state = the host's own setting.</summary>
    public static bool HostEnabled => LocalSetting;
    public static int HostFlags => HostEnabled ? CarSyncLogic.FlagCarSync : 0;

    /// <summary>0.1.9: per-slot host ping flags (CarSync bit + optional contact/winch suspend for that client).</summary>
    public static int HostFlagsFor(int slot)
    {
        var f = HostFlags;
        lock (_slotSuspend) if (_slotSuspend.TryGetValue(slot, out var s)) f |= s;
        return f;
    }

    static volatile int _hostFlag = -1;
    public static int HostFlag => _hostFlag;
    /// <summary>0.1.9 R3: host says this client's car is in a contact/winch pause.</summary>
    public static bool HostSuspendContact, HostSuspendWinch;
    public static bool ClientEnabled => _hostFlag == 1;

    /// <summary>Effective state for this PC in the current LAN session.</summary>
    public static bool Enabled => LanState.Active switch
    {
        LanRole.Client => ClientEnabled,
        LanRole.Host => HostEnabled,
        _ => LocalSetting,
    };
    public static bool ApplyEnabled => LanState.Active == LanRole.Host && HostEnabled && Override != "measure";

    /// <summary>Network thread (ClientLink) or main thread (info query): the host's session flag.</summary>
    public static void OnHostFlags(int flags, double now)
    {
        var v = (flags & CarSyncLogic.FlagCarSync) != 0 ? 1 : 0;
        var old = _hostFlag;
        _hostFlag = v;
        HostSuspendContact = (flags & CarSyncLogic.FlagSuspendContact) != 0;
        HostSuspendWinch = (flags & CarSyncLogic.FlagSuspendWinch) != 0;
        if (old != v) Plugin.L.LogInfo($"[CarSync] host says CarSync {(v == 1 ? "ON" : "OFF")} for this session");
    }

    /// <summary>The F7 button (and the self-test autopilot, through the same call).</summary>
    public static bool UserToggle(string source)
    {
        if (LanState.Active == LanRole.Client) { Plugin.L.LogInfo($"[CarSync] toggle ignored ({source}): the host decides"); return false; }
        var v = !LocalSetting;
        if (Override == "on" || Override == "off") Override = null;
        Plugin.CarSyncEnabled.Value = v;
        Plugin.L.LogWarning($"[CarSync] {(v ? "ENABLED" : "DISABLED")} via {source}{(LanState.Active == LanRole.Host ? " (clients follow within ~0.2 s)" : "")}");
        return true;
    }

    internal static void NoteNoRunner() => _hostFlag = -1;

    internal static void SetSlotSuspend(int slot, byte flags)
    {
        lock (_slotSuspend) { if (flags != 0) _slotSuspend[slot] = flags; else _slotSuspend.Remove(slot); }
    }

    internal static Rigidbody Body(FusionPlayer fp)
    {
        if (fp == null) return null;
        var rb = fp.rigidbody;
        if (rb == null && fp.vehicleBase != null) rb = fp.vehicleBase.cachedRigidbody;
        return rb;
    }

    static readonly Dictionary<IntPtr, Vector3> _boxes = new();
    public static readonly Dictionary<string, string> MeasuredBoxes = new();

    /// <summary>Half extents (x right, y up, z forward) around the rigidbody position.</summary>
    internal static Vector3 BoxOf(FusionPlayer car)
    {
        if (ContactBoxOverride > 0) return Vector3.one * ContactBoxOverride;
        var fallback = new Vector3(Plugin.CarHalfWidth.Value, Plugin.CarHalfHeight.Value, Plugin.CarHalfLength.Value);
        if (!Plugin.ContactBoxFromColliders.Value || car == null) return fallback;
        if (_boxes.TryGetValue(car.Pointer, out var b)) return b;
        b = fallback;
        try
        {
            var rb = Body(car);
            if (rb != null)
            {
                var inv = Quaternion.Inverse(rb.rotation); var o = rb.position;
                float mx = 0, my = 0, mz = 0; int n = 0;
                void Corner(Transform t, Vector3 lp) { var d = inv * (t.TransformPoint(lp) - o); mx = Mathf.Max(mx, Mathf.Abs(d.x)); my = Mathf.Max(my, Mathf.Abs(d.y)); mz = Mathf.Max(mz, Mathf.Abs(d.z)); }
                void Box(Transform t, Vector3 c, Vector3 size)
                {
                    var h = size * 0.5f;
                    for (int i = 0; i < 8; i++) Corner(t, c + new Vector3((i & 1) != 0 ? h.x : -h.x, (i & 2) != 0 ? h.y : -h.y, (i & 4) != 0 ? h.z : -h.z));
                }
                foreach (var col in car.GetComponentsInChildren<Collider>())
                {
                    if (col == null || col.isTrigger || !col.enabled || col.attachedRigidbody != rb) continue;
                    var bc = col.TryCast<BoxCollider>(); if (bc != null) { Box(col.transform, bc.center, bc.size); n++; continue; }
                    var mc = col.TryCast<MeshCollider>(); if (mc != null && mc.sharedMesh != null) { var mb = mc.sharedMesh.bounds; Box(col.transform, mb.center, mb.size); n++; continue; }
                    var sc = col.TryCast<SphereCollider>(); if (sc != null) { Box(col.transform, sc.center, Vector3.one * sc.radius * 2); n++; continue; }
                    var cc = col.TryCast<CapsuleCollider>();
                    if (cc != null)
                    {
                        var size = Vector3.one * cc.radius * 2; size[Mathf.Clamp(cc.direction, 0, 2)] = Mathf.Max(cc.height, cc.radius * 2);
                        Box(col.transform, cc.center, size); n++;
                    }
                }
                if (n > 0 && mx is > 0.3f and < 5f && my is > 0.2f and < 5f && mz is > 0.5f and < 8f)
                {
                    b = new Vector3(mx, my, mz);
                    MeasuredBoxes[car.name] = $"{mx:0.00} x {mz:0.00} x {my:0.00} ({n})";
                    Plugin.L.LogInfo($"[CarSync] contact box of {car.name}: half width {mx:0.00} m, half length {mz:0.00} m, half height {my:0.00} m from {n} colliders (cfg fallback {fallback.x:0.0}/{fallback.z:0.0}/{fallback.y:0.0})");
                }
                else Plugin.L.LogInfo($"[CarSync] contact box of {car.name}: colliders unusable ({n}, {mx:0.00}/{mz:0.00}/{my:0.00}); cfg values used");
            }
        }
        catch (Exception e) { Plugin.L.LogWarning($"[CarSync] contact box measurement failed: {e.Message}"); }
        _boxes[car.Pointer] = b;
        return b;
    }
}

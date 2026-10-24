using System.Collections.Concurrent;
using System.Net;
using UnityEngine;

namespace OthLan;

/// <summary>
/// 0.2.0 T6.4: host teleports a client's proxy onto the latest OTC3 pose when that client asks (OTRS).
/// The client also calls <see cref="CarStreamSender.BumpEpoch"/>; this does not touch CarSync.
/// </summary>
internal static class SessionResync
{
    static readonly ConcurrentQueue<int> _q = new();
    public static int Teleports;

    public static void Accept(byte[] data, IPEndPoint from)
    {
        if (!CoopSessionLogic.TryDecodeResync(data, out var slot)) return;
        var known = HostPings.EndpointOf(slot);
        if (known == null || from == null || known.Port != from.Port || !known.Address.Equals(from.Address)) return;
        _q.Enqueue(slot);
    }

    public static void Tick()
    {
        if (LanState.Active != LanRole.Host) { Clear(); return; }
        var n = 0;
        while (n++ < CoopSessionLogic.MaxRows && _q.TryDequeue(out var slot)) Teleport(slot);
    }

    public static void Clear()
    {
        while (_q.TryDequeue(out _)) { }
    }

    static void Teleport(int slot)
    {
        if (!CarRegistry.TryGetBySlot(slot, out var car) || car.IsOwn || car.Nrb == null) return;
        var sample = CarRelay.PlaybackOf(slot).SampleAt(NetClock.Now, 0);
        if (!sample.Ok)
        {
            Plugin.L.LogInfo($"[Session] resync slot {slot}: no stream pose yet");
            return;
        }
        var pos = new Vector3(sample.Px, sample.Py, sample.Pz);
        var rot = new Quaternion(sample.Qx, sample.Qy, sample.Qz, sample.Qw);
        if (rot.x == 0f && rot.y == 0f && rot.z == 0f && rot.w == 0f) rot = Quaternion.identity;
        try
        {
            car.Nrb.Teleport(new Il2CppSystem.Nullable<Vector3>(pos), new Il2CppSystem.Nullable<Quaternion>(rot));
            Teleports++;
            Plugin.L.LogInfo($"[Session] resync slot {slot}: teleported proxy to client pose");
        }
        catch (System.Exception e) { Plugin.L.LogWarning($"[Session] resync slot {slot}: {e.Message}"); }
    }
}

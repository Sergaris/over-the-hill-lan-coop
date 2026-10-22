using System;
using System.Collections.Generic;
using System.Linq;

namespace OthLan;

/// <summary>
/// 0.1.6 host-assigned player slots (pure, unit-tested in tests/LogicTests).
/// Before connecting, the client asks the host over UDP Port+1 ("OTHLAN?SLOT1 id=... hint=..."); the host reserves a
/// free slot for that client id. The client then claims it in the Fusion connect uniqueId (byte0 = slot, byte1 = magic,
/// bytes 2..5 = <see cref="Tag"/> of its id); the host's connection-request hook admits it only if the slot is free or
/// reserved for the same tag. A reservation that never connects expires; a slot whose player left is released.
/// The same client id gets the same slot back on reconnect (if nobody else took it meanwhile).
/// Clients that got no answer fall back to the 0.1.5 IP-derived pick and are admitted into free, unreserved slots.
/// </summary>
public sealed class SlotAllocator
{
    sealed class Entry { public string Id; public uint Tag; public double Expires; public bool Connected; }
    readonly object _lock = new();
    readonly Dictionary<int, Entry> _slots = new();
    readonly Dictionary<string, int> _last = new();
    public int Players { get; private set; }
    public int HostIndex { get; }
    public double ReserveSeconds { get; set; } = 45;

    public SlotAllocator(int players, int hostIndex) { Players = players; HostIndex = hostIndex; }

    public static uint Tag(string clientId)
    {
        uint h = 2166136261;
        foreach (var c in clientId ?? "") { h ^= c; h *= 16777619; }
        return h == 0 ? 1u : h;
    }

    /// <summary>New session (host started): forget reservations, keep the id→slot memory only if the size matches.</summary>
    public void Reset(int players)
    {
        lock (_lock) { if (players != Players) _last.Clear(); Players = players; _slots.Clear(); }
    }

    IEnumerable<int> Candidates() => Enumerable.Range(0, Players).Where(i => i != HostIndex);

    void Expire(double now)
    {
        foreach (var k in _slots.Where(kv => !kv.Value.Connected && kv.Value.Expires < now).Select(kv => kv.Key).ToList()) _slots.Remove(k);
    }

    /// <summary>Reserve (or return the existing) slot for this client id. -1 = no free slot.</summary>
    public int Request(string clientId, int hint, double now)
    {
        if (string.IsNullOrEmpty(clientId)) return -1;
        lock (_lock)
        {
            Expire(now);
            foreach (var kv in _slots)
                if (kv.Value.Id == clientId)
                {
                    if (!kv.Value.Connected) kv.Value.Expires = now + ReserveSeconds;
                    return kv.Key;
                }
            var free = Candidates().Where(i => !_slots.ContainsKey(i)).ToList();
            if (free.Count == 0) return -1;
            int pick = _last.TryGetValue(clientId, out var l) && free.Contains(l) ? l : free.Contains(hint) ? hint : free[0];
            _slots[pick] = new Entry { Id = clientId, Tag = Tag(clientId), Expires = now + ReserveSeconds };
            _last[clientId] = pick;
            return pick;
        }
    }

    /// <summary>Fusion connection request for <paramref name="slot"/> carrying <paramref name="tag"/> (0 = an old client
    /// without a tag). True = let it in.</summary>
    public bool Admit(int slot, uint tag, double now, out string why)
    {
        why = null;
        lock (_lock)
        {
            Expire(now);
            if (slot < 0 || slot >= Players || slot == HostIndex) { why = $"bad slot {slot}"; return false; }
            if (_slots.TryGetValue(slot, out var e))
            {
                if (tag != 0 && e.Tag == tag) { if (!e.Connected) e.Expires = now + ReserveSeconds; return true; }
                why = e.Connected ? $"slot {slot} is in use" : $"slot {slot} is reserved for another player";
                return false;
            }
            // No reservation: a fallback (IP-derived) client. Admit into the free slot and hold it like a reservation.
            _slots[slot] = new Entry { Id = "tag:" + tag.ToString("x8"), Tag = tag, Expires = now + ReserveSeconds };
            return true;
        }
    }

    /// <summary>Host main thread, ~1/s: a slot whose player is valid is Connected; a Connected slot whose player is
    /// gone is released (the id keeps its preference for a reconnect).</summary>
    public List<int> Sync(Func<int, bool> playerValid, double now)
    {
        var released = new List<int>();
        lock (_lock)
        {
            foreach (var kv in _slots.ToList())
            {
                var valid = playerValid(kv.Key);
                if (valid) kv.Value.Connected = true;
                else if (kv.Value.Connected) { _slots.Remove(kv.Key); released.Add(kv.Key); }
            }
            Expire(now);
        }
        return released;
    }

    public string Describe()
    {
        lock (_lock) return string.Join(" ", _slots.OrderBy(k => k.Key).Select(kv => $"{kv.Key}:{(kv.Value.Connected ? "in" : "res")}:{kv.Value.Id}"));
    }

    public int Count { get { lock (_lock) return _slots.Count; } }
}

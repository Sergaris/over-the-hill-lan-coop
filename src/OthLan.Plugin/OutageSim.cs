using System;
using System.Runtime.InteropServices;

namespace OthLan;

#if SELFTEST
/// <summary>
/// Self-test only (--othlan-outage): a FULL network outage of this process without admin rights or drivers. While
/// <see cref="Active"/>, every Fusion datagram (NetSocketNative.Send/Receive detours) and every OTH-LAN side-channel
/// datagram (ClientLink, HostInfo) is dropped in both directions, like pulling the cable. Not compiled into public builds.
/// </summary>
internal static unsafe class OutageSim
{
    public static volatile bool Active;
    /// <summary>Partial loss (--othlan-loss): drop this share of Fusion datagrams per direction (0..1).</summary>
    public static volatile float Loss;
    public static long LostOut, LostIn;
    static readonly Random Rng = new();
    static bool Roll() { var l = Loss; if (l <= 0) return false; lock (Rng) return Rng.NextDouble() < l; }
    public static long DroppedOut, DroppedIn;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SendFn(IntPtr self, IntPtr sock, IntPtr addr, IntPtr buf, int len, byte reliable, IntPtr mi);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int RecvFn(IntPtr self, IntPtr sock, IntPtr addr, IntPtr buf, int len, IntPtr mi);
    static SendFn _sOrig, _sHook; static RecvFn _rOrig, _rHook;
    public static bool Installed;

    public static void Install()
    {
        if (Installed) return;
        var t = typeof(Fusion.Sockets.NetSocketNative);
        var ps = OptionalHooks.Resolve(t, "NativeMethodInfoPtr_Send_Public_Virtual_Final_New_Int32_NetSocket_ptr_NetAddress_ptr_Byte_Int32_Boolean_0", "Fusion.Sockets.NetSocketNative::Send");
        var pr = OptionalHooks.Resolve(t, "NativeMethodInfoPtr_Receive_Public_Virtual_Final_New_Int32_NetSocket_ptr_NetAddress_ptr_Byte_Int32_0", "Fusion.Sockets.NetSocketNative::Receive");
        _sHook = SendD; _rHook = RecvD;
        Installed = OptionalHooks.Apply(ps, _sHook, out _sOrig, "Fusion.Sockets.NetSocketNative::Send") &&
                    OptionalHooks.Apply(pr, _rHook, out _rOrig, "Fusion.Sockets.NetSocketNative::Receive");
        Plugin.L.LogWarning($"[Outage] self-test outage emulation {(Installed ? "installed" : "FAILED")}");
    }

    // ---- 0.1.8 Fusion netem (--othlan-fnetem): delay + jitter per direction, and periodic stalls of the host-to-client
    // direction (packets held, then delivered in a burst): the reporter's 2026-10-22 log pattern (snapshot stalls >= 0.7 s
    // every 1-2 s at ~200 ms ping while the small echo pings looked fine). Order is kept (no reordering).
    public static volatile int DelayMs, JitterMs, StallEveryMs, StallMs;
    public static long Delayed, Stalled;
    static bool NetemOn => DelayMs > 0 || JitterMs > 0 || StallMs > 0 && StallEveryMs > 0;
    const int AddrSize = 0x18;   // sizeof(NetAddress), manifest offsets
    sealed class Pkt { public IntPtr Self, Sock; public byte[] Addr, Data; public byte Reliable; public double Due; }
    static readonly System.Collections.Generic.Queue<Pkt> _qOut = new(), _qIn = new();
    static readonly System.Diagnostics.Stopwatch Sw = System.Diagnostics.Stopwatch.StartNew();
    static double _lastOutDue, _lastInDue; static IntPtr _sMi;
    static double Jit() { var j = JitterMs; if (j <= 0) return 0; lock (Rng) return Rng.NextDouble() * j; }
    static double StallEnd(double now)
    {
        if (StallMs <= 0 || StallEveryMs <= 0) return 0;
        var ph = now % StallEveryMs;
        return ph < StallMs ? now - ph + StallMs : 0;
    }
    static void FlushOut()
    {
        var now = Sw.Elapsed.TotalMilliseconds;
        while (_qOut.Count > 0 && _qOut.Peek().Due <= now)
        {
            var p = _qOut.Dequeue();
            fixed (byte* a = p.Addr) fixed (byte* d = p.Data) _sOrig(p.Self, p.Sock, (IntPtr)a, (IntPtr)d, p.Data.Length, p.Reliable, _sMi);
        }
    }
    public static void Pump() { if (!Installed) return; try { FlushOut(); } catch { } }

    static int SendD(IntPtr self, IntPtr sock, IntPtr addr, IntPtr buf, int len, byte reliable, IntPtr mi)
    {
        if (Active) { DroppedOut++; return len; }
        if (Roll()) { LostOut++; return len; }
        if (NetemOn || _qOut.Count > 0)
        {
            _sMi = mi;
            var now = Sw.Elapsed.TotalMilliseconds;
            var p = new Pkt { Self = self, Sock = sock, Addr = new byte[AddrSize], Data = new byte[len], Reliable = reliable };
            Marshal.Copy(addr, p.Addr, 0, AddrSize); Marshal.Copy(buf, p.Data, 0, len);
            p.Due = Math.Max(_lastOutDue, now + DelayMs + Jit()); _lastOutDue = p.Due;
            _qOut.Enqueue(p); Delayed++;
            FlushOut();
            return len;
        }
        return _sOrig(self, sock, addr, buf, len, reliable, mi);
    }

    static int RecvD(IntPtr self, IntPtr sock, IntPtr addr, IntPtr buf, int len, IntPtr mi)
    {
        if (!Active && (NetemOn || _qIn.Count > 0))
        {
            FlushOut();
            var now = Sw.Elapsed.TotalMilliseconds;
            int last = 0;
            for (int i = 0; i < 512; i++)
            {
                var m = _rOrig(self, sock, addr, buf, len, mi);
                if (m <= 0) { last = m; break; }
                if (Roll()) { LostIn++; continue; }
                var p = new Pkt { Addr = new byte[AddrSize], Data = new byte[m] };
                Marshal.Copy(addr, p.Addr, 0, AddrSize); Marshal.Copy(buf, p.Data, 0, m);
                var due = Math.Max(_lastInDue, now + DelayMs + Jit());
                var se = StallEnd(now); if (se > due) { due = se; Stalled++; }
                p.Due = due; _lastInDue = due;
                _qIn.Enqueue(p); Delayed++;
            }
            if (_qIn.Count > 0 && _qIn.Peek().Due <= now)
            {
                var p = _qIn.Dequeue();
                var n2 = Math.Min(len, p.Data.Length);
                Marshal.Copy(p.Addr, 0, addr, AddrSize); Marshal.Copy(p.Data, 0, buf, n2);
                return n2;
            }
            return last;
        }
        var n = _rOrig(self, sock, addr, buf, len, mi);
        if (!Active)
        {
            for (int i = 0; i < 64 && n > 0 && Roll(); i++) { LostIn++; n = _rOrig(self, sock, addr, buf, len, mi); }
            return n;
        }
        for (int i = 0; i < 512 && n > 0; i++) { DroppedIn++; n = _rOrig(self, sock, addr, buf, len, mi); }   // drain: the cable is out
        return n > 0 ? 0 : n;
    }
}
#else
internal static class OutageSim { public const bool Active = false; public const float Loss = 0; public static void Pump() { } }
#endif

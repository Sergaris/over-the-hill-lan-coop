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

    static int SendD(IntPtr self, IntPtr sock, IntPtr addr, IntPtr buf, int len, byte reliable, IntPtr mi)
    {
        if (Active) { DroppedOut++; return len; }
        if (Roll()) { LostOut++; return len; }
        return _sOrig(self, sock, addr, buf, len, reliable, mi);
    }

    static int RecvD(IntPtr self, IntPtr sock, IntPtr addr, IntPtr buf, int len, IntPtr mi)
    {
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
internal static class OutageSim { public const bool Active = false; public const float Loss = 0; }
#endif

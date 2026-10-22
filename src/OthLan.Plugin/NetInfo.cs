using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace OthLan;

internal static class NetInfo
{
    public static List<string> LocalIPv4()
    {
        var r = new List<string>();
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var a in n.GetIPProperties().UnicastAddresses)
                if (a.Address.AddressFamily == AddressFamily.InterNetwork) r.Add(a.Address + " (" + n.Description + ")");
        }
        return r;
    }

    /// <summary>Address shown in the UI (F7, Local Co-op footer, status). Self-test demo mode shows a placeholder so
    /// screenshots carry no real address; networking always uses <see cref="RadminIp"/>.</summary>
    public static string DisplayIp() => DemoIp ?? RadminIp();
    public static string DemoIp;
    /// <summary>Demo mode: this PC's real Radmin address inside a shown text (e.g. "connected to ...") becomes a placeholder.</summary>
    public static string Mask(string s)
    {
        if (DemoIp == null || string.IsNullOrEmpty(s)) return s;
        _real ??= RadminIp() ?? "";
        return _real.Length > 0 ? s.Replace(_real, "26.10.20.30") : s;
    }
    static string _real;

    /// <summary>Radmin VPN hands out 26.0.0.0/8 addresses on "Famatech Radmin VPN Ethernet Adapter".</summary>
    public static string RadminIp()
    {
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var a in n.GetIPProperties().UnicastAddresses)
                if (a.Address.AddressFamily == AddressFamily.InterNetwork &&
                    (a.Address.GetAddressBytes()[0] == 26 || n.Description.Contains("Radmin")))
                    return a.Address.ToString();
        }
        return null;
    }
}

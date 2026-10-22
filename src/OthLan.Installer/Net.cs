using System.Collections.Generic;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace OthLan.Installer;

static class Net
{
    public static string RadminIp(out string adapter)
    {
        adapter = null;
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var a in n.GetIPProperties().UnicastAddresses)
                if (a.Address.AddressFamily == AddressFamily.InterNetwork &&
                    (a.Address.GetAddressBytes()[0] == 26 || n.Description.Contains("Radmin")))
                { adapter = n.Description; return a.Address.ToString(); }
        }
        return null;
    }

    public static (int code, string output) Run(string exe, string args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(exe, args)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, o.Trim());
        }
        catch (System.Exception e) { return (-1, e.Message); }
    }

    public static string RuleName(int port) => $"OTH-LAN UDP {port}";

    /// <summary>Port = Fusion game traffic, Port+1 = OTH-LAN host-info query (0.1.4+). Same rule name as before so
    /// upgrades replace the 0.1.x rule.</summary>
    public static bool AddFirewall(int port, string gameExe)
    {
        RemoveFirewall(port);
        var (c, o) = Run("netsh", $"advfirewall firewall add rule name=\"{RuleName(port)}\" dir=in action=allow protocol=UDP localport={port},{port + 1} program=\"{gameExe}\" profile=any enable=yes");
        Log.Info($"netsh add rule -> {c}: {o}");
        return c == 0;
    }

    public static void RemoveFirewall(int port)
    {
        var (c, o) = Run("netsh", $"advfirewall firewall delete rule name=\"{RuleName(port)}\"");
        Log.Info($"netsh delete rule -> {c}: {o}");
    }
}

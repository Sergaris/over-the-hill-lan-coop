using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OthLan.Installer;

static class Program
{
    static bool IsAdmin()
    {
        try { return new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator); }
        catch { return false; }
    }

    /// <summary>Same exe, same arguments, elevated (UAC prompt). 1223 = the user said no.</summary>
    static int Elevate(string[] args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, string.Join(" ", args.Select(Quote))) { UseShellExecute = true, Verb = "runas" };
            using var p = System.Diagnostics.Process.Start(psi);
            p!.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { return 1223; }
    }
    static string Quote(string a) => a.Length > 0 && a.IndexOfAny(new[] { ' ', '"', '\t' }) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + (a.EndsWith("\\") ? "\\" : "") + "\"";

    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    [STAThread]
    static int Main(string[] args)
    {
        UiText.FromArgs(args);
        // 0.1.8: "update" is the in-game updater's step: no admin, no window (see Updater.cs in the plugin).
        if (args.Length > 0 && args[0] == "update") return Update.Run(args);
        if (!IsAdmin()) return Elevate(args);
        // CLI if a command is given (install/uninstall/verify); otherwise the GUI (Russian or English, see UiText).
        if (args.Length > 0 && args[0] is "install" or "uninstall" or "verify")
        {
            try { AttachConsole(-1); } catch { }
            if (!args.Contains("--yes")) args = args.Append("--yes").ToArray(); // no "press Enter" in a GUI-subsystem exe
            return Cli.Run(args);
        }
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => MainForm.Crash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => MainForm.Crash(e.ExceptionObject as Exception);
        var form = new MainForm(args);
        Application.Run(form);
        return form.ExitCode;
    }
}

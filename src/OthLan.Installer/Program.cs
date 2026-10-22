using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OthLan.Installer;

static class Program
{
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    [STAThread]
    static int Main(string[] args)
    {
        UiText.FromArgs(args);
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

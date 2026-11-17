using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace HybridLauncher.Starter;

/// <summary>
/// Starts the real app in the "app" folder beside this exe with the same arguments, then exits. Shortcuts, Start with
/// Windows and updates all point at the real exe; this one is only what a person double-clicks in the folder.
/// </summary>
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var here = AppDomain.CurrentDomain.BaseDirectory;
        var exe = Path.Combine(here, "app", "HybridLauncher.exe");
        if (!File.Exists(exe))
        {
            MessageBox.Show($"The app folder is missing or incomplete:\n{exe}\n\nExtract the whole download again.", "Hybrid Launcher",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        var psi = new ProcessStartInfo(exe)
        {
            Arguments = string.Join(" ", args.Select(Quote)),
            WorkingDirectory = Path.GetDirectoryName(exe),
            UseShellExecute = false,
        };
        Process? app;
        try { app = Process.Start(psi); }
        catch (Exception ex)
        {
            MessageBox.Show($"Hybrid Launcher could not be started:\n{ex.Message}\n\n{exe}", "Hybrid Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        if (app is null) return 0;
        using (app)
        {
            // 2.3-rc.2: an app that ends by itself while starting is said, not a double-click that seems to do nothing. Code 0 is
            // a normal end (another copy was asked to show itself), 3 is a failure the app has already explained.
            if (!app.WaitForExit(20000)) return 0;
            int code = app.ExitCode;
            if (code is 0 or 3) return 0;
            var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HybridLauncher", "logs");
            MessageBox.Show(
                $"Hybrid Launcher closed while starting (code 0x{code:X8}).\n\n" +
                "Try starting it again. If it keeps happening, restart Windows or extract the whole download again, and send the newest log with a bug report.\n\n" +
                $"Logs: {logs}",
                "Hybrid Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return code;
        }
    }

    /// <summary>One argument as Windows parses it back: quoted when it has spaces or quotes, inner quotes and the backslashes before them escaped.</summary>
    static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0) return arg;
        var sb = new System.Text.StringBuilder("\"");
        int slashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') sb.Append('\\', slashes * 2 + 1);
            else sb.Append('\\', slashes);
            slashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', slashes * 2).Append('"');
        return sb.ToString();
    }
}

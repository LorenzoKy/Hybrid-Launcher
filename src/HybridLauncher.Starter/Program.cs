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
        Process.Start(psi)?.Dispose();
        return 0;
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

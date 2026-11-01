using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace HybridLauncher.Core.Opti;

public sealed class BatResult
{
    public bool Completed { get; init; }
    public string Transcript { get; init; } = "";
    public List<string> Answers { get; init; } = [];
    public string? Failure { get; init; }
}

/// <summary>
/// Runs OptiScaler's setup_windows.bat without a window and answers its prompts from <see cref="OptiSetupOptions"/>,
/// like an "expect" script: each answer is written only once the bat is waiting on that question,
/// so the questions the bat skips (Nvidia path, no old files, ...) never desync the answers.
/// </summary>
public static partial class BatDriver
{
    const int MaxAnswers = 25;
    static readonly TimeSpan Overall = TimeSpan.FromMinutes(6);
    static readonly TimeSpan Idle = TimeSpan.FromSeconds(120);

    [GeneratedRegex(@"\.\s\.\s\.\s*$")]
    private static partial Regex PausePrompt(); // "Press any key to continue . . ." in any language

    [DllImport("kernel32.dll")] static extern uint GetOEMCP();

    /// <summary>
    /// The encoding cmd writes in when its output is a pipe: the system's OEM code page (GetOEMCP), not the user's regional
    /// format. Formats Windows has only in Unicode (Hindi, Georgian, Armenian, ...) have no OEM code page and report 1
    /// (CP_OEMCP), which .NET cannot open: issue #1. UTF-8 for any code page .NET does not know.
    /// </summary>
    public static Encoding OutputEncoding(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        // 0..3 are Windows' "use the default" placeholders (CP_ACP, CP_OEMCP, CP_MACCP, CP_THREAD_ACP), not code pages.
        if (codePage > 3)
        {
            try { return Encoding.GetEncoding(codePage); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
        }
        Log.Warn($"Setup bat: code page {codePage} is not supported, reading its output as UTF-8");
        return new UTF8Encoding(false);
    }

    public static async Task<BatResult> RunAsync(string batFile, OptiSetupOptions options, Action<string>? onLine, CancellationToken ct)
    {
        var oem = OutputEncoding((int)GetOEMCP());

        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            WorkingDirectory = Path.GetDirectoryName(batFile)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = oem,
            StandardErrorEncoding = oem,
        };
        // ".\" so it runs even when NoDefaultCurrentDirectoryInExePath stops cmd from searching the current folder.
        psi.Arguments = $"/d /c .\\{Path.GetFileName(batFile)}";
        psi.Environment.Remove("NoDefaultCurrentDirectoryInExePath");

        var transcript = new StringBuilder();
        var answers = new List<string>();
        var pending = new StringBuilder();
        string? failure = null;

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        Log.Info($"Running {batFile} (pid {proc.Id})");
        var stdin = proc.StandardInput;
        stdin.AutoFlush = true;

        var stderrTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync(CancellationToken.None)) is not null)
            {
                lock (transcript) transcript.AppendLine("[stderr] " + line);
            }
        }, CancellationToken.None);

        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(Overall);

        var buffer = new char[4096];
        var lineBuffer = new StringBuilder();
        try
        {
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
                idle.CancelAfter(Idle);
                int read;
                try
                {
                    read = await proc.StandardOutput.ReadAsync(buffer.AsMemory(), idle.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    failure = overall.IsCancellationRequested ? L.T("The setup script took too long.") : L.T("The setup script stopped responding.");
                    break;
                }
                if (read == 0) break; // bat finished

                var chunk = new string(buffer, 0, read);
                lock (transcript) transcript.Append(chunk);
                pending.Append(chunk);
                EmitLines(chunk, lineBuffer, onLine);

                var text = pending.ToString();
                var answer = Decide(text, options, out var unknown);
                if (unknown)
                {
                    failure = L.T("The setup script asked a question this version of Hybrid Launcher does not know:") + "\n" + LastLines(text, 6);
                    break;
                }
                if (answer is null) continue;

                if (answers.Count >= MaxAnswers)
                {
                    failure = L.T("The setup script kept asking the same questions (rejected answer?).");
                    break;
                }
                answers.Add(answer);
                Log.Info($"bat> answering '{answer}' to: {LastLines(text, 1)}");
                // The prompt has no newline yet: show it together with the answer.
                onLine?.Invoke($"{lineBuffer.ToString().Trim()}  → {(answer.Length == 0 ? "(Enter)" : answer)}");
                lineBuffer.Clear();
                lock (transcript) transcript.AppendLine($"<<{answer}>>");
                pending.Clear();
                try { await stdin.WriteLineAsync(answer); }
                catch (IOException) { /* bat already exiting */ }
            }
        }
        finally
        {
            if (!proc.HasExited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            }
        }

        try { await proc.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        try { await stderrTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        ct.ThrowIfCancellationRequested();

        string all;
        lock (transcript) all = transcript.ToString();
        File.WriteAllText(Path.Combine(AppInfo.LogsDir, $"setup-bat-{DateTime.Now:yyyyMMdd-HHmmss}.log"), all);

        return new BatResult { Completed = failure is null, Transcript = all, Answers = answers, Failure = failure };
    }

    /// <summary>Returns the answer for the prompt at the end of <paramref name="text"/>, or null if the bat is not waiting yet.</summary>
    internal static string? Decide(string text, OptiSetupOptions o, out bool unknown)
    {
        unknown = false;
        var tail = text.TrimEnd(' ', '\t');
        string yes(bool b) => b ? "1" : "2";

        if (tail.EndsWith("Enter 1-8 (or press Enter for default):", StringComparison.Ordinal))
        {
            int idx = Array.FindIndex(OptiSetupOptions.DllNames, n => n.Equals(o.DllName, StringComparison.OrdinalIgnoreCase));
            return (idx < 0 ? 1 : idx + 1).ToString(CultureInfo.InvariantCulture);
        }
        if (tail.EndsWith("(Detected Nvidia):", StringComparison.Ordinal))
            return (o.Nvidia ?? true) ? "2" : "1";
        if (tail.EndsWith("(Detected AMD/Intel):", StringComparison.Ordinal))
            return (o.Nvidia ?? false) ? "2" : "1";
        if (tail.EndsWith("(or press Enter for Yes):", StringComparison.Ordinal))
            return yes(o.Spoofing);
        if (PausePrompt().IsMatch(tail))
            return "";

        if (!tail.EndsWith("Waiting -", StringComparison.Ordinal))
            return null;

        // "Waiting - " follows several different questions: look at what was printed since the last answer.
        if (text.Contains("Do you want to delete these files?", StringComparison.Ordinal)) return yes(o.DeleteOldFiles);
        if (text.Contains("Continue installation to current folder?", StringComparison.Ordinal)) return "1";
        if (text.Contains("Do you want to overwrite", StringComparison.Ordinal)) return "1";
        if (text.Contains("re-download a possibly newer version", StringComparison.Ordinal)) return yes(o.OptiPatcher);
        if (text.Contains("Download OptiPatcher.asi?", StringComparison.Ordinal)) return yes(o.OptiPatcher);

        unknown = true;
        return null;
    }

    static void EmitLines(string chunk, StringBuilder lineBuffer, Action<string>? onLine)
    {
        foreach (var c in chunk)
        {
            if (c == '\n')
            {
                var line = lineBuffer.ToString().TrimEnd('\r');
                lineBuffer.Clear();
                if (onLine is not null && !IsNoise(line))
                    onLine(line.Trim());
            }
            else if (c != '\f')
            {
                lineBuffer.Append(c);
            }
        }
    }

    // Blank lines, the ASCII-art banner and the echoed "C:\...>REM" line before @echo off.
    static bool IsNoise(string line)
    {
        var t = line.Trim();
        if (t.Length < 3 || t.Contains(">REM ", StringComparison.Ordinal)) return true;
        int letters = t.Count(char.IsLetterOrDigit);
        return letters * 3 < t.Length;
    }

    static string LastLines(string text, int n) =>
        string.Join("\n", text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).TakeLast(n));
}

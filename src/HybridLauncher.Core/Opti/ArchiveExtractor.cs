using System.Diagnostics;
using SharpCompress.Archives;

namespace HybridLauncher.Core.Opti;

public static class ArchiveExtractor
{
    /// <summary>Extracts a 7z/zip/rar archive into an empty <paramref name="destination"/>. Uses SharpCompress,
    /// falling back to Windows' bsdtar (tar.exe) when SharpCompress cannot decode the archive.</summary>
    public static async Task ExtractAsync(string archive, string destination, CancellationToken ct)
    {
        ResetDir(destination);
        try
        {
            await Task.Run(() => ExtractManaged(archive, destination, ct), ct);
            return;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"SharpCompress could not extract {archive}: {ex.Message}. Trying tar.exe");
        }

        ResetDir(destination);
        await ExtractWithTarAsync(archive, destination, ct);
        EnsureInside(destination);
    }

    static void ExtractManaged(string archive, string destination, CancellationToken ct)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var a = ArchiveFactory.Open(archive, null);
        using var reader = a.ExtractAllEntries();
        while (reader.MoveToNextEntry())
        {
            ct.ThrowIfCancellationRequested();
            var entry = reader.Entry;
            if (entry.Key is null) continue;
            var full = Path.GetFullPath(Path.Combine(destination, entry.Key));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Archive entry escapes the destination: {entry.Key}");

            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(full);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using var fs = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None);
            reader.WriteEntryTo(fs);
        }
    }

    static async Task ExtractWithTarAsync(string archive, string destination, CancellationToken ct)
    {
        var tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
        if (!File.Exists(tar)) throw new InvalidOperationException(L.T("Cannot extract the archive: tar.exe is missing and the built-in extractor failed."));
        var psi = new ProcessStartInfo(tar)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("-xf");
        psi.ArgumentList.Add(archive);
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(destination);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException($"tar.exe failed ({p.ExitCode}): {await err}");
    }

    static void EnsureInside(string destination)
    {
        // bsdtar refuses absolute paths and ".." by default; this is a second check that nothing is a link out.
        foreach (var f in Directory.EnumerateFileSystemEntries(destination, "*", SearchOption.AllDirectories))
            if ((File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Archive contains a link: {f}");
    }

    public static void ResetDir(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
    }
}

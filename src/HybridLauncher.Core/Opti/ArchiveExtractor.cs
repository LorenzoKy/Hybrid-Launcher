using System.Diagnostics;
using SharpCompress.Archives;

namespace HybridLauncher.Core.Opti;

public static class ArchiveExtractor
{
    /// <summary>Extracts a 7z/zip/rar archive (or a compressed tar) into an empty <paramref name="destination"/>. Uses SharpCompress,
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

    public static void ExtractManaged(string archive, string destination, CancellationToken ct)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        // A compressed tar (DXVK's .tar.gz, VKD3D-Proton's .tar.zst) opened as an archive is one compressed file holding the tar:
        // read as a stream instead, the reader looks inside.
        if (IsCompressedTar(archive))
        {
            using var stream = File.OpenRead(archive);
            using var tar = SharpCompress.Readers.ReaderFactory.Open(stream);
            int files = 0;
            while (tar.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                if (Place(root, destination, tar.Entry.Key, tar.Entry.IsDirectory) is { } file)
                {
                    using var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None);
                    tar.WriteEntryTo(fs);
                    files++;
                }
            }
            if (files == 0) throw new InvalidDataException($"No files read from {Path.GetFileName(archive)}");
            return;
        }
        using var a = ArchiveFactory.Open(archive, null);
        int written = 0;
        // Solid archives and 7z are read front to back (ExtractAllEntries); zip and the rest entry by entry. ExtractAllEntries
        // on an ordinary zip throws, which sent every zip release to tar.exe.
        if (a.IsSolid || a.Type == SharpCompress.Common.ArchiveType.SevenZip)
        {
            using var reader = a.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                if (Place(root, destination, reader.Entry.Key, reader.Entry.IsDirectory) is { } file)
                {
                    using var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None);
                    reader.WriteEntryTo(fs);
                    written++;
                }
            }
        }
        else
            foreach (var entry in a.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (Place(root, destination, entry.Key, entry.IsDirectory) is { } file)
                {
                    using var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None);
                    using var es = entry.OpenEntryStream();
                    es.CopyTo(fs);
                    written++;
                }
            }
        // An archive that gave no file (damaged, or one the reader misread as empty) goes to tar.exe like a failed read.
        if (written == 0) throw new InvalidDataException($"No files read from {Path.GetFileName(archive)}");
    }

    static bool IsCompressedTar(string archive) =>
        new[] { ".tar.gz", ".tgz", ".tar.zst", ".tar.xz", ".tar.bz2" }.Any(e => archive.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>Where an entry goes (its folders made), or null for a folder entry; throws for one that would land outside.</summary>
    static string? Place(string root, string destination, string? key, bool isDirectory)
    {
        if (key is null) return null;
        var full = Path.GetFullPath(Path.Combine(destination, key));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Archive entry escapes the destination: {key}");
        if (isDirectory)
        {
            Directory.CreateDirectory(full);
            return null;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return full;
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

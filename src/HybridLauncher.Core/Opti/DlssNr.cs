using System.Diagnostics;

namespace HybridLauncher.Core.Opti;

/// <summary>
/// The DLSS 5 neural rendering model file (nvngx_dlssnr.dll). Forks built on Dagherbou's DLSSNR work (janblade's F5 Multipass,
/// y4my4my4m's MFG, ...) need it next to OptiScaler but cannot ship it: the user gets it from NVIDIA. Hybrid Launcher keeps one
/// copy (OptiScaler tab → Forks/Unofficial Builds or Custom build → DLSS NR model file) and puts it in every install that needs
/// it, as one of the install's own files, so uninstalling removes it and whatever was there before comes back.
/// </summary>
public static class DlssNr
{
    public const string FileName = "nvngx_dlssnr.dll";

    /// <summary>The stored copy (in Hybrid Launcher's data folder); it may not exist.</summary>
    public static string StoredPath => Path.Combine(AppInfo.Ensure(Path.Combine(AppInfo.DataRoot, "DlssNr")), FileName);

    /// <summary>The stored copy when there is one and it is turned on, else null.</summary>
    public static string? Active(AppSettings s) => s.UseDlssNr && File.Exists(StoredPath) ? StoredPath : null;

    /// <summary>A game gets the file: its own choice, or else the global switch.</summary>
    public static bool OnFor(GameSettings g, AppSettings s) => g.DlssNrChoice ?? s.UseDlssNr;

    /// <summary>The stored copy when there is one and this game gets it, else null.</summary>
    public static string? ActiveFor(GameSettings g, AppSettings s) => OnFor(g, s) && File.Exists(StoredPath) ? StoredPath : null;

    /// <summary>"310.8.0.0" or similar, from the file's version info; empty when it has none.</summary>
    public static string Version(string file)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(file);
            return v.FileMajorPart > 0 ? $"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}.{v.FilePrivatePart}" : (v.FileVersion ?? "").Replace(',', '.').Replace(" ", "");
        }
        catch { return ""; }
    }

    /// <summary>NVIDIA's DLSS NR runtime says so in its version info ("NVIDIA DLSSNR").</summary>
    public static bool IsDlssNr(string file)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(file);
            return $"{v.ProductName} {v.FileDescription} {v.InternalName} {v.OriginalFilename}".Contains("dlssnr", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Copies <paramref name="source"/> in as the stored file (replacing the one there). Throws with a plain reason when it is not DLSS NR.</summary>
    public static async Task StoreAsync(string source, CancellationToken ct = default)
    {
        if (!IsDlssNr(source))
            throw new InvalidOperationException(L.F("{0} is not NVIDIA's DLSS NR runtime (its version info does not say DLSSNR).", Path.GetFileName(source)));
        var dest = StoredPath;
        var tmp = dest + ".tmp";
        await using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, true))
        await using (var to = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true))
            await from.CopyToAsync(to, ct);
        File.Move(tmp, dest, overwrite: true);
        Log.Info($"DLSS NR model file stored: {Path.GetFileName(source)} {Version(dest)}");
    }

    public static void Forget()
    {
        OptiInstaller.TryDelete(StoredPath);
        Log.Info("DLSS NR model file removed");
    }

    /// <summary>
    /// The build is a DLSS NR one: its name says so (repository or archive: "DLSSNR"), it ships the NR forwarder
    /// (nvngx.dll_dlssnr.dll or similar), or its OptiScaler.dll looks for nvngx_dlssnr.dll.
    /// </summary>
    public static bool Needed(OptiRelease release, string payload)
    {
        if ($"{release.Repo} {release.AssetName}".Contains("dlssnr", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            if (Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories)
                .Any(f => Path.GetFileName(f).Contains("dlssnr", StringComparison.OrdinalIgnoreCase))) return true;
            var dll = Path.Combine(payload, "OptiScaler.dll");
            return File.Exists(dll) && Mentions(dll, FileName);
        }
        catch (Exception ex)
        {
            Log.Warn($"DLSS NR check of {payload}: {ex.Message}");
            return false;
        }
    }

    /// <summary>The installed build is a DLSS NR one (same signs as <see cref="Needed"/>, read from what the install recorded).</summary>
    public static bool NeededBy(InstallManifest m)
    {
        if ($"{m.Source} {m.AssetName}".Contains("dlssnr", StringComparison.OrdinalIgnoreCase)) return true;
        if (m.Files.Keys.Any(f => !Path.GetFileName(f).Equals(FileName, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(f).Contains("dlssnr", StringComparison.OrdinalIgnoreCase))) return true;
        var dll = Path.Combine(m.TargetDir, m.DllName);
        try { return File.Exists(dll) && Mentions(dll, FileName); }
        catch { return false; }
    }

    /// <summary>
    /// A game's switch changed with OptiScaler already in: on puts the stored file in (when the build needs it and it is not
    /// there yet, backing up one the game had), off takes it out again (only if it is still the one put in) and restores the backup.
    /// Returns true when the folder changed.
    /// </summary>
    public static bool Sync(InstallManifest m, string? stored)
    {
        var dst = Path.Combine(m.TargetDir, FileName);
        bool ours = m.Files.TryGetValue(FileName, out var hash);
        if (stored is null)
        {
            if (!ours) return false;
            if (File.Exists(dst) && OptiInstaller.Hash(dst) == hash) OptiInstaller.TryDelete(dst);
            m.Files.Remove(FileName);
            if (m.Backups.Remove(FileName, out var backup) && File.Exists(backup)) File.Copy(backup, dst, overwrite: true);
            Log.Info($"DLSS NR model file taken out of {m.TargetDir}");
            return true;
        }
        if (ours || !File.Exists(stored) || !NeededBy(m)) return false;
        if (File.Exists(dst))
        {
            if (m.BackupRoot is not { } root) return false;   // nowhere to keep the game's own file: leave it
            var backup = Path.Combine(root, FileName);
            Directory.CreateDirectory(root);
            File.Copy(dst, backup, overwrite: true);
            m.Backups[FileName] = backup;
        }
        File.Copy(stored, dst, overwrite: true);
        m.Files[FileName] = OptiInstaller.Hash(dst);
        Log.Info($"DLSS NR model file put in {m.TargetDir}");
        return true;
    }

    /// <summary>The file holds <paramref name="text"/> as ASCII or UTF-16 (any case), read in chunks.</summary>
    static bool Mentions(string file, string text)
    {
        var needles = new[] { System.Text.Encoding.ASCII.GetBytes(text.ToLowerInvariant()), System.Text.Encoding.Unicode.GetBytes(text.ToLowerInvariant()) };
        int overlap = needles.Max(n => n.Length);
        var buf = new byte[(1 << 20) + overlap];
        using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        int kept = 0, read;
        while ((read = s.Read(buf, kept, buf.Length - kept)) > 0)
        {
            int len = kept + read;
            for (int i = 0; i < len; i++) if (buf[i] is >= (byte)'A' and <= (byte)'Z') buf[i] += 32;
            var span = buf.AsSpan(0, len);
            foreach (var n in needles) if (span.IndexOf(n) >= 0) return true;
            kept = Math.Min(overlap, len);
            span[(len - kept)..].CopyTo(buf);
        }
        return false;
    }
}

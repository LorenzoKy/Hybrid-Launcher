using System.Diagnostics;
using System.Security.Cryptography;
using HybridLauncher.Core.Ini;

namespace HybridLauncher.Core.Opti;

public sealed record InstallProgress(double Fraction, string Stage, string? Line = null);

public sealed class InstallRequest
{
    public required string GameId { get; init; }
    public required string TargetDir { get; init; }
    public required OptiRelease Release { get; init; }
    public required OptiSetupOptions Options { get; init; }
    public IReadOnlyDictionary<string, string> IniOverrides { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> AsiPlugins { get; init; } = [];
    public InstallManifest? Previous { get; init; }
    /// <summary>False: the files OptiScaler replaces are overwritten without a backup, so uninstalling cannot put them back.</summary>
    public bool Backup { get; init; } = true;
}

public sealed class InstallException(string message, string? transcript = null) : Exception(message)
{
    public string? Transcript { get; } = transcript;
}

public static class OptiInstaller
{
    /// <summary>Files the setup bat may delete or rename; backed up before it runs so uninstall can put them back.</summary>
    static readonly string[] BatTouched =
        ["nvapi64.dll", "nvngx.dll", "OptiScaler.asi", "Remove OptiScaler.bat", "Remove_OptiScaler.bat",
         "dxgi.dll", "winmm.dll", "d3d12.dll", "dbghelp.dll", "version.dll", "wininet.dll", "winhttp.dll",
         @"plugins\OptiPatcher.asi", @"OptiScaler\plugins\OptiPatcher.asi"];

    /// <summary>Files OptiScaler and its helpers write while the game runs.</summary>
    internal static readonly string[] RuntimeFiles = ["OptiScaler.log", "fakenvapi.log", "dlssg_to_fsr3.log"];

    public static async Task<InstallManifest> InstallAsync(InstallRequest req, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var target = Path.GetFullPath(req.TargetDir);
        Preflight(target);

        if (req.Previous is not null)
        {
            progress?.Report(new(0.02, L.T("Removing the previous OptiScaler install")));
            var warnings = Uninstall(req.Previous, keepIniCopy: true);
            foreach (var w in warnings) progress?.Report(new(0.03, L.T("Uninstall"), w));
        }

        if (req.Release.Channel == OptiChannel.Custom)
            return await InstallCustomAsync(req, target, progress, ct);

        progress?.Report(new(0.05, L.F("Downloading {0}", req.Release.AssetName)));
        var archive = await OptiReleases.DownloadAsync(req.Release,
            new Net.InlineProgress<double>(p => progress?.Report(new(0.05 + p * 0.5, L.F("Downloading {0}", req.Release.AssetName)))), ct);

        progress?.Report(new(0.56, L.T("Extracting")));
        var work = Path.Combine(AppInfo.CacheDir, "work", Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await ArchiveExtractor.ExtractAsync(archive, work, ct);
            var payload = FindPayloadRoot(work);
            return await InstallPayloadAsync(req, target, payload, progress, ct);
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Installs an imported build (its folder is in <see cref="OptiRelease.AssetUrl"/>). The build is copied to a work folder first,
    /// so it is never changed: a renamed dll goes back to OptiScaler.dll, and a build without setup_windows.bat or OptiScaler.ini
    /// gets the missing file from the latest nightly, because OptiScaler is always installed by its own setup script.
    /// </summary>
    static async Task<InstallManifest> InstallCustomAsync(InstallRequest req, string target, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var source = req.Release.AssetUrl;
        if (!Directory.Exists(source))
            throw new InstallException(L.F("The custom build \"{0}\" is no longer in the custom builds folder.", req.Release.AssetName));

        progress?.Report(new(0.05, L.F("Preparing custom build {0}", req.Release.AssetName)));
        var work = Path.Combine(AppInfo.CacheDir, "work", Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await Task.Run(() => CustomBuilds.CopyDir(source, work, ct), ct);

            if (!File.Exists(Path.Combine(work, "OptiScaler.dll")))
            {
                var renamed = Directory.EnumerateFiles(work, "*.dll").FirstOrDefault(OptiDetector.IsOptiScaler)
                    ?? throw new InstallException(L.F("The custom build \"{0}\" has no OptiScaler dll.", req.Release.AssetName));
                File.Move(renamed, Path.Combine(work, "OptiScaler.dll"));
                progress?.Report(new(0.08, L.T("Preparing custom build"), L.F("{0} is OptiScaler: renamed to OptiScaler.dll for the setup script.", Path.GetFileName(renamed))));
            }

            var missing = CustomBuilds.BorrowedFiles.Where(f => !File.Exists(Path.Combine(work, f))).ToList();
            if (missing.Count > 0)
                await BorrowFromNightlyAsync(work, missing, progress, ct);

            return await InstallPayloadAsync(req, target, work, progress, ct);
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch { }
        }
    }

    static async Task BorrowFromNightlyAsync(string payload, List<string> files, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var what = string.Join(" and ", files);
        List<OptiRelease> nightlies;
        try { nightlies = await OptiReleases.GetAsync(OptiChannel.Nightly, ct: ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InstallException(L.F("This build has no {0} and Hybrid Launcher could not get them from the latest nightly: {1}", what, ex.Message));
        }
        var latest = nightlies.FirstOrDefault()
            ?? throw new InstallException(L.F("This build has no {0} and no nightly release was found to take them from.", what));

        progress?.Report(new(0.1, L.F("This build has no {0}: taking it from nightly {1}", what, latest.Tag)));
        var archive = await OptiReleases.DownloadAsync(latest,
            new Net.InlineProgress<double>(p => progress?.Report(new(0.1 + p * 0.45, L.F("Downloading nightly {0} for its {1}", latest.Tag, what)))), ct);
        var tmp = payload + "-nightly";
        try
        {
            await ArchiveExtractor.ExtractAsync(archive, tmp, ct);
            var root = FindPayloadRoot(tmp);
            foreach (var f in files)
            {
                var from = Path.Combine(root, f);
                if (!File.Exists(from)) throw new InstallException(L.F("Nightly {0} has no {1} to borrow.", latest.Tag, f));
                File.Copy(from, Path.Combine(payload, f));
            }
        }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    static async Task<InstallManifest> InstallPayloadAsync(InstallRequest req, string target, string payload, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var batPath = Path.Combine(payload, "setup_windows.bat");
        if (!File.Exists(batPath) || !File.Exists(Path.Combine(payload, "OptiScaler.dll")))
            throw new InstallException(L.T("This release does not contain OptiScaler.dll and setup_windows.bat; it cannot be installed with the setup script."));

        var batText = await File.ReadAllTextAsync(batPath, ct);
        var manifest = new InstallManifest
        {
            Channel = req.Release.Channel,
            Tag = req.Release.Tag,
            AssetName = req.Release.AssetName,
            TargetDir = target,
            DllName = req.Options.DllName,
            PluginsDir = batText.Contains(@"OptiScaler\plugins", StringComparison.OrdinalIgnoreCase) ? @"OptiScaler\plugins" : "plugins",
            InstalledAt = DateTime.Now,
            BackupRoot = Path.Combine(AppInfo.BackupsDir, SafeName(req.GameId), DateTime.Now.ToString("yyyyMMdd-HHmmss")),
        };

        var payloadFiles = Directory.GetFiles(payload, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(payload, f)).ToList();
        var payloadDirs = Directory.GetDirectories(payload, "*", SearchOption.AllDirectories)
            .Select(d => Path.GetRelativePath(payload, d)).ToList();

        var backupRoot = manifest.BackupRoot!;

        // 1. Back up everything that is about to be replaced or that the bat may delete (unless the user chose to just replace).
        if (req.Backup) progress?.Report(new(0.62, L.T("Backing up existing files")));
        foreach (var rel in !req.Backup ? Enumerable.Empty<string>() : payloadFiles.Concat(BatTouched).Append(req.Options.DllName).Append(@$"{manifest.PluginsDir}\OptiPatcher.asi")
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var src = Path.Combine(target, rel);
            if (!File.Exists(src)) continue;
            var dst = Path.Combine(backupRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
            manifest.Backups[rel] = dst;
        }

        foreach (var d in payloadDirs.Append(manifest.PluginsDir).Append(Path.GetDirectoryName(manifest.PluginsDir) ?? ""))
        {
            if (d.Length > 0 && !Directory.Exists(Path.Combine(target, d)) && !manifest.CreatedDirs.Contains(d, StringComparer.OrdinalIgnoreCase))
                manifest.CreatedDirs.Add(d);
        }

        try
        {
            // 2. Copy the release next to the game exe, exactly as "extract all files to the game folder" asks.
            progress?.Report(new(0.66, L.T("Copying OptiScaler files")));
            foreach (var rel in payloadFiles)
            {
                ct.ThrowIfCancellationRequested();
                var dst = Path.Combine(target, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(Path.Combine(payload, rel), dst, overwrite: true);
            }

            // 3. Let OptiScaler's own setup script do the setup: with the saved answers, or in its own window for the user.
            if (req.Options.Manual)
            {
                progress?.Report(new(0.72, L.T("Waiting for setup_windows.bat: answer it in its window, then close it")));
                var payloadHash = Hash(Path.Combine(payload, "OptiScaler.dll"));
                await RunBatInteractiveAsync(Path.Combine(target, "setup_windows.bat"), ct);
                // The user picked the file name: find the proxy name that now holds this release's dll
                // (by hash, so an older OptiScaler already in the folder is not mistaken for it).
                var renamed = OptiSetupOptions.DllNames.FirstOrDefault(n => File.Exists(Path.Combine(target, n)) && Hash(Path.Combine(target, n)) == payloadHash);
                if (renamed is null || File.Exists(Path.Combine(target, "OptiScaler.dll")))
                    throw new InstallException(L.T("setup_windows.bat closed without installing OptiScaler (OptiScaler.dll was not renamed). The folder was put back as it was."));
                manifest.DllName = renamed;
                progress?.Report(new(0.85, L.T("Setup finished"), L.F("OptiScaler was installed as {0}.", manifest.DllName)));
            }
            else
            {
                progress?.Report(new(0.72, L.T("Running OptiScaler setup")));
                var result = await BatDriver.RunAsync(Path.Combine(target, "setup_windows.bat"), req.Options,
                    line => progress?.Report(new(0.8, L.T("Running OptiScaler setup"), line)), ct);
                if (!result.Completed)
                    throw new InstallException(result.Failure ?? L.T("The setup script failed."), result.Transcript);

                var dll = Path.Combine(target, req.Options.DllName);
                if (!File.Exists(dll) || (File.Exists(Path.Combine(target, "OptiScaler.dll")) && !req.Options.DllName.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase)))
                    throw new InstallException(L.F("The setup script did not rename OptiScaler.dll to {0}. The folder may be read-only.", req.Options.DllName), result.Transcript);
            }

            // 4. Record exactly what is now ours.
            var candidates = payloadFiles
                .Select(rel => rel.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase) ? manifest.DllName : rel)
                .Append("Remove_OptiScaler.bat")
                .Append(@$"{manifest.PluginsDir}\OptiPatcher.asi")
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var rel in candidates)
            {
                var full = Path.Combine(target, rel);
                if (File.Exists(full)) manifest.Files[rel] = Hash(full);
            }

            // 5. Keep OptiScaler.ini as the install left it (OptiScaler settings → Restore defaults), then re-apply the user's
            //    ini choices and the universal ASI plugins.
            manifest.DefaultIni = SnapshotDefaultIni(req.GameId, target);
            progress?.Report(new(0.92, L.T("Applying saved settings")));
            ApplyIniOverrides(manifest, req.IniOverrides);
            SyncAsiPlugins(manifest, req.AsiPlugins);

            progress?.Report(new(1, L.F("OptiScaler {0} installed as {1}", manifest.DisplayVersion, manifest.DllName)));
            Log.Info($"Installed {manifest.DisplayVersion} into {target} as {manifest.DllName}");
            return manifest;
        }
        catch (Exception ex)
        {
            Log.Error("Install failed, rolling back", ex);
            progress?.Report(new(0, L.T("Install failed, restoring the folder")));
            foreach (var rel in payloadFiles.Append("Remove_OptiScaler.bat").Append(req.Options.DllName).Append(manifest.DllName).Append(@$"{manifest.PluginsDir}\OptiPatcher.asi"))
                TryDelete(Path.Combine(target, rel));
            RestoreBackups(manifest);
            RemoveCreatedDirs(manifest);
            throw ex is InstallException or OperationCanceledException ? ex : new InstallException(ex.Message);
        }
    }

    /// <summary>Copies the freshly installed OptiScaler.ini aside (one per game, replaced by every install); null when there is none.</summary>
    static string? SnapshotDefaultIni(string gameId, string target)
    {
        try
        {
            var ini = Path.Combine(target, "OptiScaler.ini");
            if (!File.Exists(ini)) return null;
            var copy = Path.Combine(AppInfo.BackupsDir, "default-ini", SafeName(gameId) + ".ini");
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(ini, copy, overwrite: true);
            return copy;
        }
        catch (Exception ex)
        {
            Log.Warn($"Default OptiScaler.ini copy for {gameId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Puts every setting of <paramref name="iniPath"/> back to its value in <paramref name="defaults"/> (the copy taken at install),
    /// or to "auto" when there is no copy. Only values change: the file keeps its layout and comments. Returns how many changed.
    /// </summary>
    public static int RestoreDefaultIni(string iniPath, string? defaults)
    {
        EnsureNotRunning(Path.GetDirectoryName(iniPath)!);
        var doc = IniDocument.Load(iniPath);
        var source = defaults is not null && File.Exists(defaults) ? IniDocument.Load(defaults) : null;
        int changed = 0;
        foreach (var e in doc.Entries.ToList())
        {
            var value = source is null ? "auto" : source.Find(e.Path)?.Value ?? "auto";
            if (value.Equals(e.Value, StringComparison.OrdinalIgnoreCase)) continue;
            if (doc.Set(e.Section, e.Key, value)) changed++;
        }
        if (changed > 0) doc.Save();
        return changed;
    }

    /// <summary>
    /// Opens setup_windows.bat in a normal console window in the game folder and waits until the user closes it.
    /// Cancelling closes the window.
    /// </summary>
    static async Task RunBatInteractiveAsync(string batPath, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(batPath)!;
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = $"/c \"\"{batPath}\"\"",
            WorkingDirectory = dir,
            UseShellExecute = true,
        }) ?? throw new InstallException(L.T("Windows could not open setup_windows.bat."));
        Log.Info($"Manual setup: {batPath} opened (pid {process.Id})");
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }

    /// <summary>Removes exactly what <paramref name="m"/> lists and restores what it replaced. Returns warnings, never throws for single files.</summary>
    public static List<string> Uninstall(InstallManifest m, bool keepIniCopy = false)
    {
        var warnings = new List<string>();
        var target = m.TargetDir;
        if (!Directory.Exists(target))
        {
            warnings.Add(L.F("{0} no longer exists; nothing to remove.", target));
            return warnings;
        }
        EnsureNotRunning(target);

        if (keepIniCopy && File.Exists(Path.Combine(target, "OptiScaler.ini")))
        {
            var keep = Path.Combine(AppInfo.BackupsDir, "last-ini", SafeName(target) + ".ini");
            Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
            File.Copy(Path.Combine(target, "OptiScaler.ini"), keep, overwrite: true);
        }

        foreach (var (rel, hash) in m.Files)
        {
            var full = Path.Combine(target, rel);
            if (!File.Exists(full)) continue;
            // The proxy dll (dxgi.dll, ...) may have been swapped for another mod since; only remove it if it is still OptiScaler.
            if (rel.Equals(m.DllName, StringComparison.OrdinalIgnoreCase) && Hash(full) != hash && !OptiDetector.IsOptiScaler(full))
            {
                warnings.Add(L.F("Kept {0}: it was replaced by something that is not OptiScaler after install.", rel));
                continue;
            }
            if (!TryDelete(full)) warnings.Add(L.F("Could not delete {0} (in use?)", rel));
        }

        foreach (var (name, hash) in m.AsiPlugins)
        {
            var full = Path.Combine(target, m.PluginsDir, name);
            if (File.Exists(full) && Hash(full) == hash) TryDelete(full);
        }

        foreach (var f in RuntimeFiles) TryDelete(Path.Combine(target, f));

        RemoveCreatedDirs(m);
        if (RestoreBackups(m) && m.BackupRoot is { } root && Directory.Exists(root))
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
        // A real uninstall (not the first half of a reinstall) has no more use for the default ini copy.
        if (!keepIniCopy && m.DefaultIni is { } defaults) TryDelete(defaults);
        Log.Info($"Uninstalled {m.DisplayVersion} from {target}");
        return warnings;
    }

    /// <summary>Copies enabled universal plugins in, removes ones turned off (only if unchanged), and enables ASI loading when needed.</summary>
    public static void SyncAsiPlugins(InstallManifest m, IReadOnlyList<string> enabled)
    {
        var pluginsDir = Path.Combine(m.TargetDir, m.PluginsDir);
        foreach (var name in m.AsiPlugins.Keys.ToList())
        {
            if (enabled.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            var full = Path.Combine(pluginsDir, name);
            if (File.Exists(full) && Hash(full) == m.AsiPlugins[name]) TryDelete(full);
            m.AsiPlugins.Remove(name);
        }

        foreach (var name in enabled)
        {
            var src = Path.Combine(AppInfo.AsiPluginsDir, name);
            if (!File.Exists(src)) continue;
            var dst = Path.Combine(pluginsDir, name);
            if (File.Exists(dst) && !m.AsiPlugins.ContainsKey(name) && Hash(dst) != Hash(src))
            {
                Log.Warn($"Not overwriting {dst}: a different file with that name is already there.");
                continue;
            }
            if (!Directory.Exists(pluginsDir))
            {
                Directory.CreateDirectory(pluginsDir);
                if (!m.CreatedDirs.Contains(m.PluginsDir, StringComparer.OrdinalIgnoreCase)) m.CreatedDirs.Add(m.PluginsDir);
            }
            File.Copy(src, dst, overwrite: true);
            m.AsiPlugins[name] = Hash(dst);
        }

        bool anyAsi = Directory.Exists(pluginsDir) && Directory.EnumerateFiles(pluginsDir, "*.asi").Any();
        var ini = Path.Combine(m.TargetDir, "OptiScaler.ini");
        if (anyAsi && File.Exists(ini))
        {
            var doc = IniDocument.Load(ini);
            if (doc.Get("Plugins", "LoadAsiPlugins") is { } v && !v.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                doc.Set("Plugins", "LoadAsiPlugins", "true");
                doc.Save();
            }
        }
    }

    public static void ApplyIniOverrides(InstallManifest m, IReadOnlyDictionary<string, string> overrides)
    {
        var ini = Path.Combine(m.TargetDir, "OptiScaler.ini");
        if (overrides.Count == 0 || !File.Exists(ini)) return;
        var doc = IniDocument.Load(ini);
        foreach (var (path, value) in overrides)
            if (!doc.Set(path, value)) Log.Warn($"Ini override {path} does not exist in this OptiScaler version; skipped.");
        doc.Save();
    }

    /// <summary>Throws an <see cref="InstallException"/> with a clear reason when the folder cannot be modified safely.</summary>
    public static void Preflight(string target)
    {
        if (!Directory.Exists(target)) throw new InstallException(L.F("The game folder does not exist: {0}", target));
        if (!CanWrite(target))
            throw new InstallException(L.F("Hybrid Launcher cannot write to {0}. Restart Hybrid Launcher as administrator, or move the game to a folder you own.", target)) { HResult = 5 };
        EnsureNotRunning(target);
    }

    public static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".hl-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    static void EnsureNotRunning(string target)
    {
        var running = RunningGameProcess(target);
        if (running is not null) throw new InstallException(L.F("{0} is running from this folder. Close the game first.", running));
    }

    /// <summary>
    /// The exe name of a game process running from this folder, or null. Matched by the process's real path, so another app's
    /// process with the same name (a crashpad_handler.exe of a browser) is not the game; crash handlers left behind do not count.
    /// </summary>
    public static string? RunningGameProcess(string dir)
    {
        var procs = GameSessions.InFolder(dir, helpers: false);
        try { return procs.Count > 0 ? procs[0].ProcessName + ".exe" : null; }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    static string FindPayloadRoot(string extracted)
    {
        if (File.Exists(Path.Combine(extracted, "OptiScaler.dll"))) return extracted;
        var dirs = Directory.GetDirectories(extracted);
        if (dirs.Length == 1 && File.Exists(Path.Combine(dirs[0], "OptiScaler.dll"))) return dirs[0];
        return extracted;
    }

    /// <summary>Puts every backed-up original back. Returns false if any of them could not be restored.</summary>
    static bool RestoreBackups(InstallManifest m)
    {
        bool ok = true;
        foreach (var (rel, backup) in m.Backups)
        {
            try
            {
                if (!File.Exists(backup)) continue;
                var dst = Path.Combine(m.TargetDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(backup, dst, overwrite: true);
            }
            catch (Exception ex)
            {
                ok = false;
                Log.Error($"Restoring {rel} failed", ex);
            }
        }
        return ok;
    }

    static void RemoveCreatedDirs(InstallManifest m)
    {
        foreach (var rel in m.CreatedDirs.OrderByDescending(d => d.Length))
        {
            var full = Path.Combine(m.TargetDir, rel);
            try
            {
                if (Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any()) Directory.Delete(full);
            }
            catch { }
        }
    }

    static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Delete {path} failed: {ex.Message}");
            return false;
        }
    }

    public static string Hash(string file)
    {
        using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }

    public static string SafeName(string s) =>
        string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')).Trim('_');
}

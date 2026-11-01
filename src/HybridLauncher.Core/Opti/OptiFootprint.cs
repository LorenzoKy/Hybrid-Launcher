namespace HybridLauncher.Core.Opti;

/// <summary>How much disk space OptiScaler takes in a game folder, logs included.</summary>
public sealed record OptiFootprint(long TotalBytes, int FileCount, long LogBytes, IReadOnlyList<(string RelativePath, long Bytes)> Files)
{
    public static readonly OptiFootprint Empty = new(0, 0, 0, []);

    /// <summary>Files OptiScaler ships that are recognisable by name when it was installed without Hybrid Launcher.</summary>
    static readonly string[] KnownPayload =
        ["OptiScaler.ini", "setup_windows.bat", "Remove_OptiScaler.bat", "Remove OptiScaler.bat", "fakenvapi.dll", "fakenvapi.ini",
         "dlssg_to_fsr3_amd_is_better.dll", "libxess.dll", "libxess_dx11.dll", "libxess_fg.dll", "libxell.dll",
         "amd_fidelityfx_dx12.dll", "amd_fidelityfx_vk.dll", "amd_fidelityfx_upscaler_dx12.dll", "amd_fidelityfx_framegeneration_dx12.dll",
         "amd_fidelityfx_loader_dx12.dll", @"plugins\OptiPatcher.asi", @"OptiScaler\plugins\OptiPatcher.asi"];

    static readonly string[] KnownDirs = ["D3D12_Optiscaler", "OptiScaler"];

    /// <summary>
    /// Measures the files of an OptiScaler install: what Hybrid Launcher recorded (payload, ASI plugins, folders it created) plus
    /// the logs and ini OptiScaler writes at run time. Without a manifest, the detected dll and the known payload names count.
    /// </summary>
    public static OptiFootprint Measure(string targetDir, InstallManifest? manifest, DetectedOpti? detected)
    {
        if (!Directory.Exists(targetDir) || (manifest is null && detected is null)) return Empty;
        var files = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        void AddFile(string rel)
        {
            if (files.ContainsKey(rel)) return;
            var size = DiskUsage.FileSize(Path.Combine(targetDir, rel));
            if (size > 0 || File.Exists(Path.Combine(targetDir, rel))) files[rel] = size;
        }

        void AddDir(string rel)
        {
            var full = Path.Combine(targetDir, rel);
            if (rel.Length == 0 || !Directory.Exists(full)) return;
            try
            {
                foreach (var f in Directory.EnumerateFiles(full, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    AddFile(Path.GetRelativePath(targetDir, f));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        if (manifest is not null)
        {
            foreach (var rel in manifest.Files.Keys) AddFile(rel);
            foreach (var name in manifest.AsiPlugins.Keys) AddFile(Path.Combine(manifest.PluginsDir, name));
            foreach (var rel in manifest.CustomDlls.Values.SelectMany(f => f.Keys)) AddFile(rel);
            foreach (var dir in manifest.CreatedDirs) AddDir(dir);
        }
        else
        {
            AddFile(Path.GetFileName(detected!.DllPath));
            foreach (var rel in KnownPayload) AddFile(rel);
            foreach (var dir in KnownDirs) AddDir(dir);
        }

        AddFile("OptiScaler.ini");
        long logs = 0;
        foreach (var log in OptiInstaller.RuntimeFiles)
        {
            AddFile(log);
            if (files.TryGetValue(log, out var size)) logs += size;
        }

        var list = files.OrderByDescending(f => f.Value).Select(f => (f.Key, f.Value)).ToList();
        return new OptiFootprint(list.Sum(f => f.Value), list.Count, logs, list);
    }
}

namespace HybridLauncher.Core.Opti;

/// <summary>What a custom dll entry gives OptiScaler: each kind is a subfolder of the entry with the dll in it.</summary>
public enum CustomDllKind { Ffx, Dlss, Dlssg }

/// <summary>An entry of the shared custom dll folder: its folder name (what OptiScaler's menu shows), its kinds and its size.</summary>
public sealed record CustomDllEntry(string Name, IReadOnlyList<CustomDllKind> Kinds, long Bytes, int FileCount);

/// <summary>
/// OptiScaler's custom dlls (nightly builds from 29 Oct 2026): extra upscalers and DLSSG frame generation in
/// <c>custom\&lt;name&gt;\ffx|dlss|dlssg\*.dll</c> next to OptiScaler's dll. Nothing in OptiScaler.ini turns them on: OptiScaler
/// finds the folders when the game starts and lists each one in its menu (ffx and dlss in the DX12 upscaler list under the
/// folder's name, dlssg as "Nvngx Replacement (FG Nvngx)"). Hybrid Launcher keeps the entries in one shared folder
/// (<see cref="AppInfo.CustomDllsDir"/>) and copies the ones turned on for a game into its install, recording every file.
/// </summary>
public static class CustomDlls
{
    /// <summary>The folder OptiScaler reads them from, relative to the game folder (next to OptiScaler's dll).</summary>
    public const string GameFolder = "custom";

    public static string Folder(CustomDllKind kind) => kind switch
    {
        CustomDllKind.Ffx => "ffx",
        CustomDllKind.Dlss => "dlss",
        _ => "dlssg",
    };

    /// <summary>The name OptiScaler prefers when a kind folder holds more than one dll (the others are ones that dll loads itself).</summary>
    public static string StandardName(CustomDllKind kind) => kind == CustomDllKind.Ffx ? "amd_fidelityfx_upscaler_dx12.dll" : "nvngx.dll";

    public static string Label(CustomDllKind kind) => kind switch
    {
        CustomDllKind.Ffx => L.T("FFX upscaler"),
        CustomDllKind.Dlss => L.T("DLSS upscaler"),
        _ => L.T("DLSSG frame generation"),
    };

    /// <summary>The entries in the shared folder that hold at least one dll in a kind folder, by name.</summary>
    public static List<CustomDllEntry> Entries()
    {
        var list = new List<CustomDllEntry>();
        try
        {
            foreach (var dir in Directory.GetDirectories(AppInfo.CustomDllsDir))
            {
                var kinds = Enum.GetValues<CustomDllKind>()
                    .Where(k => Directory.Exists(Path.Combine(dir, Folder(k))) && Directory.EnumerateFiles(Path.Combine(dir, Folder(k)), "*.dll").Any())
                    .ToList();
                if (kinds.Count == 0) continue;
                var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
                list.Add(new CustomDllEntry(Path.GetFileName(dir), kinds, files.Sum(f => new FileInfo(f).Length), files.Length));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"Custom dlls: {ex.Message}"); }
        return list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>A name OptiScaler can use as a folder (and in OptiScaler.ini's Dx12Upscaler): no path characters, not empty.</summary>
    public static bool IsValidName(string name) =>
        name.Trim().Length > 0 && name.Trim() == name && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name != "." && name != "..";

    /// <summary>
    /// Copies <paramref name="dlls"/> into the shared entry <paramref name="name"/> under the folder for <paramref name="kind"/>.
    /// A kind folder already there is emptied first, so the entry holds only what was picked.
    /// </summary>
    public static void Add(string name, CustomDllKind kind, IEnumerable<string> dlls)
    {
        var dir = Path.Combine(AppInfo.CustomDllsDir, name, Folder(kind));
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        foreach (var dll in dlls) File.Copy(dll, Path.Combine(dir, Path.GetFileName(dll)), overwrite: true);
    }

    /// <summary>Copies a folder already laid out for OptiScaler (ffx, dlss, dlssg inside) as the shared entry <paramref name="name"/>.</summary>
    public static void AddFolder(string name, string source)
    {
        var dest = Path.Combine(AppInfo.CustomDllsDir, name);
        if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        foreach (var kind in Enum.GetValues<CustomDllKind>())
        {
            var from = Path.Combine(source, Folder(kind));
            if (!Directory.Exists(from)) continue;
            foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(dest, Folder(kind), Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(file, to, overwrite: true);
            }
        }
    }

    /// <summary>True when <paramref name="folder"/> is laid out for OptiScaler: a dll in one of its ffx, dlss or dlssg folders.</summary>
    public static bool IsLaidOut(string folder) =>
        Enum.GetValues<CustomDllKind>().Any(k => Directory.Exists(Path.Combine(folder, Folder(k))) && Directory.EnumerateFiles(Path.Combine(folder, Folder(k)), "*.dll").Any());

    public static void Delete(string name)
    {
        var dir = Path.Combine(AppInfo.CustomDllsDir, name);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// Brings the install's custom folder in line with <paramref name="enabled"/>: entries turned on are copied in (again when the shared
    /// copy changed), entries turned off are taken out. Only files Hybrid Launcher copied and nobody changed since are deleted;
    /// a file of the same name that is not ours is never overwritten.
    /// </summary>
    public static void Sync(InstallManifest m, IReadOnlyCollection<string> enabled)
    {
        foreach (var name in m.CustomDlls.Keys.ToList())
        {
            if (enabled.Contains(name, StringComparer.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(AppInfo.CustomDllsDir, name))) continue;
            RemoveFiles(m, m.CustomDlls[name], keep: []);
            m.CustomDlls.Remove(name);
            RemoveEmptyDirs(m, name);
        }

        foreach (var name in enabled)
        {
            var source = Path.Combine(AppInfo.CustomDllsDir, name);
            if (!Directory.Exists(source)) continue;
            var ours = m.CustomDlls.TryGetValue(name, out var had) ? had : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var wanted = Directory.GetFiles(source, "*", SearchOption.AllDirectories)
                .ToDictionary(f => Path.Combine(GameFolder, name, Path.GetRelativePath(source, f)), f => f, StringComparer.OrdinalIgnoreCase);

            // Files the shared entry no longer has (a kind replaced or emptied) leave the install too.
            RemoveFiles(m, ours, keep: wanted.Keys);
            foreach (var rel in ours.Keys.Where(r => !wanted.ContainsKey(r)).ToList()) ours.Remove(rel);

            if (!Directory.Exists(Path.Combine(m.TargetDir, GameFolder)) && !m.CreatedDirs.Contains(GameFolder, StringComparer.OrdinalIgnoreCase))
                m.CreatedDirs.Add(GameFolder);
            foreach (var (rel, src) in wanted)
            {
                var dst = Path.Combine(m.TargetDir, rel);
                var hash = OptiInstaller.Hash(src);
                if (File.Exists(dst))
                {
                    var now = OptiInstaller.Hash(dst);
                    if (now == hash) { ours[rel] = hash; continue; }
                    if (!ours.TryGetValue(rel, out var recorded) || recorded != now)
                    {
                        Log.Warn($"Not overwriting {dst}: a different file with that name is already there.");
                        continue;
                    }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, overwrite: true);
                ours[rel] = hash;
            }
            m.CustomDlls[name] = ours;
            RemoveEmptyDirs(m, name);
        }
    }

    /// <summary>Deletes the recorded files not in <paramref name="keep"/> that still are what Hybrid Launcher copied.</summary>
    static void RemoveFiles(InstallManifest m, Dictionary<string, string> files, IEnumerable<string> keep)
    {
        var kept = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
        foreach (var (rel, hash) in files)
        {
            if (kept.Contains(rel)) continue;
            var full = Path.Combine(m.TargetDir, rel);
            if (File.Exists(full) && OptiInstaller.Hash(full) == hash) OptiInstaller.TryDelete(full);
        }
    }

    /// <summary>Takes out the entry's empty folders, and the custom folder when Hybrid Launcher made it and it is empty.</summary>
    static void RemoveEmptyDirs(InstallManifest m, string name)
    {
        var root = Path.Combine(m.TargetDir, GameFolder);
        var entry = Path.Combine(root, name);
        try
        {
            if (Directory.Exists(entry))
            {
                foreach (var dir in Directory.GetDirectories(entry, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).Append(entry))
                    if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            if (m.CustomDlls.Count == 0 && m.CreatedDirs.Contains(GameFolder, StringComparer.OrdinalIgnoreCase)
                && Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
                Directory.Delete(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"Custom dlls, folders of {name}: {ex.Message}"); }
    }
}

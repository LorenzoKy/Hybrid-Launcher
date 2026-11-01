using HybridLauncher.Core.Ini;

namespace HybridLauncher.Core.Opti;

/// <summary>What a dll in a custom entry is to OptiScaler: each kind is a subfolder of the entry with the dll in it.</summary>
public enum CustomDllKind { Ffx, FfxVk, Dlss, Dlssg }

/// <summary>One dll of an entry: its kind folder, file name and size. <see cref="Loaded"/>: the one OptiScaler loads from that folder.</summary>
public sealed record CustomDllFile(CustomDllKind Kind, string FileName, long Bytes, bool Loaded);

/// <summary>An entry of the shared custom dll folder: its folder name (the name OptiScaler's menu shows) and its dlls.</summary>
public sealed record CustomDllEntry(string Name, IReadOnlyList<CustomDllFile> Files)
{
    public IReadOnlyList<CustomDllKind> Kinds => Files.Select(f => f.Kind).Distinct().OrderBy(k => k).ToList();
    public long Bytes => Files.Sum(f => f.Bytes);
}

/// <summary>
/// OptiScaler's custom dlls (nightly builds from 28-29 Oct 2026, misc/CustomProviders.h): extra upscalers and DLSSG frame
/// generation in <c>custom\&lt;name&gt;\&lt;kind&gt;\*.dll</c> of OptiScaler's own folder (the "OptiScaler" folder next to the
/// renamed dll when it exists, else the game folder; OptiDllPath in OptiScaler.ini moves it). Each kind a folder has is an
/// entry in OptiScaler's menu under the folder's name:
///   ffx     DX12 FFX upscaler            (standard name amd_fidelityfx_upscaler_dx12.dll)
///   ffx_vk  Vulkan FFX upscaler          (amd_fidelityfx_upscaler_vk.dll, then the legacy amd_fidelityfx_vk.dll)
///   dlss    NGX runtime that runs DLSS   (nvngx.dll)
///   dlssg   nvngx_dlssg replacement, an FG Nvngx entry (nvngx_dlssg.dll)
/// The dll can have any name; with more dlls in a kind folder (ones the dll loads itself) the standard name is used, else the
/// first one. Nothing in OptiScaler.ini turns them on: picking one in the menu writes the folder name to Dx12Upscaler,
/// VulkanUpscaler or FGNvngxReplacement. Hybrid Launcher keeps the entries in one shared folder laid out the same way
/// (<see cref="AppInfo.CustomDllsDir"/>) and copies the ones turned on for a game into its install, recording every file.
/// </summary>
public static class CustomDlls
{
    public static readonly CustomDllKind[] Kinds = Enum.GetValues<CustomDllKind>();

    public static string Folder(CustomDllKind kind) => kind switch
    {
        CustomDllKind.Ffx => "ffx",
        CustomDllKind.FfxVk => "ffx_vk",
        CustomDllKind.Dlss => "dlss",
        _ => "dlssg",
    };

    /// <summary>The names OptiScaler prefers, in order, when a kind folder holds more than one dll.</summary>
    public static string[] StandardNames(CustomDllKind kind) => kind switch
    {
        CustomDllKind.Ffx => ["amd_fidelityfx_upscaler_dx12.dll"],
        CustomDllKind.FfxVk => ["amd_fidelityfx_upscaler_vk.dll", "amd_fidelityfx_vk.dll"],
        CustomDllKind.Dlss => ["nvngx.dll"],
        _ => ["nvngx_dlssg.dll"],
    };

    public static string Label(CustomDllKind kind) => kind switch
    {
        CustomDllKind.Ffx => L.T("FFX upscaler, DX12"),
        CustomDllKind.FfxVk => L.T("FFX upscaler, Vulkan"),
        CustomDllKind.Dlss => L.T("DLSS upscaler (NGX runtime)"),
        _ => L.T("DLSSG frame generation"),
    };

    /// <summary>Where OptiScaler lists the entry, for the hints.</summary>
    public static string MenuPlace(CustomDllKind kind) => kind switch
    {
        CustomDllKind.Ffx or CustomDllKind.Dlss => L.T("DX12 upscaler list"),
        CustomDllKind.FfxVk => L.T("Vulkan upscaler list"),
        _ => L.T("FG Nvngx list"),
    };

    /// <summary>The kind a dll most likely is, from its file name (the user can change it).</summary>
    public static CustomDllKind Guess(string fileName)
    {
        var n = fileName.ToLowerInvariant();
        if (n.Contains("dlssg")) return CustomDllKind.Dlssg;
        if (n.StartsWith("nvngx") || n.Contains("dlss")) return CustomDllKind.Dlss;
        if (n.Contains("_vk") || n.Contains("vulkan")) return CustomDllKind.FfxVk;
        return CustomDllKind.Ffx;
    }

    /// <summary>The dll OptiScaler loads from a kind folder: a standard name first, else the first dll.</summary>
    public static string? LoadedName(CustomDllKind kind, IEnumerable<string> fileNames)
    {
        var names = fileNames.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var standard in StandardNames(kind))
            if (names.FirstOrDefault(n => n.Equals(standard, StringComparison.OrdinalIgnoreCase)) is { } hit) return hit;
        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    static string EntryDir(string name) => Path.Combine(AppInfo.CustomDllsDir, name);
    static string KindDir(string name, CustomDllKind kind) => Path.Combine(EntryDir(name), Folder(kind));

    /// <summary>The entries in the shared folder that hold at least one dll in a kind folder, by name.</summary>
    public static List<CustomDllEntry> Entries()
    {
        var list = new List<CustomDllEntry>();
        try
        {
            foreach (var dir in Directory.GetDirectories(AppInfo.CustomDllsDir))
            {
                var name = Path.GetFileName(dir);
                var files = new List<CustomDllFile>();
                foreach (var kind in Kinds)
                {
                    var kindDir = Path.Combine(dir, Folder(kind));
                    if (!Directory.Exists(kindDir)) continue;
                    var names = Directory.GetFiles(kindDir).Select(Path.GetFileName).Select(n => n!).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                    var loaded = LoadedName(kind, names);
                    foreach (var file in names)
                        files.Add(new CustomDllFile(kind, file, new FileInfo(Path.Combine(kindDir, file)).Length, file.Equals(loaded, StringComparison.OrdinalIgnoreCase)));
                }
                if (files.Any(f => f.FileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))) list.Add(new CustomDllEntry(name, files));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"Custom dlls: {ex.Message}"); }
        return list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>A name OptiScaler can use as a folder (and in OptiScaler.ini): no path characters, no spaces around it, not empty.</summary>
    public static bool IsValidName(string name) =>
        name.Length > 0 && name.Trim() == name && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name != "." && name != ".."
        && !name.EndsWith('.');

    /// <summary>A dll file name: a valid name that ends in .dll.</summary>
    public static bool IsValidDllName(string name) => IsValidName(name) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && name.Length > 4;

    public static bool Exists(string name) => Directory.Exists(EntryDir(name));

    public static bool FileExists(string name, CustomDllKind kind, string fileName) => File.Exists(Path.Combine(KindDir(name, kind), fileName));

    /// <summary>Copies <paramref name="source"/> into entry <paramref name="name"/> as <paramref name="fileName"/> in the folder for <paramref name="kind"/>.</summary>
    public static void AddFile(string name, CustomDllKind kind, string source, string fileName)
    {
        var dir = KindDir(name, kind);
        Directory.CreateDirectory(dir);
        File.Copy(source, Path.Combine(dir, fileName), overwrite: true);
    }

    /// <summary>Copies a folder already laid out for OptiScaler (ffx, ffx_vk, dlss, dlssg inside) into entry <paramref name="name"/>.</summary>
    public static void AddFolder(string name, string source)
    {
        foreach (var kind in Kinds)
        {
            var from = Path.Combine(source, Folder(kind));
            if (!Directory.Exists(from)) continue;
            foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(KindDir(name, kind), Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(file, to, overwrite: true);
            }
        }
    }

    /// <summary>True when <paramref name="folder"/> is laid out for OptiScaler: a dll in one of its ffx, ffx_vk, dlss or dlssg folders.</summary>
    public static bool IsLaidOut(string folder) =>
        Kinds.Any(k => Directory.Exists(Path.Combine(folder, Folder(k))) && Directory.EnumerateFiles(Path.Combine(folder, Folder(k)), "*.dll").Any());

    /// <summary>
    /// Moves (or renames) one file: to another name, kind or entry. A file already at the destination is replaced.
    /// Empty kind folders and an emptied entry are removed.
    /// </summary>
    public static void MoveFile(string name, CustomDllKind kind, string fileName, string toName, CustomDllKind toKind, string toFileName)
    {
        var from = Path.Combine(KindDir(name, kind), fileName);
        var to = Path.Combine(KindDir(toName, toKind), toFileName);
        if (from.Equals(to, StringComparison.Ordinal)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        // A change of case only (same file on NTFS) goes through a temporary name.
        if (from.Equals(to, StringComparison.OrdinalIgnoreCase))
        {
            var tmp = to + ".moving";
            File.Move(from, tmp);
            File.Move(tmp, to);
        }
        else File.Move(from, to, overwrite: true);
        Tidy(name);
    }

    public static void DeleteFile(string name, CustomDllKind kind, string fileName)
    {
        var path = Path.Combine(KindDir(name, kind), fileName);
        if (File.Exists(path)) File.Delete(path);
        Tidy(name);
    }

    /// <summary>Renames an entry's folder (the name OptiScaler's menu shows). <paramref name="to"/> must not exist yet, apart from a change of case.</summary>
    public static void RenameEntry(string from, string to)
    {
        var src = EntryDir(from);
        var dst = EntryDir(to);
        if (from.Equals(to, StringComparison.OrdinalIgnoreCase))
        {
            var tmp = dst + ".renaming";
            Directory.Move(src, tmp);
            Directory.Move(tmp, dst);
        }
        else Directory.Move(src, dst);
    }

    public static void Delete(string name)
    {
        var dir = EntryDir(name);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>Removes the entry's empty kind folders, and the entry itself when nothing is left.</summary>
    static void Tidy(string name)
    {
        var dir = EntryDir(name);
        if (!Directory.Exists(dir)) return;
        foreach (var sub in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            if (!Directory.EnumerateFileSystemEntries(sub).Any()) Directory.Delete(sub);
        if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
    }

    // ---------- the game's side ----------

    /// <summary>
    /// OptiScaler's folder relative to the game folder, as OptiScaler finds it at start: OptiDllPath from OptiScaler.ini when it
    /// is set and inside the game folder, else the "OptiScaler" folder when there is one, else the game folder itself ("").
    /// </summary>
    public static string OptiFolder(string target)
    {
        try
        {
            var ini = Path.Combine(target, "OptiScaler.ini");
            if (File.Exists(ini) && IniDocument.Load(ini).Get("Libraries", "OptiDllPath") is { } set
                && set.Length > 0 && !set.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                var full = Path.GetFullPath(Path.IsPathRooted(set) ? set : Path.Combine(target, set));
                var rel = Path.GetRelativePath(target, full);
                if (Directory.Exists(full) && !rel.StartsWith("..") && !Path.IsPathRooted(rel)) return rel == "." ? "" : rel;
            }
        }
        catch (Exception ex) { Log.Warn($"Custom dlls, OptiDllPath in {target}: {ex.Message}"); }
        return Directory.Exists(Path.Combine(target, "OptiScaler")) ? "OptiScaler" : "";
    }

    /// <summary>The custom folder relative to the game folder: custom inside <see cref="OptiFolder"/>.</summary>
    public static string GameFolder(string target) => Path.Combine(OptiFolder(target), "custom");

    /// <summary>
    /// Brings the install's custom folder in line with <paramref name="enabled"/>: entries turned on are copied in (again when the shared
    /// copy changed), entries turned off are taken out. Only files Hybrid Launcher copied and nobody changed since are deleted;
    /// a file of the same name that is not ours is never overwritten.
    /// </summary>
    public static void Sync(InstallManifest m, IReadOnlyCollection<string> enabled)
    {
        var root = GameFolder(m.TargetDir);
        foreach (var name in m.CustomDlls.Keys.ToList())
        {
            if (enabled.Contains(name, StringComparer.OrdinalIgnoreCase) && Exists(name)) continue;
            RemoveFiles(m, m.CustomDlls[name], keep: []);
            m.CustomDlls.Remove(name);
            RemoveEmptyDirs(m, root, name);
        }

        foreach (var name in enabled)
        {
            var source = EntryDir(name);
            if (!Directory.Exists(source)) continue;
            var ours = m.CustomDlls.TryGetValue(name, out var had) ? had : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var wanted = Directory.GetFiles(source, "*", SearchOption.AllDirectories)
                .ToDictionary(f => Path.Combine(root, name, Path.GetRelativePath(source, f)), f => f, StringComparer.OrdinalIgnoreCase);

            // Files the shared entry no longer has (renamed, moved, deleted), or that sit where OptiScaler no longer looks, leave.
            RemoveFiles(m, ours, keep: wanted.Keys);
            foreach (var rel in ours.Keys.Where(r => !wanted.ContainsKey(r)).ToList()) ours.Remove(rel);

            if (!Directory.Exists(Path.Combine(m.TargetDir, root)) && !m.CreatedDirs.Contains(root, StringComparer.OrdinalIgnoreCase))
                m.CreatedDirs.Add(root);
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
            RemoveEmptyDirs(m, root, name);
        }
    }

    /// <summary>
    /// After an entry was renamed: OptiScaler.ini settings that picked it by its old folder name (Dx12Upscaler, VulkanUpscaler,
    /// FGNvngxReplacement) follow the new one. Returns whether the file changed.
    /// </summary>
    public static bool RenameInIni(string target, string from, string to)
    {
        var ini = Path.Combine(target, "OptiScaler.ini");
        if (!File.Exists(ini)) return false;
        var doc = IniDocument.Load(ini);
        bool changed = false;
        foreach (var path in IniKeys)
            if (doc.Find(path)?.Value is { } v && v.Equals(from, StringComparison.OrdinalIgnoreCase)) changed |= doc.Set(path, to);
        if (changed) doc.Save();
        return changed;
    }

    /// <summary>The OptiScaler.ini settings whose value can be a custom entry's folder name.</summary>
    public static readonly string[] IniKeys = ["Upscalers/Dx12Upscaler", "Upscalers/VulkanUpscaler", "FrameGen/FGNvngxReplacement"];

    /// <summary>Deletes the recorded files not in <paramref name="keep"/> that still are what Hybrid Launcher copied.</summary>
    static void RemoveFiles(InstallManifest m, Dictionary<string, string> files, IEnumerable<string> keep)
    {
        var kept = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
        foreach (var (rel, hash) in files)
        {
            if (kept.Contains(rel)) continue;
            var full = Path.Combine(m.TargetDir, rel);
            if (File.Exists(full) && OptiInstaller.Hash(full) == hash) OptiInstaller.TryDelete(full);
            // The entry's folders where the file was, possibly under a custom folder OptiScaler no longer uses.
            for (var dir = Path.GetDirectoryName(full); dir is not null && dir.Length > m.TargetDir.Length; dir = Path.GetDirectoryName(dir))
            {
                var rel2 = Path.GetRelativePath(m.TargetDir, dir);
                if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any()) break;
                // Only folders under a custom folder, or a custom folder Hybrid Launcher made.
                if (!rel2.Split(Path.DirectorySeparatorChar).Contains("custom", StringComparer.OrdinalIgnoreCase)) break;
                if (Path.GetFileName(dir).Equals("custom", StringComparison.OrdinalIgnoreCase) && !m.CreatedDirs.Contains(rel2, StringComparer.OrdinalIgnoreCase)) break;
                try { Directory.Delete(dir); } catch (IOException) { break; }
            }
        }
    }

    /// <summary>Takes out the entry's empty folders, and the custom folder when Hybrid Launcher made it and it is empty.</summary>
    static void RemoveEmptyDirs(InstallManifest m, string root, string name)
    {
        var full = Path.Combine(m.TargetDir, root);
        var entry = Path.Combine(full, name);
        try
        {
            if (Directory.Exists(entry))
            {
                foreach (var dir in Directory.GetDirectories(entry, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).Append(entry))
                    if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            if (m.CustomDlls.Count == 0 && m.CreatedDirs.Contains(root, StringComparer.OrdinalIgnoreCase)
                && Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any())
                Directory.Delete(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"Custom dlls, folders of {name}: {ex.Message}"); }
    }
}

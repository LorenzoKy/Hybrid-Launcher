using System.Diagnostics;
using System.Text.Json;
using HybridLauncher.Core.Ini;
using HybridLauncher.Core.Net;

namespace HybridLauncher.Core.Mods;

/// <summary>A ReShade installer on reshade.me: the plain build or the one with full add-on support (RenoDX needs that one).</summary>
public sealed record ReShadeRelease(string Version, bool Addon)
{
    public string FileName => $"ReShade_Setup_{Version}{(Addon ? "_Addon" : "")}.exe";
    public string Url => $"https://reshade.me/downloads/{FileName}";
    public string Display => Addon ? $"{Version}  ·  with full add-on support" : Version;
}

/// <summary>A ReShade dll found in a game folder.</summary>
public sealed record DetectedReShade(string Path, string Version)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public bool LoadedByOpti => FileName.Equals(ReShade.OptiDllName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// ReShade: Hybrid Launcher only downloads the official installers from reshade.me (kept as saved versions) and starts them; the
/// installer itself puts ReShade in the game. With OptiScaler in the same folder both want to be dxgi.dll, so "Load ReShade
/// through OptiScaler" renames ReShade's dll to ReShade64.dll and turns on [Plugins] LoadReshade in OptiScaler.ini:
/// OptiScaler then loads ReShade itself.
/// </summary>
public static class ReShade
{
    /// <summary>The name OptiScaler looks for when LoadReshade is on.</summary>
    public const string OptiDllName = "ReShade64.dll";
    public const string IniKey = "Plugins/LoadReshade";

    /// <summary>The names the ReShade installer gives its dll (by graphics API), plus the one OptiScaler loads.</summary>
    static readonly string[] DllNames = ["dxgi.dll", "d3d11.dll", "d3d12.dll", "d3d9.dll", "d3d10.dll", "opengl32.dll", "dinput8.dll", "winmm.dll", "version.dll", OptiDllName, "ReShade32.dll"];

    public static string Dir => Ensure(System.IO.Path.Combine(AppInfo.DataRoot, "ReShade"));

    /// <summary>Released versions, newest first (GitHub tags of crosire/reshade; the installers themselves come from reshade.me).</summary>
    public static async Task<List<string>> GetVersionsAsync(bool force = false, CancellationToken ct = default)
    {
        var json = await Http.GetCachedAsync("https://api.github.com/repos/crosire/reshade/tags?per_page=40", "reshade-tags.json",
            TimeSpan.FromHours(6), force, "application/vnd.github+json", ct);
        var list = new List<string>();
        if (json is null) return list;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var t in doc.RootElement.EnumerateArray())
        {
            var name = t.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (name is null || !name.StartsWith('v')) continue;
            var v = name[1..];
            // Only plain releases (6.3.3), not pre-release tags.
            if (Version.TryParse(v, out _)) list.Add(v);
        }
        return list.OrderByDescending(v => Version.Parse(v)).ToList();
    }

    /// <summary>Installers already on this PC, newest first.</summary>
    public static List<(ReShadeRelease Release, string Path, long Size, DateTime Date)> Saved()
    {
        var list = new List<(ReShadeRelease, string, long, DateTime)>();
        try
        {
            foreach (var f in Directory.GetFiles(Dir, "ReShade_Setup_*.exe"))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(f)["ReShade_Setup_".Length..];
                bool addon = name.EndsWith("_Addon", StringComparison.OrdinalIgnoreCase);
                var version = addon ? name[..^"_Addon".Length] : name;
                if (!Version.TryParse(version, out _)) continue;
                var info = new FileInfo(f);
                list.Add((new ReShadeRelease(version, addon), f, info.Length, info.LastWriteTime));
            }
        }
        catch (Exception ex) { Log.Warn($"ReShade saved versions: {ex.Message}"); }
        return list.OrderByDescending(x => Version.Parse(x.Item1.Version)).ThenBy(x => x.Item1.Addon).ToList();
    }

    public static string SavedPath(ReShadeRelease r) => System.IO.Path.Combine(Dir, r.FileName);

    /// <summary>The installer on disk: the saved copy, or downloaded from reshade.me first.</summary>
    public static async Task<string> DownloadAsync(ReShadeRelease release, IProgress<double>? progress, CancellationToken ct)
    {
        var dest = SavedPath(release);
        if (File.Exists(dest) && new FileInfo(dest).Length > 1_000_000 && IsSigned(dest))
        {
            progress?.Report(1);
            return dest;
        }
        Log.Info($"Downloading {release.Url}");
        await Http.DownloadFileAsync(release.Url, dest, 0, progress, ct);
        if (new FileInfo(dest).Length < 1_000_000 || !IsSigned(dest))
        {
            File.Delete(dest);
            throw new InvalidOperationException($"reshade.me did not send a valid {release.FileName}. Check the version and try again.");
        }
        return dest;
    }

    /// <summary>A real installer is a PE file whose version resource names ReShade.</summary>
    static bool IsSigned(string exe)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(exe);
            return (v.ProductName ?? v.FileDescription ?? "").Contains("ReShade", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Starts the installer with the game exe preselected (ReShade's setup takes the target as its argument).</summary>
    public static void RunInstaller(string installer, string? gameExe)
    {
        var psi = new ProcessStartInfo(installer) { UseShellExecute = true, WorkingDirectory = System.IO.Path.GetDirectoryName(installer)! };
        if (gameExe is not null && File.Exists(gameExe)) psi.ArgumentList.Add(gameExe);
        Log.Info($"ReShade installer: {installer} {gameExe}");
        Process.Start(psi)?.Dispose();
    }

    public static void Delete(string installer)
    {
        if (System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(installer))!.Equals(System.IO.Path.GetFullPath(Dir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            File.Delete(installer);
    }

    /// <summary>Finds ReShade in the game folder by the dll's version resource, whatever its name.</summary>
    public static DetectedReShade? Detect(string dir)
    {
        foreach (var name in DllNames)
        {
            var path = System.IO.Path.Combine(dir, name);
            if (File.Exists(path) && IsReShade(path)) return new DetectedReShade(path, DllVersion(path));
        }
        return null;
    }

    public static bool IsReShade(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            return (v.ProductName ?? "").Contains("ReShade", StringComparison.OrdinalIgnoreCase)
                   || (v.FileDescription ?? "").Contains("ReShade", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    static string DllVersion(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            return $"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}";
        }
        catch { return "?"; }
    }

    /// <summary>OptiScaler.ini's LoadReshade value in the folder, or null when there is no OptiScaler.ini.</summary>
    public static bool? OptiLoadsReShade(string dir)
    {
        var ini = System.IO.Path.Combine(dir, "OptiScaler.ini");
        if (!File.Exists(ini)) return null;
        try { return IniDocument.Load(ini).Find(IniKey)?.Value.Equals("true", StringComparison.OrdinalIgnoreCase) == true; }
        catch { return null; }
    }

    /// <summary>What <see cref="LoadThroughOpti"/> did, in plain words.</summary>
    public sealed record SetupResult(string Message, bool OptiDllReplaced);

    /// <summary>
    /// Makes OptiScaler load ReShade: ReShade's dll becomes ReShade64.dll (or is copied back from OptiScaler's backup when the
    /// OptiScaler install had replaced it), and LoadReshade=true goes into OptiScaler.ini and into the game's saved ini choices,
    /// so it survives OptiScaler updates. <paramref name="optiDll"/> is OptiScaler's own file name in the folder.
    /// </summary>
    public static SetupResult LoadThroughOpti(string dir, GameSettings settings, string? optiDll)
    {
        var ini = System.IO.Path.Combine(dir, "OptiScaler.ini");
        if (!File.Exists(ini)) throw new InvalidOperationException(L.T("OptiScaler is not installed in this folder (no OptiScaler.ini). Install OptiScaler first."));

        var target = System.IO.Path.Combine(dir, OptiDllName);
        string message;
        bool optiReplaced = false;
        var found = Detect(dir);
        if (found is { LoadedByOpti: true })
            message = L.F("ReShade {0} is already {1}.", found.Version, OptiDllName);
        else if (found is not null)
        {
            // The ReShade installer may have written over OptiScaler's own dll (both like dxgi.dll).
            optiReplaced = optiDll is not null && found.FileName.Equals(optiDll, StringComparison.OrdinalIgnoreCase);
            if (File.Exists(target)) File.Delete(target);
            File.Move(found.Path, target);
            settings.ReShade.RenamedFrom = found.FileName;
            settings.ReShade.RestoredFromBackup = false;
            message = L.F("Renamed ReShade's {0} to {1}.", found.FileName, OptiDllName);
        }
        else if (settings.Install?.Backups.Select(b => b.Value).FirstOrDefault(b => File.Exists(b) && IsReShade(b)) is { } backup)
        {
            // OptiScaler went in over ReShade: its backup holds ReShade's dll. Copied, so uninstalling OptiScaler still restores it.
            File.Copy(backup, target, overwrite: true);
            settings.ReShade.RenamedFrom = null;
            settings.ReShade.RestoredFromBackup = true;
            message = L.F("ReShade had been replaced when OptiScaler was installed: Hybrid Launcher put it back as {0}.", OptiDllName);
        }
        else throw new InvalidOperationException(L.T("No ReShade dll in this folder. Run the ReShade installer for this game first, then press the button again."));

        var doc = IniDocument.Load(ini);
        if (!doc.Set(IniKey, "true")) throw new InvalidOperationException(L.T("This OptiScaler version has no LoadReshade setting in OptiScaler.ini."));
        doc.Save();
        settings.IniOverrides[IniKey] = "true";
        return new SetupResult(message + " OptiScaler now loads it (LoadReshade = true).", optiReplaced);
    }

    /// <summary>Undoes <see cref="LoadThroughOpti"/>: LoadReshade back to Auto, ReShade64.dll back to its old name (or removed when it was a copy).</summary>
    public static string StopLoadingThroughOpti(string dir, GameSettings settings)
    {
        var ini = System.IO.Path.Combine(dir, "OptiScaler.ini");
        if (File.Exists(ini))
        {
            var doc = IniDocument.Load(ini);
            if (doc.Set(IniKey, "auto")) doc.Save();
        }
        settings.IniOverrides.Remove(IniKey);

        var dll = System.IO.Path.Combine(dir, OptiDllName);
        string message = L.T("OptiScaler no longer loads ReShade.");
        if (File.Exists(dll) && IsReShade(dll))
        {
            if (settings.ReShade.RestoredFromBackup)
            {
                File.Delete(dll);
                message += $" The {OptiDllName} copy was removed (OptiScaler's backup still holds the original).";
            }
            else if (settings.ReShade.RenamedFrom is { } old)
            {
                var back = System.IO.Path.Combine(dir, old);
                if (File.Exists(back)) message += $" {old} is taken (OptiScaler?), so ReShade stays as {OptiDllName}.";
                else
                {
                    File.Move(dll, back);
                    message += $" ReShade is {old} again.";
                }
            }
        }
        settings.ReShade.RenamedFrom = null;
        settings.ReShade.RestoredFromBackup = false;
        return message;
    }

    /// <summary>
    /// Takes ReShade out of the game: every ReShade dll Hybrid Launcher knows the name of (ReShade64.dll included, which ReShade's own
    /// setup does not look for), its logs, and LoadReshade in OptiScaler.ini. ReShade.ini, presets and shaders stay, so a later
    /// install finds them again. Returns what was removed, in plain words.
    /// </summary>
    public static string Uninstall(string dir, GameSettings settings)
    {
        var removed = new List<string>();
        foreach (var name in DllNames)
        {
            var path = System.IO.Path.Combine(dir, name);
            if (!File.Exists(path) || !IsReShade(path)) continue;
            File.Delete(path);
            removed.Add(name);
        }
        foreach (var log in Directory.GetFiles(dir, "ReShade.log*")) { try { File.Delete(log); } catch { } }
        var ini = System.IO.Path.Combine(dir, "OptiScaler.ini");
        if (File.Exists(ini))
        {
            var doc = IniDocument.Load(ini);
            if (doc.Find(IniKey)?.Value.Equals("true", StringComparison.OrdinalIgnoreCase) == true && doc.Set(IniKey, "auto")) doc.Save();
        }
        settings.IniOverrides.Remove(IniKey);
        settings.ReShade.RenamedFrom = null;
        settings.ReShade.RestoredFromBackup = false;
        Log.Info($"ReShade removed from {dir}: {string.Join(", ", removed)}");
        return removed.Count == 0
            ? L.T("There was no ReShade dll in the game folder.")
            : L.F("Removed {0}. ReShade.ini, presets and shaders are kept for a later install.", string.Join(", ", removed));
    }

    static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}

namespace HybridLauncher.Core.Opti;

/// <summary>
/// Changes the file name OptiScaler is loaded as (dxgi.dll, winmm.dll, version.dll…) in place, without reinstalling: the dll
/// is renamed, the install's record follows (its files, the name), and a game file the install had replaced under the old name
/// is put back. Every other file of the install stays as it is.
/// </summary>
public static class OptiDllSwap
{
    /// <summary>The names a quick swap can use: the setup's list less OptiScaler.asi, which needs an ASI loader in the game.</summary>
    public static IEnumerable<string> Names => OptiSetupOptions.DllNames.Where(n => !n.EndsWith(".asi", StringComparison.OrdinalIgnoreCase));

    /// <summary>What a name is used for in games, shown beside it.</summary>
    public static string Hint(string name) => name.ToLowerInvariant() switch
    {
        "dxgi.dll" => L.T("Works in most games (DirectX 11 and 12)"),
        "winmm.dll" => L.T("Loaded by nearly every game; good when dxgi.dll is taken"),
        "version.dll" => L.T("Loaded by nearly every game; good when dxgi.dll is taken"),
        "dbghelp.dll" => L.T("Some games only"),
        "d3d12.dll" => L.T("DirectX 12 games only"),
        "wininet.dll" or "winhttp.dll" => L.T("Games that go online at start"),
        _ => "",
    };

    /// <summary>Why <paramref name="name"/> cannot be used now, or null when it can.</summary>
    public static string? WhyNot(InstallManifest m, string name)
    {
        if (name.Equals(m.DllName, StringComparison.OrdinalIgnoreCase)) return L.T("in use now");
        if (!Names.Contains(name, StringComparer.OrdinalIgnoreCase)) return L.T("not a name OptiScaler is loaded as");
        var path = Path.Combine(m.TargetDir, name);
        if (File.Exists(path)) return L.F("taken by {0}", Mods.ModCheck.Owner(path));
        return null;
    }

    /// <summary>
    /// Renames OptiScaler's dll to <paramref name="name"/>. Throws with a reason when the name is taken, the dll is missing, or
    /// the file is in use (the game running).
    /// </summary>
    public static void Swap(InstallManifest m, string name)
    {
        if (WhyNot(m, name) is { } why) throw new InvalidOperationException(L.F("{0} cannot be used: {1}.", name, why));
        var from = Path.Combine(m.TargetDir, m.DllName);
        var to = Path.Combine(m.TargetDir, name);
        if (!File.Exists(from) || !OptiDetector.IsOptiScaler(from))
            throw new InvalidOperationException(L.F("OptiScaler's {0} is not in the game folder: install OptiScaler again.", m.DllName));
        var old = m.DllName;
        File.Move(from, to);
        try
        {
            // The game's own file under the old name (backed up by the install) goes back where it was. A backup that is
            // itself an OptiScaler (an older one the install replaced) is not the game's: putting it back left two OptiScalers.
            if (m.Backups.TryGetValue(old, out var backup) && File.Exists(backup))
            {
                if (!OptiDetector.IsOptiScaler(backup))
                {
                    File.Copy(backup, from, overwrite: false);
                    m.Backups.Remove(old);
                }
                // The user's own OptiScaler stays recorded: it goes back at uninstall, not now (two would load).
                else if (!m.UserOptiBackups.Contains(old, StringComparer.OrdinalIgnoreCase)) m.Backups.Remove(old);
            }
        }
        catch
        {
            File.Move(to, from);
            throw;
        }
        if (m.Files.Remove(old, out var hash)) m.Files[name] = hash;
        m.DllName = name;
        Log.Info($"OptiScaler renamed {old} -> {name} in {m.TargetDir}");
        RemoveOtherCopies(m);
    }

    /// <summary>
    /// Takes out any other OptiScaler dll beside the game under a name it loads as (an older install, a copy left by a
    /// rename): two of them make the game load OptiScaler twice. Each is moved into the install's backup folder, not
    /// recorded as something to put back, unless <paramref name="userOwn"/> (an install where Hybrid Launcher had none
    /// before): then it is the user's own and goes back at uninstall. Returns the names taken out.
    /// </summary>
    public static List<string> RemoveOtherCopies(InstallManifest m, bool userOwn = false)
    {
        var removed = new List<string>();
        foreach (var name in Names.Append("OptiScaler.dll"))
        {
            if (name.Equals(m.DllName, StringComparison.OrdinalIgnoreCase)) continue;
            var path = Path.Combine(m.TargetDir, name);
            try
            {
                if (!File.Exists(path) || !OptiDetector.IsOptiScaler(path)) continue;
                var keep = Path.Combine(m.BackupRoot ?? Path.Combine(AppInfo.BackupsDir, "optiscaler-copies"), "other-optiscaler", name);
                Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
                File.Copy(path, keep, overwrite: true);
                File.Delete(path);
                bool ours = m.Files.Remove(name);
                if (userOwn && !ours)
                {
                    m.Backups[name] = keep;
                    if (!m.UserOptiBackups.Contains(name, StringComparer.OrdinalIgnoreCase)) m.UserOptiBackups.Add(name);
                }
                else if (m.Backups.TryGetValue(name, out var b) && !m.UserOptiBackups.Contains(name, StringComparer.OrdinalIgnoreCase)
                         && (!File.Exists(b) || OptiDetector.IsOptiScaler(b))) m.Backups.Remove(name);
                removed.Add(name);
                Log.Info($"Removed another OptiScaler copy {name} from {m.TargetDir} (kept in {keep})");
            }
            catch (Exception ex) { Log.Error($"Could not remove the other OptiScaler copy {path}", ex); }
        }
        return removed;
    }
}

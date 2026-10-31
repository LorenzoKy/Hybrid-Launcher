using System.Diagnostics;

namespace HybridLauncher.Core.Detection;

public static class GameScanner
{
    /// <summary>Scans every enabled source, removes duplicates (same install folder) and locates each main exe.</summary>
    public static async Task<List<Game>> ScanAsync(AppSettings settings, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var sources = new List<(string Name, Func<IEnumerable<Game>> Scan)>();
        if (settings.ScanSteam) sources.Add(("Steam", SteamScanner.Scan));
        if (settings.ScanEpic) sources.Add(("Epic", EpicScanner.Scan));
        if (settings.ScanGog) sources.Add(("GOG", GogScanner.Scan));
        if (settings.ScanXbox) sources.Add(("Xbox", XboxScanner.Scan));
        var libraries = settings.GameLibraries.ToList();
        var customs = settings.CustomGames.ToList();
        var directs = settings.DirectInstalls.ToList();
        sources.Add(("Hybrid Launcher installs", () => ScanDirect(directs)));
        sources.Add(("Libraries", () => ScanLibraries(libraries)));
        sources.Add(("Custom", () => ScanCustom(customs)));

        var results = await Task.WhenAll(sources.Select(s => Task.Run(() =>
        {
            try { return s.Scan().ToList(); }
            catch (Exception ex)
            {
                Log.Error($"{s.Name} scan failed", ex);
                return [];
            }
        }, ct)));

        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var games = new List<Game>();
        foreach (var g in results.SelectMany(r => r))
        {
            var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(g.InstallDir));
            if (!seenIds.Add(g.Id)) continue;
            if (g.Store != GameStore.Custom && !seenDirs.Add(dir)) continue;
            games.Add(g);
        }

        await Parallel.ForEachAsync(games, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, (g, _) =>
        {
            g.MainExe ??= ExeLocator.Locate(g.InstallDir, g.StoreExe, g.Name);
            // A game run through a Steam emulator carries the Steam app id it was set up with: its pages are found by it.
            if (g.Store != GameStore.Steam && Steam.SteamEmulator.SettingsDir(g.InstallDir, g.MainExe) is { } emu)
            {
                g.EmulatorSettingsDir = emu;
                g.EmulatorAppId = Steam.SteamEmulator.AppId(emu);
            }
            return ValueTask.CompletedTask;
        });

        // A library sub-folder without any exe is not a game.
        games.RemoveAll(g => g.Id.StartsWith("lib:", StringComparison.Ordinal) && g.MainExe is null);
        games.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        Log.Info($"Scan found {games.Count} games in {sw.ElapsedMilliseconds} ms");
        return games;
    }

    static IEnumerable<Game> ScanLibraries(List<string> libraries)
    {
        foreach (var lib in libraries)
        {
            string[] dirs;
            try { dirs = Directory.GetDirectories(lib); } catch { continue; }
            foreach (var dir in dirs)
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('.') || name.StartsWith('$')) continue;
                yield return new Game
                {
                    Id = $"lib:{Path.GetFullPath(dir).ToLowerInvariant()}",
                    Name = name,
                    Store = GameStore.Custom,
                    InstallDir = dir,
                    LibraryRoot = lib,
                };
            }
        }
    }

    /// <summary>Games Hybrid Launcher downloaded itself: still there when their folder is.</summary>
    static IEnumerable<Game> ScanDirect(List<DirectInstall> installs)
    {
        foreach (var d in installs)
        {
            if (!Directory.Exists(d.InstallDir)) continue;
            string? exe = null;
            try { exe = d.Exe is { Length: > 0 } ? Accounts.DirectDownloader.Full(d.InstallDir, d.Exe) : null; } catch { }
            // A download still going in the folder hides the game, unless it is an update of a game already there (its exe is).
            if (Accounts.DirectDownloader.HasState(d.InstallDir) && (exe is null || !File.Exists(exe))) continue;
            yield return new Game
            {
                Id = d.Id,
                Name = d.Name,
                Store = d.Store,
                InstallDir = d.InstallDir,
                StoreExe = exe is not null && File.Exists(exe) ? exe : null,
                SteamAppId = d.Store == GameStore.Steam && d.Id.StartsWith("steam:", StringComparison.OrdinalIgnoreCase) ? d.Id[6..] : null,
                Direct = true,
            };
        }
    }

    static IEnumerable<Game> ScanCustom(List<CustomGame> customs)
    {
        foreach (var c in customs)
        {
            if (string.IsNullOrWhiteSpace(c.ExePath)) continue;
            var exe = Path.GetFullPath(c.ExePath);
            yield return new Game
            {
                Id = CustomId(exe),
                Name = string.IsNullOrWhiteSpace(c.Name) ? NameFromExe(exe) : c.Name!,
                Store = GameStore.Custom,
                InstallDir = Path.GetDirectoryName(exe)!,
                StoreExe = exe,
                MainExe = exe,
            };
        }
    }

    public static string CustomId(string exePath) => $"custom:{Path.GetFullPath(exePath).ToLowerInvariant()}";

    public static string NameFromExe(string exe)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            var product = info.ProductName?.Trim();
            if (!string.IsNullOrEmpty(product) && product.Length < 60) return product;
        }
        catch { }
        return Path.GetFileNameWithoutExtension(exe);
    }

    /// <summary>The folder OptiScaler goes into: the user's exe override, else the detected main exe's folder.</summary>
    public static string? TargetDir(Game game, GameSettings settings)
    {
        var exe = EffectiveExe(game, settings);
        return exe is null ? null : Path.GetDirectoryName(exe);
    }

    public static string? EffectiveExe(Game game, GameSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ExeOverride) && File.Exists(settings.ExeOverride)) return settings.ExeOverride;
        return game.MainExe;
    }
}

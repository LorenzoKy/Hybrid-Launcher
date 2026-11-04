using System.Reflection;

namespace HybridLauncher.Core.Ini;

/// <summary>
/// The keys of the official OptiScaler.ini (OptiScalerKeys.txt, "Section/Key" per line, from the official repository's file;
/// refresh it with Tools\gaming\list-ini-keys.py). Keys outside it come from a fork or a test build: the settings tab shows them apart.
/// </summary>
public static class OptiScalerKeys
{
    static readonly Lazy<HashSet<string>> Keys = new(() =>
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HybridLauncher.Core.Ini.OptiScalerKeys.txt");
            if (stream is null) return set;
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
                if (line.Trim() is { Length: > 0 } key) set.Add(key);
        }
        catch (Exception ex) { Log.Warn($"Official OptiScaler keys: {ex.Message}"); }
        return set;
    });

    /// <summary>True when the list could not be read too: nothing is then shown as a fork's own.</summary>
    public static bool IsOfficial(string section, string key) => Keys.Value.Count == 0 || Keys.Value.Contains($"{section}/{key}");
}

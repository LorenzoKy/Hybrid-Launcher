namespace HybridLauncher.Core.Accounts;

/// <summary>
/// Epic games that come in two separate versions under one name (2.1). Epic sells Grand Theft Auto V as two apps: "Grand
/// Theft Auto V Enhanced" and the older one, still titled just "Grand Theft Auto V" in Epic's catalog, with the same art. In
/// the library they read as the same game twice (and only Enhanced has achievements on Epic: Legacy keeps them in Rockstar's
/// Social Club). The older one is named "… Legacy", as Rockstar and Steam call it, so the two can be told apart and each
/// finds its own art.
/// </summary>
public static class EpicTitles
{
    // Epic app names of the older version of a game whose newer one Epic sells beside it.
    static readonly Dictionary<string, string> Legacy = new(StringComparer.OrdinalIgnoreCase)
    {
        ["9d2d0eb64d5c44529cece33fe2a46482"] = "Grand Theft Auto V Legacy",
    };

    // Epic apps whose achievements live in the publisher's own service, not on Epic (Epic answers "none" for them).
    static readonly Dictionary<string, string> AchievementsAt = new(StringComparer.OrdinalIgnoreCase)
    {
        ["9d2d0eb64d5c44529cece33fe2a46482"] = "Rockstar Games Social Club",
    };

    /// <summary>
    /// For an Epic game (id "epic:&lt;app&gt;") whose achievements its publisher keeps in its own service: that service's name.
    /// Hybrid Launcher cannot read those, so the game page says where they are instead of showing nothing.
    /// </summary>
    public static string? AchievementsElsewhere(string gameId) =>
        gameId.StartsWith("epic:", StringComparison.OrdinalIgnoreCase) && AchievementsAt.TryGetValue(gameId[5..], out var at) ? at : null;

    /// <summary>The name to show for an Epic app: the Legacy name for the known older versions, else Epic's own title.</summary>
    public static string Name(string appName, string title) =>
        Legacy.TryGetValue(appName, out var name) && !title.Contains("Legacy", StringComparison.OrdinalIgnoreCase) ? name : title;

    /// <summary>
    /// A list of owned Epic games where one title is another's with " Enhanced" after it: the shorter one is the older
    /// version and gets " Legacy" (games Epic may split the same way later, beside the known ones above).
    /// </summary>
    public static List<OwnedGame> Disambiguate(List<OwnedGame> games)
    {
        var titles = new HashSet<string>(games.Select(g => g.Name), StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < games.Count; i++)
        {
            var g = games[i];
            var app = g.Id.StartsWith("epic:", StringComparison.Ordinal) ? g.Id[5..] : g.Id;
            var name = Name(app, g.Name);
            if (name == g.Name && titles.Contains(g.Name + " Enhanced")) name = g.Name + " Legacy";
            // Its own art: Epic shows the newer version's picture for both; found by the new name instead (OwnedCovers).
            if (name != g.Name) games[i] = g with { Name = name, Cover = null, Wide = null };
        }
        return games;
    }
}

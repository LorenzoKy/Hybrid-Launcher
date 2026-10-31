using System.Text.RegularExpressions;
using HybridLauncher.Core.Detection;

namespace HybridLauncher.Core.Media;

/// <summary>
/// The names a game is looked up by. Stores decorate names ("Batman™: Arkham Knight", "BioShock Infinite Complete Edition -
/// Amazon Prime") that wikis and search boxes do not know; and a loose match on a short name finds the wrong game
/// ("Control" → "CONTROL Resonant"). Every lookup by name goes through here, and through the page the user picked when
/// Hybrid Launcher matched the wrong one (<see cref="GameSettings.PageAppId"/>).
/// </summary>
public static partial class GameNames
{
    /// <summary>
    /// The store name without trademark signs (also written "(TM)", or garbled as "â„¢" by a wrong code page), store prefixes
    /// ("Amazon Prime Gaming - …"), curly quotes and doubled spaces: ready for a search box.
    /// </summary>
    public static string Clean(string name)
    {
        var s = Marks().Replace(name, "");
        s = s.Replace('’', '\'').Replace('‘', '\'').Replace('“', '"').Replace('”', '"').Replace(' ', ' ');
        s = StorePrefix().Replace(s, "");
        return Spaces().Replace(s, " ").Trim();
    }

    /// <summary>
    /// The game's plain name: without what stores add to it (store tags, "(PC)", editions), so "CONTROL Ultimate Edition"
    /// and "Control" are the same game. Null when there is nothing to take away.
    /// </summary>
    public static string? Plain(string name)
    {
        var clean = Clean(name);
        var s = clean;
        // Edition words can follow a store tag ("… Deluxe Edition - Amazon Prime"): strip until nothing changes.
        for (int i = 0; i < 3; i++)
        {
            var before = s;
            s = StoreTag().Replace(s, "");
            s = Brackets().Replace(s, "");
            s = Edition().Replace(s, "");
            s = s.Trim(' ', '-', ':', '–', '—');
            if (s == before) break;
        }
        return s.Length >= 2 && !s.Equals(clean, StringComparison.OrdinalIgnoreCase) ? s : null;
    }

    /// <summary>The plain name, or the clean one when there is nothing to take away.</summary>
    public static string PlainOrClean(string name) => Plain(name) ?? Clean(name);

    /// <summary>Letters and digits only, lower case, of the plain name: two names are the same game when these match.</summary>
    public static string Key(string name) => ExeLocator.Normalize(PlainOrClean(name));

    /// <summary>
    /// Whether a name found on a site is the same game as <paramref name="ours"/>: the same letters, or the same once both
    /// lose their edition words. A name that merely starts the same ("Control" / "CONTROL Resonant") is another game.
    /// </summary>
    public static bool SameGame(string ours, string theirs)
    {
        var a = ExeLocator.Normalize(ours);
        var b = ExeLocator.Normalize(theirs);
        if (a.Length == 0 || b.Length == 0) return false;
        return a == b || Key(ours) == Key(theirs);
    }

    /// <summary>
    /// Whether <paramref name="theirs"/> is <paramref name="ours"/> plus a remaster word ("Yakuza 5" → "Yakuza 5 Remastered",
    /// "Resident Evil 4" → "Resident Evil 4 Remake"). Remasters, remakes and Game of the Year editions keep those words in
    /// <see cref="Plain"/>: wikis and stores list them as games of their own.
    /// </summary>
    public static bool IsRemasterOf(string theirs, string ours)
    {
        var a = ExeLocator.Normalize(Clean(ours));
        var b = ExeLocator.Normalize(Clean(theirs));
        if (a.Length < 2 || !b.StartsWith(a, StringComparison.Ordinal) || b.Length == a.Length) return false;
        return b[a.Length..] is "remastered" or "remasterededition" or "remaster" or "remake" or "hd" or "hdremaster" or "hdremastered"
            or "redux" or "reloaded" or "definitiveedition" or "gameoftheyearedition" or "goty" or "gotyedition";
    }

    /// <summary>The name to look the game up by: the one the user set with the page, else the store name cleaned.</summary>
    public static string LookupName(Game game) =>
        SettingsStore.Current.Games.TryGetValue(game.Id, out var s) && !string.IsNullOrWhiteSpace(s.PageName) ? s.PageName! : Clean(game.Name);

    /// <summary>
    /// The page the user picked for the game: an app id, "" when they said it has no store page, null when Hybrid Launcher matches it.
    /// </summary>
    public static string? PickedAppId(Game game) =>
        SettingsStore.Current.Games.TryGetValue(game.Id, out var s) && !string.IsNullOrWhiteSpace(s.PageAppId)
            ? (s.PageAppId == "none" ? "" : s.PageAppId) : null;

    [GeneratedRegex(@"[™®©℠]|â„¢|Â[®©]|\s?\((TM|R|C)\)|\s?\[(TM|R)\]", RegexOptions.IgnoreCase)]
    private static partial Regex Marks();

    [GeneratedRegex(@"^(Amazon\s+)?(Prime\s+Gaming|Amazon\s+Prime|Amazon\s+Games)\s*[-–—:|]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex StorePrefix();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"(\s*[-–—:]\s*|\s+)(Amazon Prime( Gaming)?|Amazon Games|Prime Gaming|Epic Games( Store)?|GOG|Steam|Windows( 10| 11)?|PC|Microsoft Store|Xbox)( Edition| Version)?$|\s+for Windows( 10| 11)?$", RegexOptions.IgnoreCase)]
    private static partial Regex StoreTag();

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]\s*$")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"[:\s-]+(The\s+)?(Complete|Definitive|Ultimate|Deluxe|Digital Deluxe|Gold|Premium|Enhanced|Standard|Game of the Year|GOTY|Anniversary|Director'?s Cut|Collector'?s)(\s+Edition)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Edition();
}

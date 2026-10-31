using Microsoft.Win32;

namespace HybridLauncher.Core;

/// <summary>Where the store chip on the banner leads: the game in its launcher's library when possible, its store page otherwise.</summary>
public static class StoreLinks
{
    /// <summary>The link for the game, and what it opens. Null when there is nothing to open (a game added by hand with no Steam match).</summary>
    public static (string Uri, string What)? For(Game game, string? steamAppId)
    {
        switch (game.Store)
        {
            case GameStore.Steam when game.SteamAppId is { } id:
                return GameLauncher.HasProtocol("steam")
                    ? ($"steam://nav/games/details/{id}", L.T("Open this game in your Steam library"))
                    : ($"https://store.steampowered.com/app/{id}", L.T("Open this game's Steam store page"));

            case GameStore.Gog when game.Id.StartsWith("gog:", StringComparison.Ordinal):
                var gogId = game.Id["gog:".Length..];
                return GameLauncher.HasProtocol("goggalaxy")
                    ? ($"goggalaxy://openGameView/{gogId}", L.T("Open this game in GOG Galaxy"))
                    : ($"https://www.gog.com/en/games?query={Uri.EscapeDataString(game.Name)}", L.T("Find this game on the GOG store"));

            case GameStore.Epic:
                return ($"https://store.epicgames.com/browse?q={Uri.EscapeDataString(game.Name)}&sortBy=relevancy", L.T("Find this game on the Epic Games Store"));

            case GameStore.Xbox when game.XboxAumid?.Split('!')[0] is { Length: > 0 } pfn:
                // Resolved to the Xbox app's own page when the link is opened (see OpenAsync); the Store page is the fallback.
                return GameLauncher.HasProtocol("msxbox")
                    ? ($"{XboxPrefix}{pfn}", L.T("Open this game in the Xbox app"))
                    : ($"ms-windows-store://pdp/?PFN={pfn}", L.T("Open this game in the Microsoft Store"));
        }
        return steamAppId is not null ? ($"https://store.steampowered.com/app/{steamAppId}", L.T("Open the Steam store page for this game")) : null;
    }

    public static string Describe(Game game, string? steamAppId) => For(game, steamAppId)?.What ?? L.T("Added by you");

    const string XboxPrefix = "xbox-pfn:";

    /// <summary>Opens the link; returns it, or null when the game has none. Xbox games open on their page in the Xbox app.</summary>
    public static async Task<string?> OpenAsync(Game game, string? steamAppId, CancellationToken ct = default)
    {
        if (For(game, steamAppId) is not { } link) return null;
        var uri = link.Uri;
        if (uri.StartsWith(XboxPrefix, StringComparison.Ordinal))
        {
            var pfn = uri[XboxPrefix.Length..];
            uri = await XboxProductIdAsync(pfn, ct) is { } id ? $"msxbox://game/?productId={id}" : $"ms-windows-store://pdp/?PFN={pfn}";
        }
        GameLauncher.OpenUri(uri);
        return uri;
    }

    /// <summary>The Store product id ("9NBLGGH2JHXJ") of a package family, from Microsoft's public catalog (cached).</summary>
    public static async Task<string?> XboxProductIdAsync(string pfn, CancellationToken ct = default)
    {
        try
        {
            var json = await Net.Http.GetCachedAsync(
                $"https://displaycatalog.mp.microsoft.com/v7.0/products/lookup?alternateId=PackageFamilyName&value={Uri.EscapeDataString(pfn)}&market=US&languages=en-us",
                Path.Combine("xbox", $"pfn-{string.Concat(pfn.Split(Path.GetInvalidFileNameChars()))}.json"), TimeSpan.FromDays(60), ct: ct);
            if (json is null) return null;
            var m = System.Text.RegularExpressions.Regex.Match(json, @"""ProductId""\s*:\s*""([A-Z0-9]{12})""");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Xbox product id for {pfn}: {ex.Message}");
            return null;
        }
    }

}

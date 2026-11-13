using Microsoft.UI.Xaml.Media;

namespace HybridLauncher.Helpers;

/// <summary>
/// The game page's banner pictures (hero, logo, the floor's mirror) of the games shown last, decoded and kept in memory for the
/// session (2.0.0-rc.4): going back to a game puts them on screen in the first frame, instead of reading and decoding a
/// 3840 px picture again and fading it in. The oldest go first past <see cref="Max"/> entries (about eight games' worth;
/// a decoded hero is 10-20 MB). UI thread only.
/// </summary>
public static class ArtCache
{
    const int Max = 30;

    static readonly Dictionary<string, (ImageSource Image, long Used, bool Opened)> Items = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string key)
    {
        if (!Items.TryGetValue(key, out var item)) return null;
        Items[key] = item with { Used = System.Diagnostics.Stopwatch.GetTimestamp() };
        return item.Image;
    }

    public static void Put(string key, ImageSource image)
    {
        Items[key] = (image, System.Diagnostics.Stopwatch.GetTimestamp(), false);
        while (Items.Count > Max) Items.Remove(Items.MinBy(i => i.Value.Used).Key);
    }

    /// <summary>The picture loaded fine (a logo is shown only then).</summary>
    public static void MarkOpened(string key)
    {
        if (Items.TryGetValue(key, out var item)) Items[key] = item with { Opened = true };
    }

    public static bool Opened(string key) => Items.TryGetValue(key, out var item) && item.Opened;

    /// <summary>A new look or a cleared cache: the pictures are read again.</summary>
    public static void Clear() => Items.Clear();
}

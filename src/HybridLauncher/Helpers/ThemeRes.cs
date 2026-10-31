using Microsoft.UI.Xaml;

namespace HybridLauncher.Helpers;

/// <summary>
/// The app's resources as the window's theme sees them. Application.Current.Resources["X"] answers for the theme the app
/// started in (Windows' own), not for the theme picked in Hybrid Launcher: with Windows dark and Hybrid Launcher light, every brush read in code
/// came out of the dark set (light text on light cards). This looks the key up in the picked theme's dictionaries first.
/// Use <see cref="Current"/> wherever code reads a theme brush; XAML's {ThemeResource} already follows the element.
/// </summary>
public sealed class ThemeRes
{
    public static readonly ThemeRes Current = new();

    /// <summary>The window's actual theme, kept up to date by the main window.</summary>
    public static ElementTheme Theme { get; set; } = ElementTheme.Default;

    /// <summary>
    /// Raised after the theme, palette or Material colours were applied. Controls that paint themselves in code listen to it:
    /// a change that ends in the same light / dark theme raises no ActualThemeChanged, so they would keep the old colours.
    /// </summary>
    public static event Action? Applied;

    public static void RaiseApplied() => Applied?.Invoke();

    public object this[string key] => TryGetValue(key, out var v) ? v : Application.Current.Resources[key];

    public bool TryGetValue(string key, out object value)
    {
        var app = Application.Current;
        bool appLight = app.RequestedTheme == ApplicationTheme.Light;
        // Same theme as the app started in (or not known yet): WinUI's own answer is the right one.
        if (Theme == ElementTheme.Default || (Theme == ElementTheme.Light) == appLight) return app.Resources.TryGetValue(key, out value);
        if (Find(app.Resources, key, Theme == ElementTheme.Light, out value)) return true;
        return app.Resources.TryGetValue(key, out value);
    }

    /// <summary>The theme dictionary of each dictionary, the merged ones last added first (the order WinUI resolves them in).</summary>
    static bool Find(ResourceDictionary dict, string key, bool light, out object value)
    {
        if (dict.ThemeDictionaries.TryGetValue(light ? "Light" : "Dark", out var t) && t is ResourceDictionary themed && themed.TryGetValue(key, out value!)
            || !light && dict.ThemeDictionaries.TryGetValue("Default", out t) && t is ResourceDictionary fallback && fallback.TryGetValue(key, out value!))
            return true;
        for (int i = dict.MergedDictionaries.Count - 1; i >= 0; i--)
            if (Find(dict.MergedDictionaries[i], key, light, out value)) return true;
        value = null!;
        return false;
    }
}

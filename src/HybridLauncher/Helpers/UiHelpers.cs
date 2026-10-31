using HybridLauncher.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HybridLauncher.Helpers;

/// <summary>Runs something once after a delay on a UI thread.</summary>
public static class Later
{
    // Timers are held here until they fire: one nobody holds can be collected before it ticks (a page that allocates a lot
    // while loading triggers that), and its action then never runs.
    static readonly HashSet<Microsoft.UI.Dispatching.DispatcherQueueTimer> Pending = [];

    public static void Run(Microsoft.UI.Dispatching.DispatcherQueue queue, TimeSpan delay, Action action)
    {
        var timer = queue.CreateTimer();
        timer.Interval = delay;
        timer.IsRepeating = false;
        Pending.Add(timer);
        timer.Tick += (t, _) =>
        {
            t.Stop();
            Pending.Remove(t);
            action();
        };
        timer.Start();
    }
}

/// <summary>Monochrome store logos (Steam, Epic and GOG paths from Simple Icons, CC0) that follow the text colour.</summary>
public static class StoreLogos
{
    const string Steam = "M 11.979 0 C 5.678 0 .511 4.86 .022 11.037 l 6.432 2.658 c .545 -.371 1.203 -.59 1.912 -.59 .063 0 .125 .004 .188 .006 l 2.861 -4.142 V 8.91 c 0 -2.495 2.028 -4.524 4.524 -4.524 2.494 0 4.524 2.031 4.524 4.527 s -2.03 4.525 -4.524 4.525 h -.105 l -4.076 2.911 c 0 .052 .004 .105 .004 .159 0 1.875 -1.515 3.396 -3.39 3.396 -1.635 0 -3.016 -1.173 -3.331 -2.727 L .436 15.27 C 1.862 20.307 6.486 24 11.979 24 c 6.627 0 11.999 -5.373 11.999 -12 S 18.605 0 11.979 0 z M 7.54 18.21 l -1.473 -.61 c .262 .543 .714 .999 1.314 1.25 1.297 .539 2.793 -.076 3.332 -1.375 .263 -.63 .264 -1.319 .005 -1.949 s -.75 -1.121 -1.377 -1.383 c -.624 -.26 -1.29 -.249 -1.878 -.03 l 1.523 .63 c .956 .4 1.409 1.5 1.009 2.455 -.397 .957 -1.497 1.41 -2.454 1.012 H 7.54 z m 11.415 -9.303 c 0 -1.662 -1.353 -3.015 -3.015 -3.015 -1.665 0 -3.015 1.353 -3.015 3.015 0 1.665 1.35 3.015 3.015 3.015 1.663 0 3.015 -1.35 3.015 -3.015 z m -5.273 -.005 c 0 -1.252 1.013 -2.266 2.265 -2.266 1.249 0 2.266 1.014 2.266 2.266 0 1.251 -1.017 2.265 -2.266 2.265 -1.253 0 -2.265 -1.014 -2.265 -2.265 z";

    const string Epic = "M3.537,0 C2.165,0 1.66,0.506 1.66,1.879 L1.66,18.44 C1.659,18.585 1.666,18.729 1.68,18.873 C1.711,19.173 1.717,19.463 1.996,19.793 C2.023,19.826 2.307,20.038 2.307,20.038 C2.46,20.113 2.565,20.168 2.737,20.238 L11.072,23.729 C11.505,23.928 11.686,24.005 12,23.999 L12.002,23.999 C12.316,24.005 12.497,23.928 12.93,23.729 L21.265,20.237 C21.437,20.167 21.542,20.113 21.695,20.037 C21.695,20.037 21.979,19.826 22.006,19.794 C22.286,19.464 22.291,19.173 22.322,18.874 C22.336,18.73 22.343,18.585 22.342,18.44 L22.342,1.879 C22.342,0.506 21.836,-0.001 20.464,-0.001 L3.537,0 Z M16.903,3.11 L17.583,3.11 C18.721,3.11 19.271,3.663 19.271,4.806 L19.271,6.686 L17.897,6.686 L17.897,4.886 C17.897,4.517 17.727,4.346 17.374,4.346 L17.139,4.346 C16.772,4.346 16.602,4.516 16.602,4.885 L16.602,10.695 C16.602,11.064 16.772,11.235 17.139,11.235 L17.401,11.235 C17.754,11.235 17.924,11.064 17.924,10.695 L17.924,8.619 L19.297,8.619 L19.297,10.762 C19.297,11.906 18.735,12.472 17.597,12.472 L16.903,12.472 C15.765,12.472 15.203,11.906 15.203,10.762 L15.203,4.82 C15.203,3.676 15.765,3.111 16.903,3.111 L16.903,3.11 Z M4.717,3.19 L7.831,3.19 L7.831,4.464 L6.117,4.464 L6.117,7.067 L7.765,7.067 L7.765,8.342 L6.117,8.342 L6.117,11.116 L7.857,11.116 L7.857,12.391 L4.717,12.391 L4.717,3.19 Z M8.533,3.19 L10.731,3.19 C11.869,3.19 12.431,3.754 12.431,4.898 L12.431,7.343 C12.431,8.487 11.869,9.053 10.731,9.053 L9.932,9.053 L9.932,12.391 L8.532,12.391 L8.533,3.19 Z M13.063,3.19 L14.463,3.19 L14.463,12.391 L13.063,12.391 L13.063,3.19 Z M9.933,4.425 L9.933,7.817 L10.508,7.817 C10.862,7.817 11.031,7.646 11.031,7.277 L11.031,4.965 C11.031,4.597 10.861,4.425 10.508,4.425 L9.933,4.425 Z M6.193,14.572 C6.395,14.573 6.595,14.609 6.784,14.68 C6.963,14.752 7.128,14.853 7.274,14.979 L6.822,15.525 C6.729,15.445 6.626,15.38 6.514,15.33 C6.399,15.283 6.275,15.26 6.151,15.262 C6.054,15.261 5.959,15.282 5.871,15.322 C5.786,15.361 5.71,15.416 5.647,15.485 C5.582,15.556 5.531,15.638 5.496,15.728 C5.458,15.823 5.439,15.925 5.44,16.027 L5.44,16.035 C5.439,16.141 5.458,16.246 5.496,16.345 C5.531,16.437 5.584,16.52 5.653,16.59 C5.721,16.658 5.802,16.713 5.891,16.75 C5.987,16.789 6.09,16.809 6.194,16.808 C6.351,16.814 6.505,16.774 6.639,16.692 L6.639,16.353 L6.091,16.353 L6.091,15.788 L7.37,15.788 L7.37,17.043 C7.212,17.171 7.035,17.275 6.846,17.35 C6.629,17.435 6.397,17.477 6.163,17.473 C5.957,17.475 5.753,17.439 5.561,17.366 C5.384,17.298 5.221,17.196 5.083,17.066 C4.947,16.938 4.839,16.783 4.765,16.611 C4.687,16.428 4.648,16.23 4.65,16.031 L4.65,16.023 C4.648,15.827 4.687,15.633 4.763,15.453 C4.836,15.281 4.942,15.125 5.075,14.993 C5.211,14.859 5.372,14.754 5.549,14.684 C5.739,14.609 5.942,14.571 6.147,14.573 C6.162,14.573 6.177,14.573 6.192,14.573 L6.193,14.572 Z M18.156,14.58 C18.364,14.579 18.57,14.611 18.768,14.674 C18.952,14.735 19.124,14.829 19.275,14.951 L18.889,15.497 C18.769,15.411 18.638,15.342 18.499,15.292 C18.375,15.247 18.243,15.223 18.111,15.222 C18.038,15.217 17.965,15.235 17.903,15.274 C17.86,15.302 17.834,15.35 17.833,15.401 L17.833,15.409 C17.832,15.439 17.84,15.468 17.855,15.493 C17.874,15.521 17.9,15.544 17.931,15.559 C17.978,15.584 18.027,15.604 18.078,15.619 C18.14,15.639 18.218,15.659 18.314,15.68 C18.46,15.711 18.604,15.752 18.744,15.802 C18.861,15.843 18.971,15.9 19.072,15.972 C19.159,16.035 19.23,16.117 19.279,16.212 C19.329,16.317 19.353,16.433 19.35,16.549 L19.35,16.557 C19.352,16.689 19.325,16.82 19.269,16.939 C19.215,17.05 19.137,17.147 19.04,17.224 C18.935,17.306 18.815,17.367 18.687,17.404 C18.537,17.446 18.382,17.467 18.227,17.465 C17.986,17.466 17.746,17.427 17.517,17.349 C17.298,17.275 17.096,17.157 16.924,17.003 L17.354,16.489 C17.631,16.712 17.932,16.824 18.254,16.824 C18.336,16.829 18.417,16.812 18.49,16.774 C18.542,16.746 18.574,16.691 18.572,16.632 L18.572,16.624 C18.572,16.597 18.565,16.57 18.552,16.547 C18.533,16.519 18.508,16.497 18.479,16.481 C18.434,16.455 18.386,16.435 18.336,16.419 C18.259,16.395 18.182,16.374 18.103,16.357 C17.964,16.325 17.826,16.288 17.69,16.244 C17.572,16.207 17.461,16.153 17.359,16.084 C17.267,16.021 17.191,15.938 17.137,15.841 C17.079,15.73 17.051,15.606 17.055,15.481 L17.055,15.473 C17.054,15.349 17.079,15.227 17.129,15.114 C17.178,15.005 17.251,14.908 17.343,14.831 C17.443,14.748 17.559,14.685 17.683,14.646 C17.828,14.6 17.979,14.578 18.131,14.58 C18.139,14.58 18.148,14.58 18.156,14.58 L18.156,14.58 Z M8.798,14.605 L9.54,14.605 L10.723,17.415 L9.898,17.415 L9.695,16.916 L8.623,16.916 L8.425,17.414 L7.615,17.414 L8.798,14.605 Z M10.995,14.625 L11.809,14.625 L12.472,15.705 L13.135,14.625 L13.949,14.625 L13.949,17.415 L13.183,17.415 L13.183,15.813 L12.472,16.904 L12.456,16.904 L11.749,15.821 L11.749,17.414 L10.995,17.414 L10.995,14.625 Z M14.464,14.625 L16.699,14.625 L16.699,15.283 L15.226,15.283 L15.226,15.705 L16.56,15.705 L16.56,16.315 L15.226,16.315 L15.226,16.757 L16.719,16.757 L16.719,17.415 L14.464,17.415 L14.464,14.625 Z M9.164,15.522 L8.849,16.315 L9.473,16.315 L9.164,15.522 Z M8.019,20.712 L16.033,20.712 L11.943,22.06 L8.019,20.712 Z";

    const string Gog = "M 7.15 15.24 H 4.36 a .4 .4 0 0 0 -.4 .4 v 2 c 0 .21 .18 .4 .4 .4 h 2.8 v 1.32 h -3.5 c -.56 0 -1.02 -.46 -1.02 -1.03 v -3.39 c 0 -.56 .46 -1.02 1.03 -1.02 h 3.48 v 1.32 z M 8.16 11.54 c 0 .58 -.47 1.05 -1.05 1.05 H 2.63 v -1.35 h 3.78 a .4 .4 0 0 0 .4 -.4 V 6.39 a .4 .4 0 0 0 -.4 -.4 H 4.39 a .4 .4 0 0 0 -.41 .4 v 2.02 c 0 .23 .18 .4 .4 .4 H 6 v 1.35 H 3.68 c -.58 0 -1.05 -.46 -1.05 -1.04 V 5.68 c 0 -.57 .47 -1.04 1.05 -1.04 H 7.1 c .58 0 1.05 .47 1.05 1.04 v 5.86 z M 21.36 19.36 h -1.32 v -4.12 h -.93 a .4 .4 0 0 0 -.4 .4 v 3.72 h -1.33 v -4.12 h -.93 a .4 .4 0 0 0 -.4 .4 v 3.72 h -1.33 v -4.42 c 0 -.56 .46 -1.02 1.03 -1.02 h 5.61 v 5.44 z M 21.37 11.54 c 0 .58 -.47 1.05 -1.05 1.05 h -4.48 v -1.35 h 3.78 a .4 .4 0 0 0 .4 -.4 V 6.39 a .4 .4 0 0 0 -.4 -.4 h -2.03 a .4 .4 0 0 0 -.4 .4 v 2.02 c 0 .23 .18 .4 .4 .4 h 1.62 v 1.35 H 16.9 c -.58 0 -1.05 -.46 -1.05 -1.04 V 5.68 c 0 -.57 .47 -1.04 1.05 -1.04 h 3.43 c .58 0 1.05 .47 1.05 1.04 v 5.86 z M 13.72 4.64 h -3.44 c -.58 0 -1.04 .47 -1.04 1.04 v 3.44 c 0 .58 .46 1.04 1.04 1.04 h 3.44 c .57 0 1.04 -.46 1.04 -1.04 V 5.68 c 0 -.57 -.47 -1.04 -1.04 -1.04 m -.3 1.75 v 2.02 a .4 .4 0 0 1 -.4 .4 h -2.03 a .4 .4 0 0 1 -.4 -.4 V 6.4 c 0 -.22 .17 -.4 .4 -.4 H 13 c .23 0 .4 .18 .4 .4 z M 12.63 13.92 H 9.24 c -.57 0 -1.03 .46 -1.03 1.02 v 3.39 c 0 .57 .46 1.03 1.03 1.03 h 3.39 c .57 0 1.03 -.46 1.03 -1.03 v -3.39 c 0 -.56 -.46 -1.02 -1.03 -1.02 m -.3 1.72 v 2 a .4 .4 0 0 1 -.4 .4 v -.01 H 9.94 a .4 .4 0 0 1 -.4 -.4 v -1.99 c 0 -.22 .18 -.4 .4 -.4 h 2 c .22 0 .4 .18 .4 .4 z M 23.49 1.1 a 1.74 1.74 0 0 0 -1.24 -.52 H 1.75 A 1.74 1.74 0 0 0 0 2.33 v 19.34 a 1.74 1.74 0 0 0 1.75 1.75 h 20.5 A 1.74 1.74 0 0 0 24 21.67 V 2.33 c 0 -.48 -.2 -.92 -.51 -1.24 m 0 20.58 a 1.23 1.23 0 0 1 -1.24 1.24 H 1.75 A 1.23 1.23 0 0 1 .5 21.67 V 2.33 a 1.23 1.23 0 0 1 1.24 -1.24 h 20.5 a 1.24 1.24 0 0 1 1.24 1.24 v 19.34 z";

    // Xbox: the official sphere (back in Simple Icons since 2025; Hybrid Launcher drew a ring with an X before).
    const string Xbox = "M 4.102 21.033 C 6.211 22.881 8.977 24 12 24 c 3.026 0 5.789 -1.119 7.902 -2.967 1.877 -1.912 -4.316 -8.709 -7.902 -11.417 -3.582 2.708 -9.779 9.505 -7.898 11.417 z m 11.16 -14.406 c 2.5 2.961 7.484 10.313 6.076 12.912 C 23.002 17.48 24 14.861 24 12.004 c 0 -3.34 -1.365 -6.362 -3.57 -8.536 0 0 -.027 -.022 -.082 -.042 -.063 -.022 -.152 -.045 -.281 -.045 -.592 0 -1.985 .434 -4.805 3.246 z M 3.654 3.426 c -.057 .02 -.082 .041 -.086 .042 C 1.365 5.642 0 8.664 0 12.004 c 0 2.854 .998 5.473 2.661 7.533 -1.401 -2.605 3.579 -9.951 6.08 -12.91 -2.82 -2.813 -4.216 -3.245 -4.806 -3.245 -.131 0 -.223 .021 -.281 .046 v -.002 z M 12 3.551 S 9.055 1.828 6.755 1.746 c -.903 -.033 -1.454 .295 -1.521 .339 C 7.379 .646 9.659 0 11.984 0 H 12 c 2.334 0 4.605 .646 6.766 2.085 -.068 -.046 -.615 -.372 -1.52 -.339 C 14.946 1.828 12 3.545 12 3.545 v .006 z";

    /// <summary>The logo at <paramref name="size"/> px, or a folder glyph for games added by hand.</summary>
    public static FrameworkElement Create(GameStore store, double size = 16)
    {
        // The launchers' current official marks at every size (Simple Icons 16.36), as their own apps and sites show them.
        var data = store switch
        {
            GameStore.Steam => Steam,
            GameStore.Epic => Epic,
            GameStore.Gog => Gog,
            GameStore.Xbox => Xbox,
            _ => null,
        };
        if (data is null) return new FontIcon { Glyph = "", FontSize = size };
        // The geometry string goes through XAML's converter (far cheaper than loading a XAML snippet for every cover). A PathIcon
        // takes the surrounding text colour, so the logo follows the theme; the paths are all 24 x 24, the Viewbox scales them.
        var icon = new PathIcon { Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data), Width = 24, Height = 24 };
        return new Viewbox { Width = size, Height = size, Child = icon };
    }

    public static string Label(GameStore store) => store switch
    {
        GameStore.Steam => "Steam",
        GameStore.Epic => "Epic Games",
        GameStore.Gog => "GOG",
        GameStore.Xbox => "Xbox / Game Pass",
        _ => L.T("Added by you"),
    };

    /// <summary>
    /// The list / grid group a game belongs to: its store, or for games found in a games folder that folder (named after it,
    /// one group per folder), after the stores; single exes added by hand come last.
    /// </summary>
    public static (int Order, string Key, string Label, string? Folder) GroupOf(Game game)
    {
        if (game.Store == GameStore.Custom && game.LibraryRoot is { } root)
        {
            var trimmed = Path.TrimEndingDirectorySeparator(root);
            var name = Path.GetFileName(trimmed);
            return ((int)GameStore.Custom, "lib:" + trimmed.ToLowerInvariant(), name.Length > 0 ? name : trimmed, root);
        }
        return ((int)game.Store + (game.Store == GameStore.Custom ? 1 : 0), "store:" + game.Store, Label(game.Store), null);
    }

    /// <summary>
    /// The launcher's logo in white on a small rounded tile in the launcher's own colours (Steam navy, Epic graphite, GOG violet,
    /// Xbox green; amber for games folders, slate for games added by hand): the group headings of the list and the library.
    /// </summary>
    public static Border Tile(GameStore store, bool folder = false, double size = 22)
    {
        var (top, bottom) = folder ? (C(0xF5, 0xC0, 0x3C), C(0xC2, 0x7E, 0x0C)) : store switch
        {
            GameStore.Steam => (C(0x2A, 0x5A, 0x80), C(0x17, 0x1D, 0x2A)),
            GameStore.Epic => (C(0x3A, 0x3A, 0x42), C(0x12, 0x12, 0x16)),
            GameStore.Gog => (C(0xB0, 0x4C, 0xD8), C(0x56, 0x18, 0x78)),
            GameStore.Xbox => (C(0x3C, 0xB8, 0x3C), C(0x0E, 0x6B, 0x0E)),
            _ => (C(0x76, 0x7E, 0x94), C(0x3A, 0x40, 0x52)),
        };
        // The logo fills about two thirds of the tile (Steam's round mark a little more: it has no corners to spare).
        double logo = size * (store == GameStore.Steam ? 0.7 : 0.62);
        FrameworkElement glyph = folder ? new FontIcon { Glyph = "\uE8B7", FontSize = size * 0.55 }
            : store == GameStore.Custom ? new FontIcon { Glyph = "\uE7FC", FontSize = size * 0.55 }
            : Create(store, logo);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        // The logo is drawn in the text colour: white on every tile, in both themes.
        var holder = new ContentControl { Content = glyph, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        // A squircle-ish tile like a Windows 11 app icon: the colour deepens top to bottom, a thin even hairline gives it an edge
        // on any background (no bright rim on top, which read as a bevel at 22 px).
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size * 0.3),
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0),
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops = { new GradientStop { Color = top, Offset = 0 }, new GradientStop { Color = Mix(top, bottom, 0.75), Offset = 1 } },
            },
            Child = holder,
        };

        static Color Mix(Color a, Color b, double t) => Color.FromArgb(0xFF, (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

        static Color C(byte r, byte g, byte b) => Color.FromArgb(0xFF, r, g, b);
    }

    /// <summary>A group heading: the launcher tile (a folder for games folders), the name in capitals and the count as a small pill.</summary>
    public static StackPanel GroupHeader(Game sample, int count, Style labelStyle)
    {
        var g = GroupOf(sample);
        var panel = Header(Tile(sample.Store, g.Folder is not null), g.Label, count, labelStyle);
        if (g.Folder is not null) ToolTipService.SetToolTip(panel, L.F("Games folder: {0}", g.Folder));
        return panel;
    }

    /// <summary>The heading of the games you own but have not installed (the list, with that filter on): a download tile.</summary>
    public static StackPanel NotInstalledHeader(int count, Style labelStyle)
    {
        var tile = Tile(GameStore.Custom);
        tile.Child = new FontIcon { Glyph = "\uE896", FontSize = 12, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        return Header(tile, L.T("Not installed"), count, labelStyle);
    }

    static StackPanel Header(Border tile, string label, int count, Style labelStyle)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
        panel.Children.Add(tile);
        panel.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), Style = labelStyle, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 1, 7, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)HybridLauncher.Helpers.ThemeRes.Current["ControlFillColorSecondaryBrush"],
            Child = new TextBlock { Text = count.ToString(), FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Opacity = 0.8 },
        });
        return panel;
    }
}

/// <summary>
/// Short tags for the upscalers and frame generation a game supports, in the maker's colour: Nvidia green (DLSS),
/// AMD red (FSR), Intel blue (XeSS). Frame generation tags blend that colour into a second shade, so they stand apart.
/// </summary>
public static class TechTags
{
    // Darker than the brand colours so white text stays readable on them.
    static readonly Color Nvidia = Color.FromArgb(0xFF, 0x4A, 0x8C, 0x00), NvidiaFg = Color.FromArgb(0xFF, 0x00, 0x86, 0x74);
    static readonly Color Amd = Color.FromArgb(0xFF, 0xC4, 0x12, 0x2E), AmdFg = Color.FromArgb(0xFF, 0xD8, 0x5A, 0x14);
    static readonly Color Intel = Color.FromArgb(0xFF, 0x00, 0x66, 0xB8), IntelFg = Color.FromArgb(0xFF, 0x5E, 0x3C, 0xD0);
    static readonly Color Epic = Color.FromArgb(0xFF, 0x4A, 0x50, 0x5C), Sony = Color.FromArgb(0xFF, 0x1F, 0x3F, 0x9A);
    static readonly Color Neutral = Color.FromArgb(0xFF, 0x3A, 0x36, 0x48);

    /// <summary>The maker's colour for a tag ("DLSS 4", "FSR FG"...), and the second shade frame generation blends into.</summary>
    public static (Color Main, Color Blend) VendorColors(string text)
    {
        var t = text.Trim().ToUpperInvariant();
        if (t.Contains("DLSS") || t.Contains("NVIDIA")) return (Nvidia, NvidiaFg);
        if (t.Contains("FSR") || t.Contains("AMD") || t.Contains("FIDELITYFX")) return (Amd, AmdFg);
        if (t.Contains("XESS") || t.Contains("XEFG") || t.Contains("INTEL")) return (Intel, IntelFg);
        if (t.Contains("TSR")) return (Epic, Epic);
        if (t.Contains("PSSR")) return (Sony, Sony);
        return (Neutral, Neutral);
    }

    /// <summary>True for frame generation tags ("DLSS FG", "DLSS MFG", "FSR FG", "XeSS FG").</summary>
    public static bool IsFrameGen(string text) => text.Contains(" FG", StringComparison.OrdinalIgnoreCase) || text.Contains("MFG", StringComparison.OrdinalIgnoreCase);

    /// <summary>A third of the way to white: the edge of a tag, the same hue as its fill but lighter, so it reads as one crisp shape.</summary>

    /// <summary>
    /// The text of a tag or chip: bounds cut to the capitals and the baseline, so even padding centres it exactly (the font's own
    /// line box puts it a pixel high, which reads as blur on small text).
    /// </summary>
    public static TextBlock Text(string text, double fontSize, Brush? foreground = null, int spacing = 30) => new()
    {
        Text = text,
        FontSize = fontSize,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        CharacterSpacing = spacing,
        TextLineBounds = TextLineBounds.Tight,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = foreground ?? new SolidColorBrush(Colors.White),
    };

    /// <summary>
    /// An upscaler / frame generation tag in its maker's colour. Nearly solid, so the art behind cannot bleed through and dirty it,
    /// with an edge in its own lighter hue (a white see-through edge blurs on fractional scaling); frame generation blends two shades.
    /// </summary>
    public static Border Create(string text, bool frameGen = false, double fontSize = 10)
    {
        frameGen |= IsFrameGen(text);
        var (main, blend) = VendorColors(text);
        Brush background = frameGen
            ? new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(0xE0, main.R, main.G, main.B), Offset = 0.15 },
                    new GradientStop { Color = Color.FromArgb(0xE0, blend.R, blend.G, blend.B), Offset = 1 },
                },
            }
            : new SolidColorBrush(Color.FromArgb(0xD8, main.R, main.G, main.B));
        // A plain fill, no rim (the 1 px it took goes to the padding, so the badge keeps its size).
        double v = Math.Round(fontSize * 0.36) + 1;
        return new Border
        {
            Background = background,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, v, 7, v),
            UseLayoutRounding = true,
            Child = Text(text, fontSize),
        };
    }
}

/// <summary>
/// Text on a coloured surface: white on deep colours, near-black on bright light ones (yellow, cyan, lime, amber, lavender),
/// where white would fade into the fill. Decided by the colour's relative luminance (WCAG), not by its hue.
/// </summary>
public static class Contrast
{
    static readonly Color Dark = Color.FromArgb(0xFF, 0x12, 0x12, 0x16);

    static double Linear(byte v)
    {
        double c = v / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// <summary>Relative luminance, 0 (black) to 1 (white).</summary>
    public static double Luminance(Color c) => 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

    /// <summary>White text would read below 3:1 on it (about where it starts to look washed out).</summary>
    public static bool IsLight(Color c) => Luminance(c) > 0.30;

    /// <summary>The text colour that reads on <paramref name="background"/>.</summary>
    public static Color On(Color background) => IsLight(background) ? Dark : Colors.White;

    public static SolidColorBrush BrushOn(Color background) => new(On(background));
}

/// <summary>
/// A colour palette layered on the light / dark theme: accent colours plus a faint tint behind the window.
/// A palette with a <see cref="Backdrop"/> is a full scene: always dark, an opaque gradient instead of Mica.
/// </summary>
public sealed record Palette(string Key, string Name, string Description, Color? Accent, Color? Tint, (Color Top, Color Bottom)? Backdrop = null)
{
    public bool ForcesDark => Backdrop is not null;
}

public static class Palettes
{
    public static readonly Palette[] All =
    [
        new("Windows", "Windows accent", "Your Windows accent colour", null, null),
        new("Hybrid", "Hybrid", "Crimson, like the logo's crossbar", C(0xD7, 0x14, 0x3A), C(0xA8, 0x0E, 0x2C)),
        new("NeonCity", "Neon city", "Cyan with a magenta haze", C(0x00, 0xD8, 0xE8), C(0xFF, 0x3B, 0xD8)),
        new("Toxic", "Toxic", "Acid green", C(0x3D, 0xDC, 0x5A), C(0x2B, 0xB0, 0x40)),
        new("Crimson", "Crimson", "Deep red", C(0xE5, 0x39, 0x50), C(0xC0, 0x18, 0x30)),
        new("Ocean", "Ocean", "Calm blue", C(0x2D, 0x9C, 0xDB), C(0x1A, 0x6F, 0xC0)),
        new("Amber", "Amber", "Warm orange", C(0xFF, 0xA5, 0x1F), C(0xE0, 0x70, 0x10)),
        new("Graphite", "Graphite", "Neutral grey, no colour", C(0x9A, 0xA4, 0xB0), null),
        // Scene themes: dark / grey backgrounds of their own, inspired by a console or game look.
        new("Cyberpunk", "Cyberpunk 2077", "Night city black, signal yellow and red (always dark)", C(0xFC, 0xEE, 0x0A), C(0xFF, 0x00, 0x3C), (C(0x16, 0x10, 0x14), C(0x07, 0x07, 0x09))),
        new("Mgs1", "Metal Gear Solid", "Shadow Moses: arctic night blue with codec green (always dark)", C(0x5C, 0xE0, 0x8C), C(0x2C, 0x6E, 0x9E), (C(0x10, 0x1A, 0x24), C(0x04, 0x08, 0x0C))),
        new("Mgs2", "Metal Gear Solid 2", "Sons of Liberty: Big Shell steel with cyan readouts (always dark)", C(0x4F, 0xC8, 0xF0), C(0x8E, 0xA4, 0xB8), (C(0x18, 0x1E, 0x26), C(0x08, 0x0A, 0x0E))),
        new("Custom", "My colours", "Your own accent, glow and background (Settings)", C(0x9A, 0x5C, 0xFF), C(0x8A, 0x4D, 0xFF)),
    ];

    /// <summary>"My colours": the accent, glow and (optionally) dark background picked in Settings.</summary>
    public static Palette Custom()
    {
        var s = AppState.Settings;
        var accent = ParseHex(s.CustomAccent) ?? C(0x9A, 0x5C, 0xFF);
        var tint = ParseHex(s.CustomTint) ?? accent;
        (Color, Color)? backdrop = null;
        if (s.CustomBackdrop && ParseHex(s.CustomBackground) is { } bg) backdrop = (bg, Mix(bg, Colors.Black, 0.55));
        return new Palette("Custom", "My colours", "Your own accent, glow and background (Settings)", accent, tint, backdrop);
    }

    public static Palette Get(string? key)
    {
        // The theme named after the app's old name became "Hybrid" with the rename (0.25).
        if (key == AppInfo.LegacyName) key = "Hybrid";
        // Scene themes dropped in 1.0 (PS5, Metal Gear Solid 3, Spider-Man): their dark look carries on as Cyberpunk 2077.
        if (key is "Ps5" or "MetalGear" or "SpiderMan") key = "Cyberpunk";
        return key == "Custom" ? Custom() : key is null or "Windows" ? WindowsAccent() : All.FirstOrDefault(p => p.Key == key) ?? WindowsAccent();
    }

    /// <summary>
    /// "Windows accent": WinUI already uses the Windows accent for controls; with material colours on, the window gets a soft
    /// wash of it too, so the backdrop matches the accent like the other themes do. Read each time: the user can change it in
    /// Windows settings. Material colours off: only the controls take the accent, the window stays Windows' own Mica.
    /// </summary>
    static Palette WindowsAccent()
    {
        if (!AppState.Settings.Material) return All[0];
        Color? tint = null;
        try { tint = new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent); }
        catch { }
        return All[0] with { Tint = tint };
    }

    public static Color? ParseHex(string? hex)
    {
        hex = hex?.Trim().TrimStart('#');
        if (hex is not { Length: 6 } || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v)) return null;
        return Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    static Color C(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);

    static ResourceDictionary? _applied;

    /// <summary>Puts the palette's accent brushes in front of the WinUI ones (a merged dictionary added last wins).</summary>
    public static void ApplyResources(Palette palette)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_applied is not null) merged.Remove(_applied);
        _applied = null;
        var tone = palette.Accent ?? palette.Tint;
        if (tone is not { } cardTone) return;
        // Material colours off: cards and outlines keep WinUI's neutral grey; only the accent brushes change.
        bool material = AppState.Settings.Material;

        var dict = new ResourceDictionary();
        foreach (var (theme, dark) in new[] { ("Dark", true), ("Light", false), ("Default", true) })
        {
            var t = new ResourceDictionary();
            // Cards and their outlines take a trace of the theme's colour, so every surface belongs to the theme
            // (WinUI's are neutral grey). Barely there: text contrast stays the same.
            if (material)
            {
            t["CardBackgroundFillColorDefaultBrush"] = new SolidColorBrush(dark
                ? Color.FromArgb(0x14, Mix(Colors.White, cardTone, 0.55).R, Mix(Colors.White, cardTone, 0.55).G, Mix(Colors.White, cardTone, 0.55).B)
                : Color.FromArgb(0xB8, Mix(Colors.White, cardTone, 0.05).R, Mix(Colors.White, cardTone, 0.05).G, Mix(Colors.White, cardTone, 0.05).B));
            // The rows inside cards take the same trace (a step lighter), so cards, rows and groups read as one material.
            t["LayerOnAcrylicFillColorDefaultBrush"] = new SolidColorBrush(dark
                ? Color.FromArgb(0x1A, Mix(Colors.White, cardTone, 0.55).R, Mix(Colors.White, cardTone, 0.55).G, Mix(Colors.White, cardTone, 0.55).B)
                : Color.FromArgb(0xC8, Mix(Colors.White, cardTone, 0.04).R, Mix(Colors.White, cardTone, 0.04).G, Mix(Colors.White, cardTone, 0.04).B));
            t["CardStrokeColorDefaultBrush"] = new SolidColorBrush(dark
                ? Color.FromArgb(0x24, Mix(Colors.White, cardTone, 0.35).R, Mix(Colors.White, cardTone, 0.35).G, Mix(Colors.White, cardTone, 0.35).B)
                : Color.FromArgb(0x1C, Mix(Colors.Black, cardTone, 0.5).R, Mix(Colors.Black, cardTone, 0.5).G, Mix(Colors.Black, cardTone, 0.5).B));
            }
            if (palette.Accent is not { } accent)
            {
                dict.ThemeDictionaries[theme] = t;
                continue;
            }
            // Dark theme: the lighter tints read on dark surfaces; light theme: the darker ones.
            var c1 = dark ? Mix(accent, Colors.White, 0.25) : Mix(accent, Colors.Black, 0.25);
            var c2 = dark ? Mix(accent, Colors.White, 0.45) : Mix(accent, Colors.Black, 0.40);
            var c3 = dark ? Mix(accent, Colors.White, 0.65) : Mix(accent, Colors.Black, 0.55);
            t["SystemAccentColor"] = accent;
            t["SystemAccentColorLight1"] = Mix(accent, Colors.White, 0.25);
            t["SystemAccentColorLight2"] = Mix(accent, Colors.White, 0.45);
            t["SystemAccentColorLight3"] = Mix(accent, Colors.White, 0.65);
            t["SystemAccentColorDark1"] = Mix(accent, Colors.Black, 0.25);
            t["SystemAccentColorDark2"] = Mix(accent, Colors.Black, 0.40);
            t["SystemAccentColorDark3"] = Mix(accent, Colors.Black, 0.55);
            t["AccentFillColorDefaultBrush"] = new SolidColorBrush(c1);
            t["AccentFillColorSecondaryBrush"] = new SolidColorBrush(Color.FromArgb(0xE6, c1.R, c1.G, c1.B));
            t["AccentFillColorTertiaryBrush"] = new SolidColorBrush(Color.FromArgb(0xCC, c1.R, c1.G, c1.B));
            t["AccentFillColorSelectedTextBackgroundBrush"] = new SolidColorBrush(accent);
            t["AccentTextFillColorPrimaryBrush"] = new SolidColorBrush(c3);
            t["AccentTextFillColorSecondaryBrush"] = new SolidColorBrush(c3);
            t["AccentTextFillColorTertiaryBrush"] = new SolidColorBrush(c2);
            t["TextOnAccentFillColorPrimaryBrush"] = Contrast.BrushOn(c1);
            t["TextOnAccentFillColorSecondaryBrush"] = new SolidColorBrush(Contrast.IsLight(c1) ? Color.FromArgb(0xB3, 0, 0, 0) : Color.FromArgb(0xB3, 255, 255, 255));
            t["SystemFillColorAttentionBrush"] = new SolidColorBrush(c1);
            t["NavigationViewSelectionIndicatorForeground"] = new SolidColorBrush(c1);
            t["HyperlinkButtonForeground"] = new SolidColorBrush(c3);
            t["HyperlinkButtonForegroundPointerOver"] = new SolidColorBrush(c2);
            AddAliases(t);
            dict.ThemeDictionaries[theme] = t;
        }
        merged.Add(dict);
        _applied = dict;
    }

    /// <summary>
    /// WinUI's control brushes (AccentButtonBackground, CheckBoxCheckBackgroundFillChecked, ToggleSwitchFillOn...) are
    /// StaticResource aliases of the accent brushes, resolved when WinUI loads, so overriding the accent keys alone does
    /// not reach them: each alias gets the palette's brush for its source key.
    /// </summary>
    static void AddAliases(ResourceDictionary target)
    {
        foreach (var (key, source) in AccentAliases.All)
            if (target.TryGetValue(source, out var brush)) target[key] = brush;
    }

    /// <summary>The window tint for the palette: a soft diagonal wash, stronger in the dark theme.</summary>
    public static Brush? TintBrush(Palette palette, bool dark)
    {
        if (palette.Backdrop is { } scene) return SceneBrush(palette, scene.Top, scene.Bottom);
        if (palette.Tint is not { } tint) return null;
        byte a = dark ? (byte)0x26 : (byte)0x14;
        var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1) };
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(a, tint.R, tint.G, tint.B), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, tint.R, tint.G, tint.B), Offset = 0.55 });
        var accent = palette.Accent ?? tint;
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)(a * 2 / 3), accent.R, accent.G, accent.B), Offset = 1 });
        return brush;
    }

    /// <summary>An opaque backdrop for scene themes: the palette's dark gradient, its tint glowing from the top-right corner.</summary>
    static Brush SceneBrush(Palette palette, Color top, Color bottom)
    {
        var tint = palette.Tint ?? top;
        var brush = new LinearGradientBrush { StartPoint = new(1, 0), EndPoint = new(0, 1) };
        brush.GradientStops.Add(new GradientStop { Color = Mix(top, tint, 0.16), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = top, Offset = 0.35 });
        brush.GradientStops.Add(new GradientStop { Color = bottom, Offset = 1 });
        return brush;
    }

    static Color Mix(Color a, Color b, double t) => Color.FromArgb(255,
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}

/// <summary>Icons the icon font does not have, drawn as paths so they look the same everywhere.</summary>
public static class Icons
{
    /// <summary>Whether icons are drawn in that place (Settings → Appearance → Icons; see <see cref="IconSpots"/>).</summary>
    public static bool Show(string spot) => IconSpots.Shown(AppState.Settings, spot);

    /// <summary>
    /// Hybrid Launcher's trophy on a 16 x 16 grid, in the PlayStation trophy's spirit: a solid cup with a flat rim and a rounded bowl, two
    /// loop handles, a short stem flaring into a foot, and a wide plinth under it. One filled silhouette (no cut-outs), so it
    /// reads at 12 px. The one achievements icon: play bar, Achievements tab, game overview, pop-ups and library covers alike.
    /// </summary>
    public const string TrophyPath =
        // Non-zero fill: the handles touch the cup without punching holes where they meet.
        "F1 " +
        // The cup: rim, bowl narrowing to the stem, the stem flaring into its foot.
        "M3.3,1.4 H12.7 V4.2 C12.7,7.4 10.8,9.4 8.85,9.8 V11.3 C8.85,11.7 9.4,12 10.4,12.2 L10.9,12.9 H5.1 L5.6,12.2 C6.6,12 7.15,11.7 7.15,11.3 V9.8 C5.2,9.4 3.3,7.4 3.3,4.2 Z " +
        // The plinth.
        "M3.6,13.4 H12.4 C12.75,13.4 13,13.65 13,14 V15 H3 V14 C3,13.65 3.25,13.4 3.6,13.4 Z " +
        // The handles: open loops from the rim down to the bowl's side.
        "M3.4,2.4 H1.9 C1.6,2.4 1.4,2.6 1.4,2.9 V4.3 C1.4,6.1 2.6,7.5 4.4,7.8 L4.05,6.6 C3.15,6.3 2.6,5.4 2.6,4.4 V3.6 H3.4 Z " +
        "M12.6,2.4 H14.1 C14.4,2.4 14.6,2.6 14.6,2.9 V4.3 C14.6,6.1 13.4,7.5 11.6,7.8 L11.95,6.6 C12.85,6.3 13.4,5.4 13.4,4.4 V3.6 H12.6 Z";

    /// <summary>Put in a tab's or a heading's glyph to draw the trophy there instead of a font glyph (see <see cref="Make"/>).</summary>
    public const string TrophyGlyph = "trophy";

    /// <summary>
    /// The icon for a glyph at a font size: a font glyph, or for <see cref="TrophyGlyph"/> the trophy path scaled to the same size
    /// (its geometry scaled, since a path icon clips rather than stretches), so both line up wherever a FontIcon went.
    /// </summary>
    public static IconElement Make(string glyph, double fontSize)
    {
        if (glyph != TrophyGlyph) return new FontIcon { Glyph = glyph, FontSize = fontSize };
        var size = Math.Round(fontSize * 1.15);
        var geometry = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), TrophyPath);
        geometry.Transform = new ScaleTransform { ScaleX = size / 16, ScaleY = size / 16 };
        return new PathIcon { Data = geometry, Width = size, Height = size };
    }

    /// <summary>
    /// A coloured icon for a glyph in lists that show each tab in its own colour: a font glyph in <paramref name="tint"/>, or for
    /// <see cref="TrophyGlyph"/> the shaded trophy (gold already), sized like the font glyph next to it.
    /// </summary>
    public static FrameworkElement Colored(string glyph, double fontSize, Color tint)
    {
        if (glyph == TrophyGlyph) return Trophy(Math.Round(fontSize * 1.05));
        return new FontIcon { Glyph = glyph, FontSize = fontSize, Foreground = new SolidColorBrush(tint), VerticalAlignment = VerticalAlignment.Center };
    }

    /// <summary>The achievements gold.</summary>
    public static readonly Color Gold = Color.FromArgb(0xFF, 0xFF, 0xD3, 0x4D);

    /// <summary>The trophy at any size, flat, in one colour: a path icon does not scale its geometry (it clips it), so the 16 px icon sits in a Viewbox.</summary>
    public static FrameworkElement Trophy(double size, Brush? foreground) => new Viewbox
    {
        Width = size,
        Height = size,
        VerticalAlignment = VerticalAlignment.Center,
        Child = new PathIcon
        {
            Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), TrophyPath),
            Width = 16,
            Height = 16,
            Foreground = foreground ?? new SolidColorBrush(Gold),
        },
    };

    // The parts of the trophy shaded on their own (same 16 x 16 grid as TrophyPath).
    const string CupPath = "M3.3,1.4 H12.7 V4.2 C12.7,7.4 10.8,9.4 8.85,9.8 V11.3 C8.85,11.7 9.4,12 10.4,12.2 L10.9,12.9 H5.1 L5.6,12.2 C6.6,12 7.15,11.7 7.15,11.3 V9.8 C5.2,9.4 3.3,7.4 3.3,4.2 Z";
    const string PlinthPath = "M3.6,13.4 H12.4 C12.75,13.4 13,13.65 13,14 V15 H3 V14 C3,13.65 3.25,13.4 3.6,13.4 Z";
    const string RimPath = "M3.3,1.4 H12.7 V2.25 H3.3 Z";
    const string ShinePath = "M4.5,2.7 H5.6 C5.6,5.1 5.9,6.9 6.7,8.3 C5.3,7.5 4.5,5.6 4.5,2.7 Z";
    const string SparklePath = "M13.6,0 L14.15,1.45 L15.6,2 L14.15,2.55 L13.6,4 L13.05,2.55 L11.6,2 L13.05,1.45 Z";

    /// <summary>
    /// The trophy with depth, as PlayStation draws its trophies: the metal lit from the left like a cylinder (light, then its
    /// colour, then deep at the right edge), a lit rim on top, a shine down the bowl, the bowl darkening towards the stem, the
    /// plinth shaded top to bottom and a thin dark outline. Gold for achievements; <paramref name="platinum"/> (every achievement
    /// unlocked) is a cool silver-blue with a sparkle, a different trophy at a glance.
    /// </summary>
    public static FrameworkElement Trophy(double size, bool platinum = false)
    {
        var (light, mid, dark, deep) = platinum
            ? (Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), Color.FromArgb(0xFF, 0xC9, 0xD6, 0xE8), Color.FromArgb(0xFF, 0x86, 0x99, 0xB4), Color.FromArgb(0xFF, 0x4E, 0x5C, 0x74))
            : (Color.FromArgb(0xFF, 0xFF, 0xF2, 0xB0), Color.FromArgb(0xFF, 0xF5, 0xC2, 0x42), Color.FromArgb(0xFF, 0xC0, 0x84, 0x14), Color.FromArgb(0xFF, 0x7C, 0x50, 0x08));
        static Geometry G(string d) => (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), d);
        static LinearGradientBrush Across(params (Color C, double At)[] stops)
        {
            var b = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 0) };
            foreach (var (c, at) in stops) b.GradientStops.Add(new GradientStop { Color = c, Offset = at });
            return b;
        }
        static LinearGradientBrush Down(params (Color C, double At)[] stops)
        {
            var b = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1) };
            foreach (var (c, at) in stops) b.GradientStops.Add(new GradientStop { Color = c, Offset = at });
            return b;
        }
        var clear = Color.FromArgb(0, 0, 0, 0);
        var grid = new Grid { Width = 16, Height = 16 };
        // The whole silhouette, lit from the left; its dark outline gives it an edge on any background.
        grid.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = G(TrophyPath),
            Fill = Across((dark, 0), (light, 0.3), (mid, 0.55), (dark, 0.85), (deep, 1)),
            Stroke = new SolidColorBrush(Color.FromArgb(0xC0, deep.R, deep.G, deep.B)), StrokeThickness = 0.45,
        });
        // The bowl darkens towards the stem; the plinth from its lit top to its shadowed foot.
        grid.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = G(CupPath), Fill = Down((clear, 0), (clear, 0.45), (Color.FromArgb(0x60, 0, 0, 0), 1)) });
        grid.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = G(PlinthPath), Fill = Down((Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF), 0), (Color.FromArgb(0x55, 0, 0, 0), 1)) });
        // The rim's top face catches the light; a shine runs down the left of the bowl.
        grid.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = G(RimPath), Fill = Across((Color.FromArgb(0xB0, light.R, light.G, light.B), 0), (Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF), 0.35), (Color.FromArgb(0x40, light.R, light.G, light.B), 1)) });
        grid.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = G(ShinePath), Fill = Down((Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF), 0), (Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1)) });
        if (platinum)
            grid.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = G(SparklePath), Fill = new SolidColorBrush(Color.FromArgb(0xFF, 0xE8, 0xF4, 0xFF)) });
        return new Viewbox { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center, Child = grid };
    }
}

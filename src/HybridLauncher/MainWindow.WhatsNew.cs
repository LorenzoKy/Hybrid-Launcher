using HybridLauncher.Core;
using HybridLauncher.Helpers;
using HybridLauncher.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HybridLauncher;

/// <summary>
/// "Welcome to the new version": once after an update, the first time the window is on screen, the version's highlights, a
/// link to everything that changed, and the support card of the welcome tour. A first start gets the tour instead.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The highlights of each version (English, translated through <see cref="L"/>), each with its icon.</summary>
    static readonly Dictionary<string, (string Glyph, string Text)[]> Highlights = new()
    {
        ["1.3.1"] =
        [
            ("", "Achievement pop-ups that look like a console's or a store's, with the game's own platinum and your own sounds."),
            ("", "A Controller tab: give any action to any button, and a Home button that also opens the trophy you just earned."),
            ("", "Search every setting from the box beside the Settings title."),
            ("", "Your play time is kept when you reinstall or move a game."),
            ("", "Epic games that 1.3.0 missed are found again, and Settings fits a 1080p window."),
        ],
    };

    /// <summary>
    /// Hotfixes: the version number (what the updater compares) → the version it fixes, and its own fixes. From that version
    /// only the fixes show; from an older one the fixed version's highlights come first.
    /// </summary>
    static readonly Dictionary<string, (string Of, (string Glyph, string Text)[] Fixes)> Hotfixes = new()
    {
        ["1.3.2"] = ("1.3.1",
        [
            ("", "Settings → Sounds: the Every achievement sound plays its own file again, not gold's."),
            ("", "Settings → About, the README and the site credit every project Hybrid Launcher works with."),
        ]),
    };

    bool _whatsNewQueued;
    ContentDialog? _whatsNew;

    /// <summary>After an update (the last version seen differs), once the window is visible and settled: the new version's welcome.</summary>
    void MaybeShowWhatsNew()
    {
        var s = AppState.Settings;
        string current = AppUpdates.Current.ToString(3);
        if (!s.TourSeen || s.LastVersionSeen == current || _whatsNewQueued || _hidden) return;
        if (Environment.GetCommandLineArgs().Contains("--selftest", StringComparer.OrdinalIgnoreCase)) return;
        _whatsNewQueued = true;
        Later.Run(DispatcherQueue, TimeSpan.FromSeconds(1.5), () =>
        {
            _whatsNewQueued = false;
            if (_closed || _hidden || AppState.Settings.LastVersionSeen == current) return;
            _ = ShowWhatsNewAsync();
        });
    }

    /// <summary>Shows the window for this version (also the self-test's and Settings' way in); seen once it closes.</summary>
    internal async Task ShowWhatsNewAsync()
    {
        if (_whatsNew is not null || RootGrid.XamlRoot is not { } root) return;
        string version = AppUpdates.Current.ToString(3);
        var body = new StackPanel { Spacing = 12, MaxWidth = 560 };
        body.Children.Add(new TextBlock
        {
            Text = L.T("Here is what's new in this version."),
            FontSize = 15, LineHeight = 22, TextWrapping = TextWrapping.Wrap, Opacity = 0.9,
        });
        // A hotfix: its fixes, after the fixed version's highlights when this is the first time on that version.
        var shown = new List<(string Glyph, string Text)>();
        if (Hotfixes.TryGetValue(version, out var hotfix))
        {
            if (AppState.Settings.LastVersionSeen != hotfix.Of && Highlights.TryGetValue(hotfix.Of, out var main)) shown.AddRange(main);
            shown.AddRange(hotfix.Fixes);
        }
        else if (Highlights.TryGetValue(version, out var own)) shown.AddRange(own);
        if (shown.Count > 0)
        {
            var items = shown;
            var list = new StackPanel { Spacing = 10 };
            var tint = Palettes.Adapt(Windows.UI.Color.FromArgb(0xFF, 0xE0, 0x14, 0x2B));
            foreach (var (glyph, text) in items)
            {
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(new Border
                {
                    Width = 30, Height = 30, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Top,
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x26, tint.R, tint.G, tint.B)),
                    Child = new FontIcon { Glyph = glyph, FontSize = 14, Foreground = new SolidColorBrush(tint), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                });
                var label = new TextBlock { Text = L.T(text), FontSize = 14, LineHeight = 20, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(label, 1);
                row.Children.Add(label);
                list.Children.Add(row);
            }
            body.Children.Add(list);
        }
        body.Children.Add(new HyperlinkButton
        {
            Content = L.T("See everything that changed"),
            NavigateUri = new Uri($"https://github.com/{AppInfo.Repository}/releases/tag/v{version}"),
            Padding = new Thickness(0),
        });
        body.Children.Add(SupportCard("New version"));

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = RootGrid.ActualTheme,
            Title = L.F("Welcome to {0} {1}", AppInfo.Name, AppUpdates.FriendlyVersion),
            Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            CloseButtonText = L.T("Let's go"),
            DefaultButton = ContentDialogButton.Close,
        };
        _whatsNew = dialog;
        Log.Info($"Welcome to {version} shown");
        // Seen as soon as it shows: a crash or a closed app never brings it back at every start.
        AppState.Settings.LastVersionSeen = version;
        SettingsStore.Save();
        try { await dialog.ShowAsync(); }
        catch (Exception ex) { Log.Warn($"Welcome to {version}: {ex.Message}"); }
        finally { _whatsNew = null; }
    }

    /// <summary>Self-test: the window is open (shown by <see cref="ShowWhatsNewAsync"/>), then closed.</summary>
    internal bool SelfTestWhatsNew(bool close)
    {
        if (_whatsNew is null) return false;
        if (close) _whatsNew.Hide();
        return true;
    }
}

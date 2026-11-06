using System.Globalization;
using System.Text;
using HybridLauncher.Core;
using HybridLauncher.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HybridLauncher.Views;

/// <summary>
/// Settings → the search box by the title: one search through every tab. Each setting is found by what the page itself shows
/// (its name, its description, its section, the choices of its lists), so it follows the UI language and every row added later
/// with no list to keep. While there is text, the tabs give way to what matches, grouped by tab and section; picking a result
/// opens its tab, scrolls to the setting and lights it for a moment.
/// </summary>
public sealed partial class SettingsPage
{
    /// <summary>One setting found in a tab. <paramref name="Nth"/> tells apart settings of the same name in the same section.</summary>
    sealed record Hit(string Tab, string Section, string Title, string Description, string Keywords, int Nth);

    List<Hit>? _index;

    /// <summary>Ctrl+F on Settings.</summary>
    public void FocusSearch()
    {
        SettingsSearch.Focus(FocusState.Keyboard);
    }

    Panel PanelOf(string tab) => tab switch
    {
        "appearance" => AppearancePanel, "features" => FeaturesPanel, "controller" => ControllerPanel, "achievements" => AchievementsPanel,
        "notifications" => NotificationsPanel, "cloud" => CloudPanel, "storage" => StoragePanel, _ => AboutPanel,
    };

    string TabName(string tab) => SettingsTabs.Items.FirstOrDefault(i => i.Tag as string == tab)?.Text ?? tab;

    void SettingsSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ShowSearch(sender.Text);

    void SettingsSearch_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        // Enter: straight to the best match.
        if (Search(sender.Text).FirstOrDefault() is { } best) GoTo(best);
    }

    bool _searching;

    void ShowSearch(string text)
    {
        bool on = !string.IsNullOrWhiteSpace(text);
        if (!on)
        {
            if (!_searching) return;
            _searching = false;
            _index = null;
            SearchResults.Children.Clear();
            SearchResults.Visibility = Visibility.Collapsed;
            SettingsTabs.Visibility = Visibility.Visible;
            ShowSettingsTab(_openTab);
            return;
        }
        if (!_searching)
        {
            _searching = true;
            SettingsTabs.Visibility = Visibility.Collapsed;
            TabIntro.Visibility = Visibility.Collapsed;
            RestoreDefaultsButton.Visibility = Visibility.Collapsed;
            foreach (var item in SettingsTabs.Items) if (item.Tag is string t) PanelOf(t).Visibility = Visibility.Collapsed;
            SearchResults.Visibility = Visibility.Visible;
        }
        FillResults(Search(text));
    }

    // ---------- the index: every setting as the page shows it ----------

    /// <summary>Reads every tab once per search (tabs built in code are built first, so their rows are there).</summary>
    List<Hit> Index()
    {
        if (_index is not null) return _index;
        try { FillPreviewGames(); } catch { }
        try { AchievementSoundsView.Fill(AchievementSoundsPanel); } catch (Exception ex) { Log.Warn($"Settings search, sounds: {ex.Message}"); }
        try { ControllerSettingsView.Fill(ControllerPanel); } catch (Exception ex) { Log.Warn($"Settings search, controller: {ex.Message}"); }
        try { CloudSettingsView.Fill(CloudPanel); } catch (Exception ex) { Log.Warn($"Settings search, cloud: {ex.Message}"); }
        try { NotificationSettingsView.Fill(NotificationsPanel); } catch (Exception ex) { Log.Warn($"Settings search, notifications: {ex.Message}"); }
        try { GitHubSignInView.Fill(GitHubPanel); } catch (Exception ex) { Log.Warn($"Settings search, GitHub: {ex.Message}"); }
        var list = new List<Hit>();
        foreach (var item in SettingsTabs.Items)
            if (item.Tag is string tab) list.AddRange(Read(tab).Select(r => r.Hit));
        return _index = list;
    }

    /// <summary>The settings of one tab, each with the element to scroll to and the row to light.</summary>
    List<(Hit Hit, FrameworkElement Target, Border? Row)> Read(string tab)
    {
        var found = new List<(Hit, FrameworkElement, Border?)>();
        var groupStyle = Ui.Style("GroupHeader");
        var headerStyle = Ui.Style("RowHeader");
        var descriptionStyle = Ui.Style("RowDescription");
        var rowStyle = Ui.Style("RowStyle");
        string section = "";
        var seen = new Dictionary<string, int>();

        void Walk(DependencyObject node, Border? row, bool root)
        {
            if (!root && node is UIElement { Visibility: Visibility.Collapsed }) return;
            switch (node)
            {
                case TextBlock tb when tb.Style == groupStyle || (tb.CharacterSpacing == 60 && tb.Style != headerStyle):
                    if (!string.IsNullOrWhiteSpace(tb.Text)) section = tb.Text;
                    return;
                case TextBlock tb when tb.Style == headerStyle && !string.IsNullOrWhiteSpace(tb.Text):
                {
                    // Its description: the description lines beside it; its keywords: the choices of the row's lists and boxes
                    // (only for a row with one setting, so the options of one do not find the others).
                    var parent = VisualParentPanel(tb);
                    string description = parent is null ? "" : string.Join(" ", parent.Children.OfType<TextBlock>()
                        .Where(t => t != tb && t.Style == descriptionStyle && t.Visibility == Visibility.Visible).Select(t => t.Text));
                    string keywords = row is not null && CountHeaders(row, headerStyle) == 1 ? string.Join(" ", Choices(row)) : "";
                    string key = section + "\u0001" + tb.Text;
                    int nth = seen.TryGetValue(key, out var n) ? n + 1 : 0;
                    seen[key] = nth;
                    found.Add((new Hit(tab, section, tb.Text.Trim(), description, keywords, nth), tb, row));
                    return;
                }
                case Border b:
                    if (b.Child is { } child) Walk(child, b.Style == rowStyle ? b : row, false);
                    return;
                case Panel p:
                    foreach (var c in p.Children) Walk(c, row, false);
                    return;
                case ScrollViewer sv when sv.Content is DependencyObject content:
                    Walk(content, row, false);
                    return;
                case UserControl uc when uc.Content is DependencyObject content:
                    Walk(content, row, false);
                    return;
            }
        }
        Walk(PanelOf(tab), null, true);
        return found;
    }

    /// <summary>The panel a text sits in, from the tree as it was built (no layout needed: the tab may be hidden).</summary>
    static Panel? VisualParentPanel(FrameworkElement e) => e.Parent as Panel;

    static int CountHeaders(DependencyObject node, Style headerStyle) => node switch
    {
        TextBlock tb => tb.Style == headerStyle ? 1 : 0,
        Border b => b.Child is { } c ? CountHeaders(c, headerStyle) : 0,
        Panel p => p.Children.Sum(c => CountHeaders(c, headerStyle)),
        _ => 0,
    };

    /// <summary>The words of a row's controls: list choices, check boxes, buttons.</summary>
    static IEnumerable<string> Choices(DependencyObject node)
    {
        switch (node)
        {
            case ComboBox combo:
                foreach (var i in combo.Items)
                {
                    var s = (i as ComboBoxItem)?.Content as string ?? i as string;
                    if (s is { Length: > 0 }) yield return s;
                }
                break;
            case ContentControl cc when cc.Content is string text:
                yield return text;
                break;
            case ContentControl cc when cc.Content is DependencyObject inner:
                foreach (var t in Choices(inner)) yield return t;
                break;
            case TextBlock tb when tb.Text is { Length: > 0 } && tb.Style is null:
                yield return tb.Text;
                break;
            case Border b when b.Child is { } child:
                foreach (var t in Choices(child)) yield return t;
                break;
            case Panel p:
                foreach (var c in p.Children) foreach (var t in Choices(c)) yield return t;
                break;
        }
    }

    // ---------- matching ----------

    /// <summary>Lower case, no accents: "Qualità" finds "qualita".</summary>
    static string Fold(string s)
    {
        var d = s.ToLower(CultureInfo.CurrentCulture).Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d) if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString();
    }

    static bool StartsWord(string hay, string word)
    {
        for (int i = hay.IndexOf(word, StringComparison.Ordinal); i >= 0; i = hay.IndexOf(word, i + 1, StringComparison.Ordinal))
            if (i == 0 || !char.IsLetterOrDigit(hay[i - 1])) return true;
        return false;
    }

    /// <summary>
    /// The settings that have every word typed (in their name, section, description, choices or tab), best first: a word in the
    /// name counts most, then the section, then the rest; a word that starts a word counts more than one inside it.
    /// </summary>
    List<Hit> Search(string text)
    {
        var words = Fold(text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];
        var scored = new List<(Hit Hit, int Score, int Order)>();
        int order = 0;
        foreach (var h in Index())
        {
            order++;
            string title = Fold(h.Title), section = Fold(h.Section), rest = Fold(h.Description + " " + h.Keywords + " " + TabName(h.Tab));
            int score = 0;
            foreach (var w in words)
            {
                int best = StartsWord(title, w) ? 12 : title.Contains(w) ? 8 : StartsWord(section, w) ? 5 : section.Contains(w) ? 3 : StartsWord(rest, w) ? 2 : rest.Contains(w) ? 1 : 0;
                if (best == 0) { score = 0; break; }
                score += best;
            }
            if (score > 0) scored.Add((h, score, order));
        }
        return scored.OrderByDescending(x => x.Score).ThenBy(x => x.Order).Select(x => x.Hit).ToList();
    }

    // ---------- the results ----------

    /// <summary>
    /// What matches, grouped by tab and section in the order they come in Settings (the group with the best match first), each
    /// setting a button with its name and description.
    /// </summary>
    void FillResults(List<Hit> hits)
    {
        SearchResults.Children.Clear();
        if (hits.Count == 0)
        {
            SearchResults.Children.Add(new TextBlock
            {
                Text = L.F("No setting matches “{0}”.", SettingsSearch.Text.Trim()),
                Style = Ui.Style("RowDescription"), Margin = new Thickness(0, 8, 0, 0),
            });
            return;
        }
        var all = Index();
        var groups = hits.Take(80).GroupBy(h => (h.Tab, h.Section)).ToList();
        bool first = true;
        foreach (var g in groups)
        {
            var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, first ? 0 : 16, 0, 4) };
            first = false;
            if (TabInfo.TryGetValue(g.Key.Tab, out var info))
            {
                var icon = Icons.Make(info.Glyph, 13);
                if (Application.Current.Resources.TryGetValue($"Tint{info.Tint}Brush", out var tint)) icon.Foreground = tint as Brush;
                icon.VerticalAlignment = VerticalAlignment.Center;
                heading.Children.Add(icon);
            }
            heading.Children.Add(new TextBlock
            {
                Text = TabName(g.Key.Tab).ToUpper(CultureInfo.CurrentCulture) + (g.Key.Section.Length > 0 ? "  ›  " + g.Key.Section : ""),
                Style = Ui.Style("GroupHeader"), VerticalAlignment = VerticalAlignment.Center,
            });
            SearchResults.Children.Add(heading);
            // In the order of the tab, not of the score: a section reads as it does in Settings.
            foreach (var h in g.OrderBy(h => all.IndexOf(h))) SearchResults.Children.Add(ResultButton(h));
        }
        if (hits.Count > 80)
            SearchResults.Children.Add(new TextBlock { Text = L.F("{0} more: type a little more to narrow it down.", hits.Count - 80), Style = Ui.Style("RowDescription"), Margin = new Thickness(0, 8, 0, 0) });
    }

    Button ResultButton(Hit h)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = h.Title, Style = Ui.Style("RowHeader") });
        if (h.Description.Length > 0)
            text.Children.Add(new TextBlock { Text = h.Description, Style = Ui.Style("RowDescription"), MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap });
        grid.Children.Add(text);
        var go = Icons.Make("", 12);
        go.Opacity = 0.6;
        go.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(go, 1);
        grid.Children.Add(go);
        var b = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(14, 8, 14, 8),
            MinHeight = 52,
            Background = Ui.Style("RowStyle").Setters.OfType<Setter>().FirstOrDefault(x => x.Property == Border.BackgroundProperty)?.Value as Brush,
        };
        ToolTipService.SetToolTip(b, TabName(h.Tab) + (h.Section.Length > 0 ? " › " + h.Section : "") + " › " + h.Title);
        b.Click += (_, _) => GoTo(h);
        return b;
    }

    /// <summary>Opens the setting's tab, scrolls to it and lights its row for a moment. The search is cleared.</summary>
    void GoTo(Hit h)
    {
        Log.Info($"Settings search: {h.Tab} › {h.Section} › {h.Title}");
        SettingsSearch.Text = "";
        ShowSearch("");
        if (SettingsTabs.Items.FirstOrDefault(i => i.Tag as string == h.Tab) is { } item && SettingsTabs.SelectedItem != item) SettingsTabs.SelectedItem = item;
        else ShowSettingsTab(h.Tab);
        // Found again in the tab as it is now (a tab built in code makes its rows anew when it opens), once it is laid out.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var match = Read(h.Tab).FirstOrDefault(r => r.Hit.Section == h.Section && r.Hit.Title == h.Title && r.Hit.Nth == h.Nth);
            if (match.Target is null) return;
            LastFound = h.Title;
            match.Target.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.3, AnimationDesired = true });
            if (match.Row is { } row) Light(row);
        });
    }

    /// <summary>The accent edge and a wash of it on a row for a moment and a half, then the row as it was.</summary>
    void Light(Border row)
    {
        var brush = row.ReadLocalValue(Border.BorderBrushProperty);
        var thickness = row.ReadLocalValue(Border.BorderThicknessProperty);
        if (Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out var accent)) row.BorderBrush = accent as Brush;
        row.BorderThickness = new Thickness(2);
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(1600);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            Restore(row, Border.BorderBrushProperty, brush);
            Restore(row, Border.BorderThicknessProperty, thickness);
        };
        timer.Start();
        static void Restore(DependencyObject o, DependencyProperty p, object local)
        {
            if (local == DependencyProperty.UnsetValue) o.ClearValue(p);
            else o.SetValue(p, local);
        }
    }

    /// <summary>The setting the search went to last (self-test).</summary>
    internal string? LastFound { get; private set; }

    /// <summary>
    /// Self-test hook: the search finds settings of XAML tabs and of tabs built in code, groups them, and a result opens its tab
    /// ("local" must find Achievements → Local achievements).
    /// </summary>
    /// <param name="shown">Called while the results for "local" are on screen (a picture for the self-test).</param>
    public async Task SelfTestSearch(Func<Task>? shown = null)
    {
        SettingsSearch.Text = L.T("Hardware acceleration");
        await Task.Delay(300);
        if (!Search(SettingsSearch.Text).Any(h => h.Tab == "features" && h.Title == L.T("Hardware acceleration"))) throw new InvalidOperationException("Hardware acceleration not found");
        if (Index().Count < 60) throw new InvalidOperationException($"only {Index().Count} settings indexed");
        foreach (var tab in new[] { "appearance", "features", "controller", "achievements", "notifications", "cloud", "storage" })
            if (!Index().Any(h => h.Tab == tab)) throw new InvalidOperationException($"nothing indexed in {tab}");
        SettingsSearch.Text = "local";
        await Task.Delay(400);
        var hit = Search("local").FirstOrDefault(h => h.Tab == "achievements" && h.Title == L.T("Local achievements"))
            ?? throw new InvalidOperationException("Local achievements not found");
        if (SearchResults.Children.Count == 0 || SettingsTabs.Visibility == Visibility.Visible) throw new InvalidOperationException("the results are not shown");
        if (shown is not null) await shown();
        GoTo(hit);
        await Task.Delay(700);
        if (_openTab != "achievements" || SettingsTabs.Visibility != Visibility.Visible || LastFound != hit.Title)
            throw new InvalidOperationException($"the result opened {_openTab}, found {LastFound}");
        SettingsSearch.Text = "zzqx";
        await Task.Delay(200);
        SettingsSearch.Text = "";
        await Task.Delay(200);
    }
}

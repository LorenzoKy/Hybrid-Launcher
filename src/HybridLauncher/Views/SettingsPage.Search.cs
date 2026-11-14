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
/// opens its tab, scrolls to the setting and lights it for a moment. Since 2.0 a setting is also found by its English name in
/// an Italian UI, by common other words (synonyms), and with a typo; the words typed are lit in the results; Down goes to the
/// results and Escape clears the search.
/// </summary>
public sealed partial class SettingsPage
{
    /// <summary>One setting found in a tab. <paramref name="Nth"/> tells apart settings of the same name in the same section.</summary>
    sealed record Hit(string Tab, string Section, string Title, string Description, string Keywords, int Nth);

    List<Hit>? _index;

    /// <summary>Ctrl+F on Settings.</summary>
    public void FocusSearch()
    {
        _searchWanted = true;
        try { SettingsSearch.Focus(FocusState.Keyboard); }
        finally { DispatcherQueue.TryEnqueue(() => _searchWanted = false); }
    }

    Panel PanelOf(string tab) => tab switch
    {
        "appearance" => AppearancePanel, "features" => FeaturesPanel, "controller" => ControllerPanel, "achievements" => AchievementsPanel,
        "notifications" => NotificationsPanel, "cloud" => CloudPanel, "storage" => StoragePanel, _ => AboutPanel,
    };

    string TabName(string tab) => SettingsTabs.Items.FirstOrDefault(i => i.Tag as string == tab)?.Text ?? tab;

    void SettingsSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ShowSearch(sender.Text);

    /// <summary>Down: to the first result (then Up and Down move through them). Escape: the search is cleared, the tab comes back.</summary>
    void SettingsSearch_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Down && _searching && SearchResults.Children.OfType<Button>().FirstOrDefault() is { } first)
        {
            first.Focus(FocusState.Keyboard);
            first.StartBringIntoView();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape && SettingsSearch.Text.Length > 0)
        {
            SettingsSearch.Text = "";
            ShowSearch("");
            e.Handled = true;
        }
    }

    /// <summary>Up and Down between the results; Up from the first one goes back to the box.</summary>
    void Result_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key is not (Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down)) return;
        var buttons = SearchResults.Children.OfType<Button>().ToList();
        int i = buttons.IndexOf((Button)sender);
        if (i < 0) return;
        int next = e.Key == Windows.System.VirtualKey.Down ? i + 1 : i - 1;
        if (next < 0) FocusSearch();
        else if (next < buttons.Count) { buttons[next].Focus(FocusState.Keyboard); buttons[next].StartBringIntoView(); }
        e.Handled = true;
    }

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
            ShowSettingsTab(_openTab);
            UpdateNav();
            return;
        }
        if (!_searching)
        {
            _searching = true;
            UpdateNav();
            TabIntro.Visibility = Visibility.Collapsed;
            ShowRestoreDefaults(false);
            foreach (var item in SettingsTabs.Items) if (item.Tag is string t) PanelOf(t).Visibility = Visibility.Collapsed;
            SearchResults.Visibility = Visibility.Visible;
            PageScroller.ScrollTo(0, 0, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
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
        try { AchievementExtrasView.Fill(AchievementExtrasPanel); } catch (Exception ex) { Log.Warn($"Settings search, achievement extras: {ex.Message}"); }
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
    /// Other words for what people type: a word on the left also finds settings with one of the words on the right. English
    /// and Italian, folded (no accents). Only the obvious ones: a wrong synonym fills the results with noise.
    /// </summary>
    static readonly Dictionary<string, string[]> Synonyms = new(StringComparer.Ordinal)
    {
        ["dark"] = ["theme", "tema"], ["light"] = ["theme", "tema"], ["scuro"] = ["theme", "tema"], ["chiaro"] = ["theme", "tema"],
        ["color"] = ["colour", "colore", "theme", "palette"], ["colore"] = ["colour", "theme", "tema", "palette"],
        ["startup"] = ["start", "windows", "avvio"], ["boot"] = ["start", "windows", "avvio"], ["autostart"] = ["start", "windows", "avvio"],
        ["audio"] = ["sound", "suono", "suoni"], ["volume"] = ["sound", "suono", "suoni"],
        ["toast"] = ["notification", "notifica", "notifiche"], ["popup"] = ["notification", "notifica", "pop-up"], ["alert"] = ["notification", "notifica"],
        ["avvisi"] = ["notification", "notifica", "notifiche"],
        ["lang"] = ["language", "lingua"], ["lingua"] = ["language"], ["italiano"] = ["language", "lingua"], ["english"] = ["language", "lingua"],
        ["trophy"] = ["achievement", "obiettivo", "platinum"], ["trophies"] = ["achievement", "obiettivi", "platinum"], ["trofei"] = ["achievement", "obiettivi", "platinum"],
        ["gamepad"] = ["controller"], ["joystick"] = ["controller"], ["pad"] = ["controller"], ["joypad"] = ["controller"],
        ["disk"] = ["storage", "cache", "spazio", "folder"], ["space"] = ["storage", "cache", "spazio"], ["disco"] = ["storage", "cache", "spazio"],
        ["backup"] = ["cloud", "save", "salvataggi"], ["saves"] = ["cloud", "save", "salvataggi"], ["salvataggi"] = ["cloud", "save"],
        ["upgrade"] = ["update", "aggiornamento", "aggiornamenti"], ["aggiorna"] = ["update", "aggiornamento", "aggiornamenti"],
        ["login"] = ["sign", "account", "accedi"], ["signin"] = ["sign", "account", "accedi"], ["accesso"] = ["sign", "account", "accedi"],
        ["blur"] = ["glass", "acrylic", "vetro"], ["transparency"] = ["glass", "acrylic", "vetro", "trasparenza"], ["trasparenza"] = ["glass", "acrylic", "vetro"],
        ["animation"] = ["motion", "animazioni"], ["animazioni"] = ["motion", "animation"],
        ["font"] = ["text", "testo"], ["tray"] = ["notification area", "area di notifica", "background"],
    };

    /// <summary>What one setting is searched in, folded once per search: its name (shown and English), its section, the rest.</summary>
    sealed record Prepared(Hit Hit, string Title, string Section, string Rest, string[] Words);

    List<Prepared>? _prepared;
    List<Hit>? _preparedFrom;

    List<Prepared> PreparedIndex()
    {
        var index = Index();
        if (_prepared is not null && ReferenceEquals(_preparedFrom, index)) return _prepared;
        var list = new List<Prepared>();
        foreach (var h in index)
        {
            // In an Italian UI the English name finds the setting too.
            string title = Fold(h.Title + " " + L.English(h.Title));
            string section = Fold(h.Section + " " + L.English(h.Section));
            string tab = TabName(h.Tab);
            string rest = Fold(h.Description + " " + L.English(h.Description) + " " + h.Keywords + " " + tab + " " + L.English(tab));
            var words = (title + " " + section + " " + rest).Split(WordBreaks, StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
            list.Add(new Prepared(h, title, section, rest, words));
        }
        _preparedFrom = index;
        return _prepared = list;
    }

    static readonly char[] WordBreaks = " ,.;:()/-–—\"“”'’…·›\n\t".ToCharArray();

    /// <summary>
    /// The settings that have every word typed (in their name, section, description, choices or tab), best first: a word in the
    /// name counts most, then the section, then the rest; a word that starts a word counts more than one inside it. A word
    /// can also be met by a synonym (half as much) or, from four letters on, by a word one typo away (two from seven letters).
    /// A name holding the whole text as typed comes first.
    /// </summary>
    List<Hit> Search(string text)
    {
        var words = Fold(text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];
        string phrase = string.Join(" ", words);
        var scored = new List<(Hit Hit, int Score, int Order)>();
        int order = 0;
        foreach (var p in PreparedIndex())
        {
            order++;
            int score = 0;
            foreach (var w in words)
            {
                int best = Exact(p, w);
                if (best == 0 && Synonyms.TryGetValue(w, out var others))
                    foreach (var o in others)
                        if (Exact(p, Fold(o)) is > 0 and var e) best = Math.Max(best, Math.Max(1, e / 2));
                if (best == 0 && w.Length >= 4) best = Fuzzy(p, w);
                if (best == 0) { score = 0; break; }
                score += best;
            }
            if (score == 0) continue;
            if (Fold(p.Hit.Title) == phrase) score += 20;
            else if (words.Length > 1 && p.Title.Contains(phrase, StringComparison.Ordinal)) score += 10;
            scored.Add((p.Hit, score, order));
        }
        return scored.OrderByDescending(x => x.Score).ThenBy(x => x.Order).Select(x => x.Hit).ToList();
    }

    static int Exact(Prepared p, string w) =>
        StartsWord(p.Title, w) ? 12 : p.Title.Contains(w, StringComparison.Ordinal) ? 8 : StartsWord(p.Section, w) ? 5
        : p.Section.Contains(w, StringComparison.Ordinal) ? 3 : StartsWord(p.Rest, w) ? 2 : p.Rest.Contains(w, StringComparison.Ordinal) ? 1 : 0;

    /// <summary>A word typed with a typo: the setting's closest word (or the start of one, while it is still being typed) within reach.</summary>
    static int Fuzzy(Prepared p, string w)
    {
        int reach = w.Length >= 7 ? 2 : 1;
        int best = 0;
        foreach (var word in p.Words)
        {
            if (word.Length < w.Length - reach) continue;
            int d = Distance(w, word, reach);
            if (d > reach && word.Length > w.Length) d = Distance(w, word[..w.Length], reach);
            if (d > reach) continue;
            int s = StartsWord(p.Title, word) ? 6 : StartsWord(p.Section, word) ? 3 : 1;
            if (s > best) best = s;
            if (best == 6) break;
        }
        return best;
    }

    /// <summary>Edit distance (a letter added, dropped, changed, or two swapped), given up past <paramref name="max"/>.</summary>
    static int Distance(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max) return max + 1;
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            int rowMin = int.MaxValue;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                int v = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) v = Math.Min(v, d[i - 2, j - 2] + 1);
                d[i, j] = v;
                rowMin = Math.Min(rowMin, v);
            }
            if (rowMin > max) return max + 1;
        }
        return d[a.Length, b.Length];
    }

    /// <summary>
    /// A text with the words typed lit in the accent colour (each word where it starts a word, else anywhere), for the results.
    /// Matched on the folded text, which keeps one letter per letter for the scripts the app is in.
    /// </summary>
    static void Highlight(TextBlock tb, string text, string[] words)
    {
        tb.Inlines.Clear();
        var folded = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            var f = Fold(c.ToString());
            folded.Append(f.Length == 1 ? f[0] : c);
        }
        string hay = folded.ToString();
        var lit = new bool[text.Length];
        foreach (var w in words)
        {
            if (w.Length == 0) continue;
            int at = -1;
            for (int i = hay.IndexOf(w, StringComparison.Ordinal); i >= 0; i = hay.IndexOf(w, i + 1, StringComparison.Ordinal))
            {
                if (i == 0 || !char.IsLetterOrDigit(hay[i - 1])) { at = i; break; }
                if (at < 0) at = i;
            }
            if (at >= 0) for (int k = at; k < at + w.Length && k < lit.Length; k++) lit[k] = true;
        }
        Brush? accent = Application.Current.Resources.TryGetValue("AccentTextFillColorPrimaryBrush", out var a) ? a as Brush : null;
        int start = 0;
        while (start < text.Length)
        {
            int end = start;
            while (end < text.Length && lit[end] == lit[start]) end++;
            var run = new Microsoft.UI.Xaml.Documents.Run { Text = text[start..end] };
            if (lit[start])
            {
                run.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
                if (accent is not null) run.Foreground = accent;
            }
            tb.Inlines.Add(run);
            start = end;
        }
    }

    // ---------- the results ----------

    /// <summary>
    /// What matches, grouped by tab and section in the order they come in Settings (the group with the best match first), each
    /// setting a button with its name and description.
    /// </summary>
    void FillResults(List<Hit> hits)
    {
        SearchResults.Children.Clear();
        // The results' own heading, where the open tab's stands.
        SearchResults.Children.Add(new TextBlock { Text = L.T("Search results"), FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0) });
        SearchResults.Children.Add(new TextBlock
        {
            Text = hits.Count == 0 ? L.F("No setting matches “{0}”.", SettingsSearch.Text.Trim()) : L.N(hits.Count, "{0} setting matches", "{0} settings match"),
            Style = Ui.Style("RowDescription"), Margin = new Thickness(0, 0, 0, 14),
        });
        if (hits.Count == 0) return;
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
        var words = Fold(SettingsSearch.Text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var name = new TextBlock { Style = Ui.Style("RowHeader") };
        Highlight(name, h.Title, words);
        text.Children.Add(name);
        if (h.Description.Length > 0)
        {
            var about = new TextBlock { Style = Ui.Style("RowDescription"), MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap };
            Highlight(about, h.Description, words);
            text.Children.Add(about);
        }
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
        b.KeyDown += Result_KeyDown;
        return b;
    }

    /// <summary>Self-test: opens a tab at a setting, by its title (as a search result would).</summary>
    internal bool SelfTestGoTo(string tab, string title)
    {
        _index = null;
        if (Search(title).FirstOrDefault(h => h.Tab == tab && h.Title == title) is not { } hit) return false;
        GoTo(hit);
        return true;
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
        Later.Run(DispatcherQueue, TimeSpan.FromMilliseconds(1600), () =>
        {
            Restore(row, Border.BorderBrushProperty, brush);
            Restore(row, Border.BorderThicknessProperty, thickness);
        });
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
        if (SearchResults.Children.Count < 3 || !_searching) throw new InvalidOperationException("the results are not shown");
        if (shown is not null) await shown();
        GoTo(hit);
        await Task.Delay(700);
        if (_openTab != "achievements" || _searching || LastFound != hit.Title)
            throw new InvalidOperationException($"the result opened {_openTab}, found {LastFound}");
        SettingsSearch.Text = "zzqx";
        await Task.Delay(200);
        SettingsSearch.Text = "";
        await Task.Delay(200);
    }
}

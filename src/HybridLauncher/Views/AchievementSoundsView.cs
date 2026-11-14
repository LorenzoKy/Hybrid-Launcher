using HybridLauncher.Helpers;
using HybridLauncher.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HybridLauncher.Views;

/// <summary>
/// Settings → Achievements → Sounds: what plays with an unlock pop-up (Windows' sound, your own .wav files, or nothing), how loud,
/// and your .wav per rarity tier (bronze, silver, gold, platinum) with one for every achievement as the fallback. No sounds ship
/// with the app. Built in code and made again when something changes.
/// </summary>
public static class AchievementSoundsView
{
    static readonly (string Tag, string Name)[] Modes = [("Windows", "Windows notification"), ("File", "My sounds (.wav)"), ("None", "None")];

    /// <summary>The tiers as the pop-up names them (<c>AchievementPopup.Tier</c>), with the share of players each stands for.</summary>
    static readonly (string Tier, string Name, string Range)[] Tiers =
    [
        ("Bronze", "Bronze", "Common: 30% of players or more, or the rarity is not known."),
        ("Silver", "Silver", "Uncommon: 10% to 30% of players."),
        ("Gold", "Gold", "Rare: under 10% of players."),
        ("Platinum", "Platinum", "The game's own platinum: its achievement for unlocking all the others (most games have one)."),
    ];

    public static void Fill(Panel host)
    {
        host.Children.Clear();
        var o = AppState.Settings.Popup;
        void Save(bool again)
        {
            SettingsStore.Save();
            if (again) Fill(host);
        }

        // ---- what plays ----
        host.Children.Add(Header("SOUNDS", top: false));
        var mode = new ComboBox { MinWidth = 200 };
        foreach (var (_, name) in Modes) mode.Items.Add(L.T(name));
        mode.SelectedIndex = Math.Max(0, Array.FindIndex(Modes, m => m.Tag == o.Sound));
        mode.SelectionChanged += (_, _) =>
        {
            if (mode.SelectedIndex < 0 || Modes[mode.SelectedIndex].Tag == o.Sound) return;
            o.Sound = Modes[mode.SelectedIndex].Tag;
            Save(again: true);
        };
        host.Children.Add(Row(Text(L.T("Sound"), L.T("What plays when a pop-up shows. No sounds come with Hybrid Launcher: with My sounds, pick your own .wav files below, one for each rarity if you like.")), mode));

        var volumeText = new TextBlock { MinWidth = 40, VerticalAlignment = VerticalAlignment.Center };
        var volume = new Slider { Width = 150, Minimum = 0, Maximum = 100, StepFrequency = 1, VerticalAlignment = VerticalAlignment.Center, Value = Math.Round(Math.Clamp(o.Volume, 0, 1) * 100) };
        volumeText.Text = $"{volume.Value:0}%";
        volume.ValueChanged += (_, e) =>
        {
            volumeText.Text = $"{e.NewValue:0}%";
            o.Volume = e.NewValue / 100;
            SettingsStore.Save();
        };
        // Let go: the sound plays once at the new volume.
        volume.PointerCaptureLost += (_, _) => AchievementPopup.PlaySample();
        var volumeBox = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        volumeBox.Children.Add(volume);
        volumeBox.Children.Add(volumeText);
        host.Children.Add(Row(Text(L.T("Sound volume"), L.T("How loud the sound plays. Let go of the slider to hear it.")), volumeBox));

        // ---- your files ----
        host.Children.Add(Header("MY SOUNDS", top: true));
        host.Children.Add(new TextBlock
        {
            Text = L.T("Used when Sound is My sounds. A rarity without its own .wav plays the one for every achievement, and without that the Windows sound. Rarity is the share of players who have the achievement (Steam's global stats); platinum is the game's achievement for unlocking all the others."),
            Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });
        double dim = o.Sound == "File" ? 1 : 0.5;
        host.Children.Add(PresetRow(host, o, () => Save(again: true)));

        host.Children.Add(FileRow(L.T("Every achievement"), L.T("Plays for a rarity that has no sound of its own."), o.SoundFile, L.T("Not set: the Windows sound plays."),
            play: () => AchievementPopup.PlaySample(AchievementPopup.EveryTier),
            set: path => { o.SoundFile = path; Save(again: true); }, dim));
        foreach (var (tier, name, range) in Tiers)
        {
            host.Children.Add(FileRow(L.T(name), L.T(range), o.TierSounds.GetValueOrDefault(tier), L.T("Not set: the sound for every achievement plays."),
                play: () => AchievementPopup.PlaySample(tier),
                set: path =>
                {
                    if (path is null) o.TierSounds.Remove(tier);
                    else o.TierSounds[tier] = path;
                    Save(again: true);
                }, dim));
        }
    }

    // ---------- presets ----------

    /// <summary>The slots of My sounds as a preset: "All" and the tiers that have a file.</summary>
    static Dictionary<string, string> Current(PopupSettings o)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (o.SoundFile is { Length: > 0 } all) d["All"] = all;
        foreach (var (k, v) in o.TierSounds) if (v is { Length: > 0 }) d[k] = v;
        return d;
    }

    static bool Same(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && string.Equals(v, p.Value, StringComparison.OrdinalIgnoreCase));

    /// <summary>Fills My sounds with <paramref name="preset"/> and switches Sound to My sounds.</summary>
    internal static void Apply(PopupSettings o, Dictionary<string, string> preset)
    {
        o.SoundFile = preset.GetValueOrDefault("All");
        o.TierSounds.Clear();
        foreach (var (tier, _, _) in Tiers) if (preset.TryGetValue(tier, out var f)) o.TierSounds[tier] = f;
        o.Sound = "File";
    }

    /// <summary>
    /// A folder's .wav files as a preset, by their names: "platinum", "gold", "silver", "bronze" (or the Italian names) go to
    /// their tier, the first other one ("trophy", "default", "all"…) to every achievement.
    /// </summary>
    internal static Dictionary<string, string> FromFolder(string dir)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(dir, "*.wav").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var f in files)
        {
            var n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
            string? slot = n.Contains("platin") ? "Platinum" : n.Contains("gold") || n.Contains("oro") ? "Gold"
                : n.Contains("silver") || n.Contains("argent") ? "Silver" : n.Contains("bronz") ? "Bronze" : null;
            if (slot is not null) d.TryAdd(slot, f);
        }
        if (files.FirstOrDefault(f => !d.ContainsValue(f)) is { } rest) d["All"] = rest;
        return d;
    }

    /// <summary>The preset row: pick a saved set, save the current one under a name, load a folder, delete the picked one.</summary>
    static Border PresetRow(Panel host, PopupSettings o, Action saved)
    {
        var now = Current(o);
        var names = o.SoundPresets.Keys.OrderBy(k => k, StringComparer.CurrentCultureIgnoreCase).ToList();
        var match = names.FirstOrDefault(n => Same(o.SoundPresets[n], now));
        var combo = new ComboBox { MinWidth = 180, PlaceholderText = L.T("No preset") };
        foreach (var n in names) combo.Items.Add(n);
        combo.SelectedIndex = match is null ? -1 : names.IndexOf(match);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not string n || n == match || !o.SoundPresets.TryGetValue(n, out var p)) return;
            Apply(o, p);
            host.DispatcherQueue.TryEnqueue(() => saved());
        };

        var save = new Button { Content = L.T("Save as…"), IsEnabled = now.Count > 0 };
        save.Click += async (_, _) =>
        {
            var box = new TextBox { PlaceholderText = L.T("Preset name"), Text = match ?? "" };
            var dialog = new ContentDialog
            {
                XamlRoot = host.XamlRoot,
                RequestedTheme = (host as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
                Title = L.T("Save these sounds as a preset"),
                Content = box,
                PrimaryButtonText = L.T("Save"),
                CloseButtonText = L.T("Cancel"),
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowOnceAsync() != ContentDialogResult.Primary || box.Text.Trim() is not { Length: > 0 } name) return;
            o.SoundPresets[name] = Current(o);
            saved();
        };

        var folder = new Button { Content = L.T("Load a folder…") };
        ToolTipService.SetToolTip(folder, L.T("Its .wav files fill My sounds by their names (bronze, silver, gold, platinum; any other for every achievement) and are kept as a preset named after the folder."));
        folder.Click += async (_, _) =>
        {
            if (await Pickers.PickFolderAsync(App.Window!) is not { } dir) return;
            var p = FromFolder(dir);
            if (p.Count == 0)
            {
                App.Window?.ShowError(L.T("No sounds"), L.F("{0} has no .wav files.", Path.GetFileName(dir)));
                return;
            }
            o.SoundPresets[Path.GetFileName(dir.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : L.T("My sounds")] = p;
            Apply(o, p);
            saved();
            App.Window?.ShowSuccess(L.F("{0} sounds loaded from {1}.", p.Count, Path.GetFileName(dir)));
        };

        var delete = new Button { Content = new FontIcon { Glyph = "\uE74D", FontSize = 13 }, Padding = new Thickness(10, 8, 10, 8), IsEnabled = match is not null };
        ToolTipService.SetToolTip(delete, L.T("Delete this preset (the sound files stay)"));
        delete.Click += (_, _) =>
        {
            if (match is null) return;
            o.SoundPresets.Remove(match);
            saved();
        };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(combo);
        actions.Children.Add(save);
        actions.Children.Add(folder);
        actions.Children.Add(delete);
        return Row(Text(L.T("Sound preset"), L.T("Keep sets of your own sounds and switch between them, or load a folder of .wav files in one go.")), actions);
    }

    /// <summary>A sound slot: its name and what it covers, the file picked (or what plays without one), then Play, Pick and Clear.</summary>
    static Border FileRow(string header, string description, string? file, string unset, Action play, Action<string?> set, double dim)
    {
        bool missing = file is { Length: > 0 } && !File.Exists(file);
        var text = Text(header, description);
        text.Children.Add(new TextBlock
        {
            Text = file is not { Length: > 0 } ? unset : missing ? L.F("{0} is no longer there: the next sound in line plays.", Path.GetFileName(file)) : Path.GetFileName(file),
            Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap, Opacity = file is { Length: > 0 } && !missing ? 1 : 0.7,
        });
        if (file is { Length: > 0 }) ToolTipService.SetToolTip(text, file);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var playButton = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, Padding = new Thickness(10, 7, 10, 7) };
        ToolTipService.SetToolTip(playButton, L.T("Play"));
        playButton.Click += (_, _) => play();
        var pick = new Button { Content = L.T("Pick .wav…") };
        pick.Click += async (_, _) =>
        {
            if (await Pickers.PickFileAsync(App.Window!, ".wav") is { } path) set(path);
        };
        var clear = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(10, 8, 10, 8), IsEnabled = file is { Length: > 0 } };
        ToolTipService.SetToolTip(clear, L.T("Remove"));
        clear.Click += (_, _) => set(null);
        actions.Children.Add(playButton);
        actions.Children.Add(pick);
        actions.Children.Add(clear);

        var row = Row(text, actions);
        row.Opacity = dim;
        return row;
    }

    static TextBlock Header(string text, bool top) =>
        new() { Text = L.T(text), Style = Ui.Style("GroupHeader"), Margin = new Thickness(0, top ? 16 : 0, 0, 4) };

    static StackPanel Text(string header, string description)
    {
        var p = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        p.Children.Add(new TextBlock { Text = header, Style = Ui.Style("RowHeader") });
        p.Children.Add(new TextBlock { Text = description, Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap });
        return p;
    }

    static Border Row(FrameworkElement text, FrameworkElement action)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        action.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(action, 1);
        grid.Children.Add(action);
        return new Border { Style = Ui.Style("RowStyle"), Child = grid };
    }
}

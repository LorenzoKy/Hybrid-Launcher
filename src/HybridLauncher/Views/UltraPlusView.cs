using System.Globalization;
using System.Text.RegularExpressions;
using HybridLauncher.Core;
using HybridLauncher.Core.Detection;
using HybridLauncher.Core.Mods;
using HybridLauncher.Helpers;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HybridLauncher.Views;

/// <summary>
/// The Ultra+ tab of a game: three sections the page stacks (and lists as sub-tabs). Install &amp; versions finds the game's
/// Ultra+ mod and every version of it (from Ultra+'s server, newest first, betas on request), installs, updates, goes back to an
/// older version or removes it; Add-ons puts the game's optional add-ons in and out; Settings edits UltraPlusConfig.ini as
/// switches and sliders (saved at once, F12 in game applies), with the shipped presets, the user's own and CustomSettings.ini.
/// </summary>
public sealed class UltraPlusTab
{
    public static Color Tint => Palettes.Adapt(Color.FromArgb(0xFF, 0x4F, 0xD6, 0xB0));

    readonly ModContext _ctx;
    public readonly UserControl Install = new(), Addons = new(), Settings = new();
    List<UltraGame> _games = [];
    List<UltraFile> _addonFiles = [];
    UltraEntry? _entry;
    bool _loaded, _loading;
    /// <summary>Something is being installed or removed in this game (here, or before the page was made again: GameJobs).</summary>
    bool _busy => GameJobs.Busy(Game.Id);

    /// <summary>The Add-ons and Settings sections have something to show (the page hides a section, and its sub-tab, that has not).</summary>
    public bool AddonsShown { get; private set; }
    public bool SettingsShown { get; private set; }

    /// <summary>A section appeared or went away: the page shows or hides it.</summary>
    public event Action? SectionsChanged;

    public UltraPlusTab(ModContext ctx)
    {
        _ctx = ctx;
        Install.Content = Ui.Card(Ui.Loading());
        // Lists read again or a mod changed (here, from the Ultra+ page or the background check): redrawn while on screen.
        void OnChanged() { if (Install.XamlRoot is not null && !_busy) _ = LoadAsync(false); }
        Install.Loaded += (_, _) => UltraPlusLibrary.Changed += OnChanged;
        Install.Unloaded += (_, _) => UltraPlusLibrary.Changed -= OnChanged;
        // A setting changed a moment ago is written before the page goes.
        Settings.Unloaded += (_, _) => Flush();
        _ = LoadAsync(false);
    }

    Game Game => _ctx.Game;
    GameSettings GameSettings => _ctx.Settings;

    /// <summary>The tab opened again: the folder may have changed (installed by hand, the overlay saved settings).</summary>
    public void Refresh()
    {
        if (_loaded && !_busy) _ = LoadAsync(false);
    }

    async Task LoadAsync(bool force)
    {
        if (_loading) return;
        _loading = true;
        try
        {
            if (force) await UltraPlus.RefreshAsync(_ctx.Token);
            var games = UltraPlus.GetGamesAsync(ct: _ctx.Token);
            var files = UltraPlus.GetModFilesAsync(ct: _ctx.Token);
            var addons = UltraPlus.GetAddonFilesAsync(ct: _ctx.Token);
            await UltraPlus.LoadSettingTextsAsync(_ctx.Token);
            _games = await games;
            var match = UltraPlusLibrary.Resolve(Game, GameSettings, _games);
            var builds = await files;
            _entry = match is null ? null : await Task.Run(() => UltraPlusLibrary.Describe(Game, GameSettings, match, builds));
            _addonFiles = await addons;
            if (force) App.Window?.ShowInfo(_entry?.Update is { } up ? L.F("Ultra+ {0} is out for {1}.", up.Version, Game.Title) : L.T("Ultra+: no newer version for this game."));
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Log.Warn($"Ultra+ tab: {ex.Message}"); }
        finally { _loading = false; }
        if (_ctx.Token.IsCancellationRequested) return;
        _loaded = true;
        Ui.Safe(BuildInstall);
        Ui.Safe(BuildAddons);
        Ui.Safe(() => BuildSettings(rebuild: false));
        SectionsChanged?.Invoke();
    }

    // ======================================================================== install & versions

    readonly ProgressBar _progress = new() { Maximum = 1 };
    readonly TextBlock _progressText = new() { Style = Ui.Style("RowDescription"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    /// <summary>The controls that start a change, each with when it may be used (busy or not aside).</summary>
    readonly List<(Control Control, Func<bool> Usable)> _locked = [];

    void BuildInstall()
    {
        _locked.Clear();
        var head = new StackPanel { Spacing = 12 };
        var wiki = _entry?.Ultra.WikiUrl is { Length: > 0 } w ? w : UltraPlus.Site;
        head.Children.Add(Ui.Heading("", L.T("Ultra+: better graphics for this game"), Ui.LinkButton(L.T("Ultra+ guide"), "", () => Ui.Open(wiki)),
            subtitle: L.T("Free engine mods by The Ultra Place: lighting, shadows, reflections and ray tracing, tuned live"), tint: Tint));

        if (_games.Count == 0)
        {
            head.Children.Add(Ui.Muted(L.T("Could not read Ultra+'s list of games (offline?). Try again in a moment.")));
            head.Children.Add(ModUi.Action(L.T("Try again"), "", () => _ = LoadAsync(true)));
        }
        else if (_entry is null) BuildUnsupported(head);
        else BuildSupported(head, _entry);

        var tips = new StackPanel { Spacing = 6 };
        tips.Children.Add(Ui.Heading("", L.T("Good to know"), tint: Ui.Slate));
        foreach (var t in new[]
        {
            "• In game, F12 applies the settings you change here. Turn on Automatically Apply Configuration to see changes live.",
            "• Ctrl+Shift+F11 opens Ultra+'s overlay (the game must run borderless). Alt+F2 compares with the game's own look.",
            "• Do not combine Ultra+ with Engine.ini tweaks: they fight over the same settings.",
            "• With OptiScaler in the same game, give OptiScaler a file name other than dwmapi.dll (dxgi.dll works for most games).",
            "• Updates keep your settings. Uninstalling keeps them too: installing again brings them back.",
            "• Ultra+ is made by The Ultra Place team. Hybrid Launcher downloads each version from their own server and checks it against their list.",
        })
            tips.Children.Add(Ui.Muted(L.T(t)));
        var links = new WrapPanel(8, []);
        links.Children.Add(Ui.LinkButton("theultraplace.com", "", () => Ui.Open(UltraPlus.Site)));
        links.Children.Add(Ui.LinkButton("Discord", "", () => Ui.Open(UltraPlus.Discord)));
        if (_entry?.Ultra.NexusUrl is { Length: > 0 } nexus) links.Children.Add(Ui.LinkButton(L.T("Nexus Mods page"), "", () => Ui.Open(nexus)));
        tips.Children.Add(links);
        Install.Content = ModUi.TwoColumns(Ui.Card(head), Ui.Card(tips));
    }

    void BuildUnsupported(StackPanel head)
    {
        head.Children.Add(ModUi.Check(null, L.F("Ultra+ has no mod of its own for {0}", Game.Title),
            L.T("If Ultra+ lists it under another name, pick it below.")));
        head.Children.Add(Picker(L.T("Search Ultra+'s games")));
    }

    /// <summary>A search over every game Ultra+ supports: the one picked becomes this game's Ultra+ mod.</summary>
    AutoSuggestBox Picker(string placeholder)
    {
        var search = new AutoSuggestBox { PlaceholderText = placeholder, QueryIcon = new SymbolIcon(Symbol.Find), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 320 };
        var all = _games.Concat(UltraPlus.Generic).ToList();
        search.TextChanged += (s, e) =>
        {
            if (e.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var q = RenoDx.Key(s.Text);
            s.ItemsSource = q.Length < 2 ? null : all.Where(g => RenoDx.Key(g.Name).Contains(q) || RenoDx.Key(g.Key).Contains(q) || g.SearchTerms.Any(t => RenoDx.Key(t).Contains(q)))
                .Take(12).Select(g => g.Name).ToList();
        };
        search.SuggestionChosen += (s, e) =>
        {
            if (all.FirstOrDefault(g => g.Name == e.SelectedItem as string) is { } g) Pick(g.Key);
        };
        return search;
    }

    void Pick(string? key)
    {
        GameSettings.UltraPlusKey = key;
        SettingsStore.Save();
        _ = LoadAsync(false);
        UltraPlusLibrary.RaiseChanged();
    }

    void BuildSupported(StackPanel head, UltraEntry e)
    {
        var state = GameSettings.UltraPlus;
        bool mine = state is not null;
        var folder = e.Folder;

        // Which Ultra+ mod, and why: changeable.
        var which = new WrapPanel(8, []);
        which.Children.Add(new TextBlock { Text = e.Ultra.Name, FontWeight = FontWeights.SemiBold, FontSize = 17, VerticalAlignment = VerticalAlignment.Center });
        which.Children.Add(Ui.Chip(e.Reason));
        if (UltraPlus.IsGeneric(e.Ultra)) which.Children.Add(Ui.Pill(L.T("Generic version"), Ui.Tone.Partial));
        var change = new HyperlinkButton { Content = L.T("Not this game?"), Padding = new Thickness(4, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center };
        var pickerHost = new Border { Visibility = Visibility.Collapsed };
        change.Click += (_, _) =>
        {
            pickerHost.Child ??= Picker(L.T("Pick the right game from Ultra+'s list"));
            pickerHost.Visibility = pickerHost.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        };
        which.Children.Add(change);
        if (GameSettings.UltraPlusKey is not null && !mine)
        {
            var auto = new HyperlinkButton { Content = L.T("Match automatically again"), Padding = new Thickness(4, 2, 4, 2), VerticalAlignment = VerticalAlignment.Center };
            auto.Click += (_, _) => Pick(null);
            which.Children.Add(auto);
        }
        head.Children.Add(which);
        head.Children.Add(pickerHost);

        // Where it stands in the folder.
        var lines = new StackPanel { Spacing = 6 };
        if (Game.NotInstalled)
            lines.Children.Add(ModUi.Check(null, L.F("{0} is not installed: Ultra+ can go in once it is.", Game.Title)));
        else if (e.Layout.ExeDir is null)
            lines.Children.Add(ModUi.Check(false, L.T("The game exe was not found"), L.T("Pick it with the gear beside Play → Change game exe.")));
        else
        {
            lines.Children.Add(ModUi.Check(folder.Installed,
                mine ? L.F("Ultra+ {0} is installed", state!.Version) + (state.Beta ? " " + L.T("(beta)") : "")
                : folder.Installed ? L.F("Ultra+ {0} is in the folder, installed outside Hybrid Launcher", folder.Version ?? "?")
                : L.T("Ultra+ is not installed in this game"),
                mine ? L.F("{0} files, installed {1:d MMM yyyy, HH:mm}", state!.Files.Count, state.InstalledAt) + (state.OwnUe4ss ? "  ·  " + L.T("your own UE4SS") : "")
                : folder.Installed ? L.T("Install a version here and Hybrid Launcher takes it over: it can then update and remove it. Your settings stay.") : null));
            if (!e.Ultra.InstallRootOnly)
                lines.Children.Add(ModUi.Check(folder.Ue4ss ? true : null, folder.Ue4ss ? L.T("UE4SS is in the game folder") : L.T("UE4SS comes with Ultra+"),
                    folder.OtherUe4ssMods.Count > 0 ? L.F("Other UE4SS mods here: {0}", string.Join(", ", folder.OtherUe4ssMods)) : null));
            if (Core.Opti.OptiDetector.Detect(e.Layout.ExeDir) is { } opti)
            {
                bool clash = opti.FileName.Equals("dwmapi.dll", StringComparison.OrdinalIgnoreCase) && !e.Ultra.InstallRootOnly;
                lines.Children.Add(ModUi.Check(!clash, L.F("OptiScaler is here as {0}", opti.FileName),
                    clash ? L.T("Ultra+ loads UE4SS through dwmapi.dll: reinstall OptiScaler under another file name first (dxgi.dll works for most games).")
                          : L.T("They work together. Use one upscaler setup at a time: OptiScaler's or Ultra+'s.")));
            }
            if (e.Update is { } up)
                lines.Children.Add(Ui.Callout("", L.F("Ultra+ {0} is out ({1:d MMM yyyy}).", up.Version, up.Published) + (mine ? " " + L.T("Updating keeps your settings.") : ""), Ui.Tone.Good));
        }
        head.Children.Add(lines);
        if (e.Ultra.Note is { Length: > 0 } note) head.Children.Add(NoteCallout(note));

        // The version to install: newest first, betas marked.
        var newestStable = e.Versions.FirstOrDefault(f => !f.Beta);
        var combo = new ComboBox { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var v in e.Versions)
        {
            var tags = new List<string>();
            if (v == newestStable) tags.Add(L.T("latest"));
            if (v.Beta) tags.Add(L.T("beta"));
            if (state?.File.Equals(v.FileName, StringComparison.OrdinalIgnoreCase) == true) tags.Add(L.T("installed"));
            combo.Items.Add(new ComboBoxItem
            {
                Content = $"v{v.Version}  ·  {v.Published:d MMM yyyy}  ·  {DiskUsage.Format(v.Size)}" + (tags.Count > 0 ? "  ·  " + string.Join(", ", tags) : ""),
                Tag = v,
            });
        }
        var preferred = e.Update ?? newestStable ?? e.Versions.FirstOrDefault();
        combo.SelectedIndex = preferred is null ? -1 : Math.Max(0, e.Versions.IndexOf(preferred));
        var hint = Ui.Muted("");
        hint.TextWrapping = TextWrapping.Wrap;
        var own = new CheckBox { Content = L.T("I manage my own UE4SS: only install Ultra+'s files"), IsChecked = state?.OwnUe4ss ?? false };
        ToolTipService.SetToolTip(own, L.T("For games where you keep UE4SS up to date yourself. Leave it off if you do not know what UE4SS is."));
        var betas = new CheckBox { Content = L.T("Show beta versions"), IsChecked = AppState.Settings.UltraPlusBetas };
        betas.Click += (_, _) =>
        {
            AppState.Settings.UltraPlusBetas = betas.IsChecked == true;
            SettingsStore.Save();
            _ = UltraPlusLibrary.ScanAsync();
        };
        // Shared by every game (the background look runs every three hours, MainWindow.UltraPlus.cs).
        var autoUpdate = new CheckBox { Content = L.T("Update automatically"), IsChecked = AppState.Settings.UltraPlusAutoUpdate };
        ToolTipService.SetToolTip(autoUpdate, L.T("In every game: a newer version found in the background goes in by itself, your settings kept."));
        autoUpdate.Click += (_, _) =>
        {
            AppState.Settings.UltraPlusAutoUpdate = autoUpdate.IsChecked == true;
            SettingsStore.Save();
        };
        var notify = new CheckBox { Content = L.T("Notify me of new versions"), IsChecked = AppState.Settings.UltraPlusNotify };
        ToolTipService.SetToolTip(notify, L.T("In every game: a Windows notification when a newer version of an Ultra+ mod you have comes out."));
        notify.Click += (_, _) =>
        {
            AppState.Settings.UltraPlusNotify = notify.IsChecked == true;
            // The versions already out are not news: only later ones notify.
            if (AppState.Settings.UltraPlusNotify) MainWindow.MarkUltraUpdatesSeen();
            SettingsStore.Save();
        };

        var installText = new TextBlock { FontWeight = FontWeights.SemiBold };
        var installIcon = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        installIcon.Children.Add(new FontIcon { Glyph = "", FontSize = 14 });
        installIcon.Children.Add(installText);
        var install = new Button { Content = installIcon, Padding = new Thickness(18, 8, 18, 8), Style = Ui.Style("AccentButtonStyle") };
        install.Click += (_, _) => { if ((combo.SelectedItem as ComboBoxItem)?.Tag is UltraFile v) _ = InstallAsync(e.Ultra, v, own.IsChecked == true); };
        var remove = ModUi.Action(L.T("Uninstall"), "", () => _ = UninstallAsync());
        var openCfg = ModUi.Action(L.T("Open the settings folder"), "", () => { if (e.Layout.ConfigDir is { } d && Directory.Exists(d)) GameLauncher.OpenFolder(d); });
        var check = ModUi.Action(L.T("Check for new versions"), "", () => _ = LoadAsync(true));
        ToolTipService.SetToolTip(check, UltraPlus.CheckedAt is { } at ? L.F("Last checked {0:d MMM, HH:mm}", at) : L.T("Not checked yet"));
        bool Installable() => (combo.SelectedItem as ComboBoxItem)?.Tag is UltraFile && !Game.NotInstalled && e.Layout.ExeDir is not null;
        _locked.Add((install, Installable));
        foreach (var c in new Control[] { remove, combo, own, betas, autoUpdate, notify, check }) _locked.Add((c, () => true));
        void Update()
        {
            var v = (combo.SelectedItem as ComboBoxItem)?.Tag as UltraFile;
            bool same = v is not null && state?.File.Equals(v.FileName, StringComparison.OrdinalIgnoreCase) == true;
            bool newer = v is not null && state is not null && !same && UltraPlus.Compare(v.Version, state.Version) > 0;
            bool older = v is not null && state is not null && !same && !newer;
            installText.Text = v is null ? L.T("Install")
                : state is null ? (folder.Installed ? L.F("Take over with {0}", v.Version) : L.F("Install {0}", v.Version))
                : same ? L.T("Reinstall") : newer ? L.F("Update to {0}", v.Version) : L.F("Go back to {0}", v.Version);
            hint.Text = v is null ? ""
                : v.Beta ? L.T("Beta version: newer fixes and features, less tested. Go back to a stable version if something breaks.")
                : older ? L.T("An older version: for when a newer one misbehaves. Your settings stay.")
                : v == newestStable ? L.T("The newest stable version, recommended.") + (e.Ultra.GamePass && Game.Store == GameStore.Xbox ? " " + L.T("(the Game Pass version)") : "")
                : "";
            install.IsEnabled = !_busy && Installable();
            remove.Visibility = mine ? Visibility.Visible : Visibility.Collapsed;
            openCfg.Visibility = folder.Installed && e.Layout.ConfigDir is { } d && Directory.Exists(d) ? Visibility.Visible : Visibility.Collapsed;
        }
        combo.SelectionChanged += (_, _) => Update();
        Update();

        if (e.Versions.Count == 0)
            head.Children.Add(Ui.Muted(L.T("Ultra+'s server has no version of this mod for this store yet.")));
        else
        {
            var pick = new StackPanel { Spacing = 6 };
            pick.Children.Add(new TextBlock { Text = L.T("VERSION"), Style = Ui.Style("GroupHeader") });
            pick.Children.Add(combo);
            pick.Children.Add(hint);
            head.Children.Add(pick);
        }
        var options = new WrapPanel(16, []);
        if (!e.Ultra.InstallRootOnly && !e.Ultra.NoUe4ss) options.Children.Add(own);
        options.Children.Add(betas);
        options.Children.Add(autoUpdate);
        options.Children.Add(notify);
        head.Children.Add(options);

        // While a version downloads and goes in: what it is doing, the bar, and Cancel (as the OptiScaler install shows it).
        var cancel = new Button { Content = L.T("Cancel") };
        cancel.Click += (_, _) => GameJobs.For(Game.Id)?.Cts.Cancel();
        if (_progress.Parent is Panel old) old.Children.Remove(_progress);
        if (_progressText.Parent is Panel oldText) oldText.Children.Remove(_progressText);
        var progressHead = new Grid { ColumnSpacing = 12 };
        progressHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        progressHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        progressHead.Children.Add(_progressText);
        Grid.SetColumn(cancel, 1);
        progressHead.Children.Add(cancel);
        var progressPanel = new StackPanel { Spacing = 6, Visibility = GameJobs.Busy(Game.Id, "ultra") ? Visibility.Visible : Visibility.Collapsed };
        progressPanel.Children.Add(progressHead);
        progressPanel.Children.Add(_progress);
        _progressPanel = progressPanel;

        var buttons = new WrapPanel(8, []) { Margin = new Thickness(0, 4, 0, 0) };
        buttons.Children.Add(install);
        buttons.Children.Add(remove);
        buttons.Children.Add(openCfg);
        buttons.Children.Add(check);
        head.Children.Add(buttons);
        head.Children.Add(progressPanel);
        Lock(_busy);
    }

    StackPanel? _progressPanel;

    /// <summary>Busy: every control that would start another change is off, the progress shows.</summary>
    void Lock(bool busy)
    {
        foreach (var (c, usable) in _locked) c.IsEnabled = !busy && usable();
        if (_progressPanel is not null) _progressPanel.Visibility = busy && GameJobs.Busy(Game.Id, "ultra") ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A job for this game moved on or started: Ultra+'s own shows its progress; any job locks the buttons.</summary>
    public void ShowJob(GameJobs.Job? job)
    {
        Lock(job is not null);
        if (job is not { Kind: "ultra" }) return;
        if (job.Fraction >= 0) _progress.Value = job.Fraction;
        if (job.Stage.Length > 0) _progressText.Text = job.Stage;
    }

    /// <summary>Ultra+'s note for a game, with its Markdown links as buttons.</summary>
    static FrameworkElement NoteCallout(string note)
    {
        var panel = new StackPanel { Spacing = 6 };
        var links = Regex.Matches(note, @"\[([^\]]+)\]\((https?://[^)\s]+)\)").Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();
        panel.Children.Add(Ui.Callout("", Regex.Replace(note, @"\[([^\]]+)\]\((https?://[^)\s]+)\)", "$1"), Ui.Tone.Partial));
        if (links.Count > 0)
        {
            var row = new WrapPanel(8, []);
            foreach (var (text, url) in links) row.Children.Add(Ui.LinkButton(text, "", () => Ui.Open(url)));
            panel.Children.Add(row);
        }
        return panel;
    }

    async Task InstallAsync(UltraGame ultra, UltraFile file, bool ownUe4ss)
    {
        if (_busy) return;
        _progress.Value = 0;
        _progressText.Text = L.F("Downloading Ultra+ {0}…", file.Version);
        Lock(true);
        try
        {
            // The progress comes through GameJobs (ShowJob), so a page opened again while it runs shows it too. Leaving the page
            // does not cancel it: only its Cancel button does.
            if (await UltraPlusLibrary.InstallAsync(Game, ultra, file, ownUe4ss, null, CancellationToken.None)) _ctx.OptiChanged();
        }
        finally
        {
            Lock(false);
            await LoadAsync(false);
        }
    }

    async Task UninstallAsync()
    {
        if (_busy) return;
        Lock(true);
        if (_progressPanel is not null) _progressPanel.Visibility = Visibility.Collapsed;
        try
        {
            Flush();
            if (await UltraPlusLibrary.UninstallAsync(Game)) _ctx.OptiChanged();
        }
        finally
        {
            Lock(false);
            await LoadAsync(false);
        }
    }

    // ======================================================================== add-ons

    void BuildAddons()
    {
        var state = GameSettings.UltraPlus;
        var list = _entry is null ? [] : UltraPlus.Addons(_entry.Ultra, _addonFiles, Game.Store == GameStore.Xbox);
        if (_entry is null || list.Count == 0)
        {
            // Nothing to offer: the section stays off the page (and its sub-tab with it).
            Addons.Content = null;
            AddonsShown = false;
            return;
        }
        AddonsShown = true;
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Ui.Heading("", L.T("Add-ons"), subtitle: L.T("Optional extras for this game's Ultra+ mod: more ray tracing, fixes, frame generation"), tint: Ui.Pink));
        if (state is null) panel.Children.Add(Ui.Callout("", L.T("Install Ultra+ first: add-ons go on top of it."), Ui.Tone.Partial));
        foreach (var (addon, builds) in list)
        {
            var name = addon.FileName;
            var installed = state is not null && state.Addons.TryGetValue(name, out var a) ? a : null;
            var row = new StackPanel { Spacing = 6 };
            var title = new WrapPanel(8, []);
            title.Children.Add(new TextBlock { Text = UltraPlus.Spaced(addon.Name), FontWeight = FontWeights.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
            if (installed is not null) title.Children.Add(Ui.Pill(L.F("Installed {0}", installed.Version), Ui.Tone.Good));
            if (installed is not null && builds[0].Published > installed.Published.AddMinutes(1)) title.Children.Add(Ui.Pill(L.F("{0} is out", builds[0].Version), Ui.Tone.Accent));
            // Another variant of the same add-on is in: this one would take its place.
            var rival = state?.Addons.Keys.FirstOrDefault(k => !k.Equals(name, StringComparison.OrdinalIgnoreCase) && UltraPlus.AddonFamily(k).Equals(UltraPlus.AddonFamily(name), StringComparison.OrdinalIgnoreCase));
            if (rival is not null) title.Children.Add(Ui.Chip(L.F("replaces {0}", UltraPlus.Spaced(rival))));
            row.Children.Add(title);
            if (addon.Description is { Length: > 0 } d) row.Children.Add(Ui.Muted(d));
            var combo = new ComboBox { MinWidth = 260 };
            foreach (var b in builds) combo.Items.Add(new ComboBoxItem { Content = $"v{b.Version}  ·  {b.Published:d MMM yyyy}" + (b.Beta ? "  ·  " + L.T("beta") : ""), Tag = b });
            combo.SelectedIndex = Math.Max(0, builds.FindIndex(b => !b.Beta));
            var buttons = new WrapPanel(8, []);
            buttons.Children.Add(combo);
            var add = ModUi.Action(installed is null ? L.T("Install") : L.T("Reinstall"), "", () =>
            {
                if ((combo.SelectedItem as ComboBoxItem)?.Tag is UltraFile f) _ = InstallAddonAsync(f);
            }, accent: installed is null);
            add.IsEnabled = state is not null && !_busy;
            buttons.Children.Add(add);
            if (installed is not null)
            {
                var removeAddon = ModUi.Action(L.T("Remove"), "", () =>
                {
                    UltraPlusLibrary.UninstallAddon(Game, name);
                    _ = LoadAsync(false);
                });
                removeAddon.IsEnabled = !_busy;
                buttons.Children.Add(removeAddon);
            }
            row.Children.Add(buttons);
            panel.Children.Add(new Border { Style = Ui.Style("RowStyle"), Padding = new Thickness(16, 12, 16, 12), Child = row });
        }
        Addons.Content = Ui.Card(panel);
    }

    async Task InstallAddonAsync(UltraFile file)
    {
        if (_busy || _entry is null) return;
        Lock(true);
        try { await UltraPlusLibrary.InstallAddonAsync(Game, _entry.Ultra, file, null, CancellationToken.None); }
        finally
        {
            Lock(false);
            await LoadAsync(false);
        }
    }

    // ======================================================================== settings

    Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, string> _defaults = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _pending = new(StringComparer.OrdinalIgnoreCase);
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _saveTimer;
    string? _configDir;
    /// <summary>The settings file as last read or written here: the section is built again only when someone else changed it.</summary>
    string _stamp = "";
    readonly List<(UltraSetting Setting, FrameworkElement Row, Action Sync)> _rows = [];
    readonly List<(FrameworkElement Group, List<FrameworkElement> Rows)> _groups = [];
    Dictionary<string, string>? _undo;
    Button? _undoButton;

    string Stamp(string dir)
    {
        var f = Path.Combine(dir, UltraPlus.ConfigFile);
        return File.Exists(f) ? f + "|" + File.GetLastWriteTimeUtc(f).Ticks : "";
    }

    void BuildSettings(bool rebuild)
    {
        var dir = _entry?.Layout.ConfigDir;
        bool present = dir is not null && File.Exists(Path.Combine(dir, UltraPlus.ConfigFile));
        if (!present)
        {
            Settings.Content = null;
            SettingsShown = false;
            _configDir = null;
            _stamp = "";
            return;
        }
        // The same file as on screen, unchanged: what is there stays (search, folded groups, scroll).
        if (!rebuild && dir == _configDir && Stamp(dir!) == _stamp && Settings.Content is not null) return;
        Flush();
        _configDir = dir;
        _stamp = Stamp(dir!);
        SettingsShown = true;
        _rows.Clear();
        _groups.Clear();
        _undo = null;

        var panel = new StackPanel { Spacing = 12 };
        var open = Ui.LinkButton(L.T("Open the file"), "", () => ModUi.OpenFile(Path.Combine(_configDir!, UltraPlus.ConfigFile), pickApp: true));
        panel.Children.Add(Ui.Heading("", L.T("Ultra+ settings"), open, subtitle: L.T("UltraPlusConfig.ini, saved as you change it: press F12 in game to apply"), tint: Ui.Blue));
        var settings = UltraPlus.ReadSettings(_configDir!);
        _values = UltraPlus.ReadConfig(_configDir!);
        _defaults = UltraPlus.Defaults(_configDir!, settings);

        // Presets: the shipped quality levels, Ultra+'s defaults, and the user's own.
        var presets = new StackPanel { Spacing = 8 };
        presets.Children.Add(new TextBlock { Text = L.T("PRESETS"), Style = Ui.Style("GroupHeader") });
        var row = new WrapPanel(8, []);
        foreach (var (name, values) in UltraPlus.ShippedPresets(_configDir!))
        {
            var b = ModUi.Action(PresetName(name), "", () => Apply(values, L.F("{0} preset applied. Press F12 in game.", PresetName(name))));
            ToolTipService.SetToolTip(b, L.F("Sets {0} quality settings to Ultra+'s {1} preset; the others stay as they are.", values.Count, PresetName(name)));
            row.Children.Add(b);
        }
        row.Children.Add(ModUi.Action(L.T("Ultra+ defaults"), "", () => Apply(_defaults, L.T("Every setting is back to Ultra+'s default. Press F12 in game."))));
        _undoButton = ModUi.Action(L.T("Undo"), "", () =>
        {
            if (_undo is not { } before) return;
            _undo = null;
            Apply(before, L.T("The settings are back as they were. Press F12 in game."), remember: false);
        });
        _undoButton.Visibility = Visibility.Collapsed;
        row.Children.Add(_undoButton);
        presets.Children.Add(row);
        presets.Children.Add(OwnPresets());
        presets.Children.Add(Ui.Muted(L.T("Ultra+'s presets go beyond the game's own: its Epic is well above the game's Epic. Start from High on most cards.")));
        panel.Children.Add(presets);

        // Search over every setting.
        var search = new AutoSuggestBox { PlaceholderText = L.F("Search {0} settings", settings.Count), QueryIcon = new SymbolIcon(Symbol.Find), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 320 };
        var changedOnly = new CheckBox { Content = L.T("Only the ones you changed") };
        var filters = new WrapPanel(16, []);
        filters.Children.Add(search);
        filters.Children.Add(changedOnly);
        panel.Children.Add(filters);
        var none = Ui.Muted(L.T("No setting matches."));
        none.Visibility = Visibility.Collapsed;

        var groups = new StackPanel { Spacing = 14 };
        foreach (var group in settings.GroupBy(s => s.Category).OrderBy(g => UltraPlus.CategoryOrder(g.Key)).ThenBy(g => g.Key))
        {
            var body = new StackPanel { Spacing = 2 };
            var rows = new List<FrameworkElement>();
            foreach (var s in group.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                var r = SettingRow(s);
                rows.Add(r);
                body.Children.Add(r);
            }
            var part = ModUi.FoldPart("ultra-" + group.Key, Glyph(group.Key), GroupName(group.Key), body, Tint);
            _groups.Add((part, rows));
            groups.Children.Add(part);
        }
        panel.Children.Add(groups);
        panel.Children.Add(none);
        void Filter()
        {
            var q = search.Text.Trim();
            foreach (var (s, r, _) in _rows)
            {
                bool hit = q.Length == 0 || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Key.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Description.Contains(q, StringComparison.OrdinalIgnoreCase);
                if (changedOnly.IsChecked == true) hit &= Changed(s);
                r.Visibility = hit ? Visibility.Visible : Visibility.Collapsed;
            }
            // A group with nothing left to show goes too.
            foreach (var (g, rows) in _groups) g.Visibility = rows.Any(r => r.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
            none.Visibility = _groups.Any(g => g.Group.Visibility == Visibility.Visible) ? Visibility.Collapsed : Visibility.Visible;
        }
        search.TextChanged += (_, _) => Filter();
        changedOnly.Click += (_, _) => Filter();

        // CustomSettings.ini: console variables of the user's own.
        var custom = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MinHeight = 90, MaxHeight = 260, FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            PlaceholderText = "r.Shadow.MaxResolution=8192", Text = UltraPlus.ReadCustom(_configDir!),
        };
        ScrollViewer.SetVerticalScrollBarVisibility(custom, ScrollBarVisibility.Auto);
        var saveCustom = ModUi.Action(L.T("Save"), "", () =>
        {
            try
            {
                UltraPlus.WriteCustom(_configDir!, custom.Text);
                App.Window?.ShowSuccess(L.T("CustomSettings.ini saved. Press F12 in game."));
            }
            catch (Exception ex) { App.Window?.ShowError(L.T("Could not save CustomSettings.ini"), ex.Message); }
        });
        var customBody = new StackPanel { Spacing = 8 };
        customBody.Children.Add(Ui.Muted(L.T("Your own console variables, one per line (r.Name=value). They load last and win over Ultra+'s values.")));
        customBody.Children.Add(custom);
        customBody.Children.Add(saveCustom);
        panel.Children.Add(ModUi.FoldPart("ultra-custom", "", L.T("Custom console variables"), customBody, Ui.Slate));

        Settings.Content = Ui.Card(panel);
    }

    static string PresetName(string name) => name.ToLowerInvariant() switch
    {
        "low" => L.T("Low"), "medium" => L.T("Medium"), "high" => L.T("High"), "veryhigh" => L.T("Very high"), "epic" => L.T("Epic"),
        "ultra" => L.T("Ultra"), "insane" => L.T("Insane"),
        _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name),
    };

    /// <summary>Ultra+'s group names, a few made plainer.</summary>
    static string GroupName(string category) => category switch
    {
        "U+ Custom" => L.T("Ultra+ tweaks"),
        "Upscaling, Denoising, and Frame Generation" => L.T("Upscaling, denoising and frame generation"),
        "Draw Distance" => L.T("Draw distance"),
        _ => L.T(category),
    };

    static string Glyph(string category) => category.ToLowerInvariant() switch
    {
        var c when c.Contains("system") => "",
        var c when c.Contains("upscal") || c.Contains("frame") => "",
        var c when c.Contains("ray") => "",
        var c when c.Contains("reflect") => "",
        var c when c.Contains("light") => "",
        var c when c.Contains("shadow") => "",
        var c when c.Contains("fog") || c.Contains("atmos") => "",
        var c when c.Contains("post") => "",
        var c when c.Contains("distance") || c.Contains("detail") || c.Contains("lod") || c.Contains("scalab") => "",
        var c when c.Contains("debug") => "",
        _ => "",
    };

    bool Changed(UltraSetting s) => _values.TryGetValue(s.Key, out var v) && _defaults.TryGetValue(s.Key, out var d) && !v.Equals(d, StringComparison.OrdinalIgnoreCase);

    FrameworkElement SettingRow(UltraSetting s)
    {
        var grid = new Grid { ColumnSpacing = 16, Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var dot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Tint), VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(dot, L.T("Changed from Ultra+'s default"));
        title.Children.Add(new TextBlock { Text = s.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        title.Children.Add(dot);
        if (s.Hotkey is { } key) title.Children.Add(Ui.Chip(key));
        text.Children.Add(title);
        if (s.Description.Length > 0) text.Children.Add(new TextBlock { Text = s.Description, Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis });
        grid.Children.Add(text);

        var resetButton = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(7), MinWidth = 0, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
        ToolTipService.SetToolTip(resetButton, L.T("Back to Ultra+'s default"));
        FrameworkElement control;
        Action sync;
        string Current() => _values.GetValueOrDefault(s.Key) ?? s.Default;
        if (s.Numeric)
        {
            static bool IsNumber(string v) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            static double Number(string v) => double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture);
            var slider = new Slider { Minimum = s.Min, Maximum = s.Max, StepFrequency = s.Step, SmallChange = s.Step, MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
            var gameBox = new CheckBox { Content = L.T("Game"), MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(gameBox, L.T("The game decides (Ultra+ leaves it alone)"));
            var value = new TextBlock { MinWidth = 40, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
            bool syncing = false;
            string Format(double d) => s.IsFloat ? d.ToString("0.###", CultureInfo.InvariantCulture) : Math.Round(d).ToString(CultureInfo.InvariantCulture);
            sync = () =>
            {
                syncing = true;
                var v = Current();
                bool game = !IsNumber(v);
                gameBox.IsChecked = game;
                slider.IsEnabled = !game;
                if (!game) slider.Value = Math.Clamp(Number(v), s.Min, s.Max);
                value.Text = game ? "—" : Format(slider.Value);
                syncing = false;
            };
            slider.ValueChanged += (_, e) =>
            {
                if (syncing) return;
                value.Text = Format(e.NewValue);
                Set(s, Format(e.NewValue));
            };
            gameBox.Click += (_, _) =>
            {
                if (gameBox.IsChecked == true) Set(s, "game");
                else Set(s, Format(IsNumber(s.Default) ? Number(s.Default) : s.Min + (s.Max - s.Min) / 2));
                sync();
            };
            var line = new Grid { ColumnSpacing = 8 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(gameBox);
            Grid.SetColumn(slider, 1);
            line.Children.Add(slider);
            Grid.SetColumn(value, 2);
            line.Children.Add(value);
            Grid.SetColumn(resetButton, 3);
            line.Children.Add(resetButton);
            control = line;
        }
        else
        {
            var values = s.Values.Count > 0 ? s.Values.ToList() : ["off", "on"];
            var current = Current();
            if (current.Length > 0 && !values.Contains(current, StringComparer.OrdinalIgnoreCase)) values.Add(current);
            var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
            foreach (var v in values) combo.Items.Add(new ComboBoxItem { Content = ValueName(v), Tag = v });
            bool syncing = false;
            sync = () =>
            {
                syncing = true;
                var v = Current();
                combo.SelectedIndex = Math.Max(0, values.FindIndex(x => x.Equals(v, StringComparison.OrdinalIgnoreCase)));
                syncing = false;
            };
            combo.SelectionChanged += (_, _) => { if (!syncing && (combo.SelectedItem as ComboBoxItem)?.Tag is string v) Set(s, v); };
            var line = new Grid { ColumnSpacing = 8 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(combo);
            Grid.SetColumn(resetButton, 1);
            line.Children.Add(resetButton);
            control = line;
        }
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        void Mark()
        {
            bool changed = Changed(s);
            dot.Visibility = changed ? Visibility.Visible : Visibility.Collapsed;
            resetButton.Opacity = changed ? 0.9 : 0;
            resetButton.IsHitTestVisible = changed;
        }
        resetButton.Click += (_, _) =>
        {
            if (_defaults.TryGetValue(s.Key, out var d)) Set(s, d);
            sync();
        };
        void Full() { sync(); Mark(); }
        Full();
        _rows.Add((s, grid, Full));
        // Narrow pages: the control goes under the text.
        bool narrowNow = false;
        grid.SizeChanged += (_, e) =>
        {
            bool narrow = e.NewSize.Width < 640;
            if (narrow == narrowNow) return;
            narrowNow = narrow;
            if (grid.RowDefinitions.Count == 0)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            grid.RowSpacing = narrow ? 8 : 0;
            grid.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(340);
            grid.ColumnSpacing = narrow ? 0 : 16;
            Grid.SetColumn(control, narrow ? 0 : 1);
            Grid.SetRow(control, narrow ? 1 : 0);
        };
        return grid;

        void Set(UltraSetting setting, string v)
        {
            _values[setting.Key] = v;
            _pending[setting.Key] = v;
            QueueSave();
            Mark();
        }
    }

    static string ValueName(string v) => v.ToLowerInvariant() switch
    {
        "game" => L.T("Game default"),
        "off" => L.T("Off"),
        "on" => L.T("On"),
        "veryhigh" => L.T("Very high"),
        _ => Regex.Replace(CultureInfo.InvariantCulture.TextInfo.ToTitleCase(v.Replace('_', ' ')), @"Minus(\d)point(\d)", "-$1.$2").Replace("Minuspoint5", "-0.5").Replace("Minus", "-"),
    };

    /// <summary>Changes are written a moment after the last one (a slider being dragged writes once).</summary>
    void QueueSave()
    {
        if (_saveTimer is null)
        {
            _saveTimer = HybridLauncher.Helpers.WeakTimer.Create(Install.DispatcherQueue, this, TimeSpan.FromMilliseconds(400), static u => u.Flush(), repeating: false);
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    void Flush()
    {
        _saveTimer?.Stop();
        if (_pending.Count == 0 || _configDir is null) return;
        try
        {
            UltraPlus.WriteConfig(_configDir, _pending);
            _pending.Clear();
            _stamp = Stamp(_configDir);
        }
        catch (Exception ex) { App.Window?.ShowError(L.T("Could not save Ultra+'s settings"), ex.Message); }
    }

    /// <summary>A preset (or the defaults) goes in at once; Undo puts back what was there before it.</summary>
    void Apply(IReadOnlyDictionary<string, string> values, string message, bool remember = true)
    {
        if (remember)
            _undo = values.Keys.Where(_values.ContainsKey).ToDictionary(k => k, k => _values[k], StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in values)
        {
            _values[k] = v;
            _pending[k] = v;
        }
        Flush();
        foreach (var r in _rows) r.Sync();
        if (_undoButton is not null) _undoButton.Visibility = _undo is not null ? Visibility.Visible : Visibility.Collapsed;
        App.Window?.ShowSuccess(message);
    }

    /// <summary>The user's own presets for this game: save the current values under a name, load or delete one.</summary>
    FrameworkElement OwnPresets()
    {
        var key = _entry!.Ultra.Key;
        var mine = AppState.Settings.UltraPlusPresets.GetValueOrDefault(key) ?? new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var row = new WrapPanel(8, []);
        var combo = new ComboBox { PlaceholderText = mine.Count == 0 ? L.T("No presets of your own yet") : L.T("Your presets"), MinWidth = 220, IsEnabled = mine.Count > 0 };
        foreach (var name in mine.Keys.OrderBy(n => n)) combo.Items.Add(name);
        var load = ModUi.Action(L.T("Load"), "", () =>
        {
            if (combo.SelectedItem is string n && mine.TryGetValue(n, out var values)) Apply(values, L.F("Preset \"{0}\" loaded. Press F12 in game.", n));
        });
        var delete = ModUi.Action(L.T("Delete"), "", () => _ = DeletePresetAsync(combo.SelectedItem as string, mine));
        load.IsEnabled = delete.IsEnabled = false;
        combo.SelectionChanged += (_, _) => load.IsEnabled = delete.IsEnabled = combo.SelectedItem is string;
        var save = ModUi.Action(L.T("Save as preset"), "", () => _ = SavePresetAsync(key, mine));
        row.Children.Add(combo);
        row.Children.Add(load);
        row.Children.Add(delete);
        row.Children.Add(save);
        return row;
    }

    async Task DeletePresetAsync(string? name, Dictionary<string, Dictionary<string, string>> mine)
    {
        if (name is null || Settings.XamlRoot is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = Settings.XamlRoot, RequestedTheme = Settings.ActualTheme, Title = L.F("Delete the preset \"{0}\"?", name),
            Content = L.T("The settings in the game stay as they are; only the saved preset goes."),
            PrimaryButtonText = L.T("Delete"), CloseButtonText = L.T("Cancel"), DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowOnceAsync() != ContentDialogResult.Primary) return;
        mine.Remove(name);
        SettingsStore.Save();
        BuildSettings(rebuild: true);
    }

    async Task SavePresetAsync(string key, Dictionary<string, Dictionary<string, string>> mine)
    {
        try
        {
            var box = new TextBox { PlaceholderText = L.T("Name"), Text = L.F("My preset {0}", mine.Count + 1) };
            var dialog = new ContentDialog
            {
                XamlRoot = Settings.XamlRoot, RequestedTheme = Settings.ActualTheme, Title = L.T("Save the current settings as a preset"), Content = box,
                PrimaryButtonText = L.T("Save"), CloseButtonText = L.T("Cancel"), DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowOnceAsync() != ContentDialogResult.Primary || box.Text.Trim() is not { Length: > 0 } n) return;
            Flush();
            mine[n] = new Dictionary<string, string>(_values, StringComparer.OrdinalIgnoreCase);
            AppState.Settings.UltraPlusPresets[key] = mine;
            SettingsStore.Save();
            App.Window?.ShowSuccess(L.F("Preset \"{0}\" saved.", n));
            BuildSettings(rebuild: true);
        }
        catch (Exception ex) { App.Window?.ShowError(L.T("Could not save the preset"), ex.Message); }
    }
}

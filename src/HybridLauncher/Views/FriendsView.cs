using HybridLauncher.Core;
using HybridLauncher.Core.Accounts;
using HybridLauncher.Helpers;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace HybridLauncher.Views;

/// <summary>
/// The Steam friends panel (the game list's footer, once Steam is signed in in Hybrid Launcher): avatars, who is online and in which game,
/// grouped as Steam's friends list does. A click opens the friend in Steam (chat), the menu their profile. Read only: Hybrid Launcher's
/// session never changes the account's own state.
/// </summary>
public sealed class FriendsView
{
    readonly StackPanel _list = new() { Spacing = 2 };
    readonly TextBlock _state = new() { Style = Ui.Style("RowDescription"), TextWrapping = TextWrapping.Wrap };
    readonly ProgressRing _ring = new() { IsActive = true, Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Left };
    readonly Microsoft.UI.Dispatching.DispatcherQueue _ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _redraw;
    CancellationTokenSource? _cts;

    public FrameworkElement Root { get; }

    /// <summary>Online friends, after the last load (the footer's badge).</summary>
    public static int OnlineCount { get; private set; }

    public static event Action? CountChanged;

    public FriendsView()
    {
        var head = new Grid { ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(StoreLogos.Create(GameStore.Steam, 16));
        title.Children.Add(new TextBlock { Text = L.T("Friends"), Style = Ui.Style("BodyStrongTextBlockStyle"), FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(title);
        var refresh = new Button { Style = Ui.Style("IconButton"), Content = new FontIcon { Glyph = "", FontSize = 13 } };
        ToolTipService.SetToolTip(refresh, L.T("Refresh"));
        refresh.Click += (_, _) => _ = LoadAsync();
        Grid.SetColumn(refresh, 1);
        head.Children.Add(refresh);

        var scroll = new ScrollViewer { Content = _list, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 12, 0) };
        var body = new StackPanel { Spacing = 10, Width = 320 };
        body.Children.Add(head);
        body.Children.Add(_state);
        body.Children.Add(_ring);
        body.Children.Add(scroll);
        Root = body;

        // Steam's updates come in bursts on SteamKit2's thread: one redraw for each burst.
        _redraw = _ui.CreateTimer();
        _redraw.Interval = TimeSpan.FromMilliseconds(400);
        _redraw.IsRepeating = false;
        _redraw.Tick += (_, _) => _ = LoadAsync(quiet: true);
        body.Loaded += (_, _) => { SteamAccount.FriendsChanged -= OnChanged; SteamAccount.FriendsChanged += OnChanged; };
        body.Unloaded += (_, _) => { SteamAccount.FriendsChanged -= OnChanged; _cts?.Cancel(); };
    }

    void OnChanged() => _ui.TryEnqueue(() => { _redraw.Stop(); _redraw.Start(); });

    public async Task LoadAsync(bool quiet = false)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        if (!quiet) { _ring.Visibility = Visibility.Visible; _state.Text = L.T("Asking Steam…"); }
        try
        {
            var friends = await SteamAccount.FriendsAsync(cts.Token);
            if (cts.IsCancellationRequested) return;
            Fill(friends);
            OnlineCount = friends.Count(f => f.Online);
            CountChanged?.Invoke();
            _state.Text = friends.Count == 0 ? L.T("No friends on this Steam account yet.") : L.F("{0} online of {1}", OnlineCount, friends.Count);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"Steam friends: {ex.Message}");
            _state.Text = L.F("Could not read the friends list: {0}", ex.Message);
        }
        finally { if (_cts == cts) _ring.Visibility = Visibility.Collapsed; }
    }

    void Fill(List<SteamFriend> friends)
    {
        _list.Children.Clear();
        foreach (var group in friends.GroupBy(f => f.Rank switch { 0 => 0, 1 or 2 => 1, _ => 2 }).OrderBy(g => g.Key))
        {
            string label = group.Key switch { 0 => L.T("IN GAME"), 1 => L.T("ONLINE"), _ => L.T("OFFLINE") };
            _list.Children.Add(new TextBlock { Text = $"{label}  ({group.Count()})", Style = Ui.Style("GroupHeader"), Margin = new Thickness(4, _list.Children.Count == 0 ? 0 : 10, 0, 4) });
            foreach (var f in group) _list.Children.Add(Row(f));
        }
    }

    static readonly Color InGame = Color.FromArgb(0xFF, 0x90, 0xBA, 0x3C), Online = Color.FromArgb(0xFF, 0x57, 0xCB, 0xDE), Offline = Color.FromArgb(0xFF, 0x89, 0x89, 0x89);

    static FrameworkElement Row(SteamFriend f)
    {
        var tint = f.InGame && f.Online ? InGame : f.Online ? Online : Offline;
        var avatar = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(2), BorderBrush = new SolidColorBrush(tint), Background = Ui.Brush("ControlFillColorSecondaryBrush") };
        if (f.AvatarUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var u))
            avatar.Child = Smooth.Art(new BitmapImage { UriSource = u, DecodePixelWidth = 64 }, Smooth.Inner(avatar), Stretch.UniformToFill);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 0 };
        text.Children.Add(new TextBlock { Text = f.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = new SolidColorBrush(f.Online ? tint : Color.FromArgb(0xFF, 0xB0, 0xB0, 0xB0)) });
        text.Children.Add(new TextBlock { Text = Detail(f), FontSize = 12, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = f.InGame && f.Online ? new SolidColorBrush(tint) : null });
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(6, 5, 6, 5), Opacity = f.Online ? 1 : 0.7 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(avatar);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var button = new Button
        {
            Content = grid, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8),
        };
        ToolTipService.SetToolTip(button, L.T("Chat in Steam"));
        button.Click += (_, _) => Ui.Open($"steam://friends/message/{f.SteamId}");
        var menu = new MenuFlyout();
        var chat = new MenuFlyoutItem { Text = L.T("Chat in Steam"), Icon = new FontIcon { Glyph = "" } };
        chat.Click += (_, _) => Ui.Open($"steam://friends/message/{f.SteamId}");
        menu.Items.Add(chat);
        var profile = new MenuFlyoutItem { Text = L.T("Steam profile"), Icon = new FontIcon { Glyph = "" } };
        profile.Click += (_, _) => Ui.Open(f.ProfileUrl);
        menu.Items.Add(profile);
        if (f.PlayingAppId != 0)
        {
            var store = new MenuFlyoutItem { Text = L.F("{0} in the store", f.PlayingName ?? L.T("The game")), Icon = new FontIcon { Glyph = "" } };
            store.Click += (_, _) => Ui.Open($"https://store.steampowered.com/app/{f.PlayingAppId}");
            menu.Items.Add(store);
        }
        button.ContextFlyout = menu;
        return button;
    }

    static string Detail(SteamFriend f)
    {
        if (f.InGame && f.Online) return f.PlayingName ?? L.T("In a game");
        if (f.Online) return f.State switch
        {
            SteamKit2.EPersonaState.Away or SteamKit2.EPersonaState.Snooze => L.T("Away"),
            SteamKit2.EPersonaState.Busy => L.T("Busy"),
            _ => L.T("Online"),
        };
        if (f.LastOnline is not { } t) return L.T("Offline");
        var ago = DateTime.UtcNow - t.ToUniversalTime();
        return ago.TotalMinutes < 60 ? L.F("Last online {0} min ago", Math.Max(1, (int)ago.TotalMinutes))
            : ago.TotalHours < 48 ? L.F("Last online {0} h ago", (int)ago.TotalHours)
            : L.F("Last online {0} days ago", (int)ago.TotalDays);
    }
}

using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace HybridLauncher.Views;

/// <summary>
/// What the Frame shows while a game's page is on screen (2.0.0-rc.4): nothing of its own. The game pages live beside the
/// Frame, one per recent game (MainWindow.GameSlots), so they are never taken out of the window and laid out again; this page
/// only carries the game's id through the Frame's history (Back, Forward) and tells the window which game to show.
/// </summary>
public sealed partial class GameShellPage : Page
{
    public GameShellPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        App.Window?.ShowGamePage(e.Parameter as string);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        App.Window?.LeaveGamePage(e.SourcePageType == typeof(GameShellPage));
    }
}

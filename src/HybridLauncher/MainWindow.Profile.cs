using HybridLauncher.Core;
using HybridLauncher.Helpers;
using HybridLauncher.Views;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HybridLauncher;

/// <summary>
/// The local user's chip under the title (2.1): their picture and name on a soft wash of the theme's colour. A click opens
/// their dashboard (Views.ProfilePage), where the profile is edited, switched, added or removed. Before
/// any profile exists it invites to make one. Folded pane: the picture alone.
/// </summary>
public sealed partial class MainWindow
{
    bool _profileWired, _profileOpen = true;

    void UpdateProfileChip() => UpdateProfileChip(_profileOpen);

    /// <summary>Draws the chip for an open (<paramref name="open"/>) or folded pane.</summary>
    void UpdateProfileChip(bool open)
    {
        _profileOpen = open;
        if (!_profileWired)
        {
            _profileWired = true;
            Profiles.Changed += UpdateProfileChip;
            ThemeRes.Applied += UpdateProfileChip;
        }
        try
        {
            var p = Profiles.Active;
            // Drawn again while it holds the focus (a theme change): the focus stays on the chip, else WinUI hands it to the
            // next element, the search box.
            bool hadFocus = Content.XamlRoot is { } xr && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(xr) is DependencyObject f
                && ProfileChip.Children.OfType<UIElement>().Any(c => IsInside(f, c));
            ProfileChip.Children.Clear();
            var accent = Palettes.Current?.Accent ?? Microsoft.UI.Colors.White;

            var face = new Grid { ColumnSpacing = 14, Padding = open ? new Thickness(8, 9, 12, 9) : new Thickness(0), CornerRadius = new CornerRadius(16) };
            if (open)
            {
                var wash = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1) };
                wash.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x38, accent.R, accent.G, accent.B), Offset = 0 });
                wash.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x0A, accent.R, accent.G, accent.B), Offset = 1 });
                face.Background = wash;
            }
            face.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            face.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var avatar = Profiles.Avatar(p, open ? 56 : 34, ring: true);
            face.Children.Add(avatar);
            if (open)
            {
                // The name alone, big (2.1-rc.3: the user's call, no line under it and no menu beside it; switching, adding and
                // editing live on the dashboard).
                var name = new TextBlock
                {
                    Text = p?.Name ?? L.T("Create your profile"), FontSize = p is null ? 16 : 21, FontWeight = FontWeights.Bold, CharacterSpacing = -10,
                    TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(name, 1);
                face.Children.Add(name);
            }
            var button = new Button
            {
                Content = face, Padding = new Thickness(0), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(16),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), HorizontalAlignment = open ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            ToolTipService.SetToolTip(button, p is null ? L.T("Create your profile") : L.F("{0}: open your dashboard", p.Name));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, p?.Name ?? L.T("Create your profile"));
            button.Click += async (_, _) =>
            {
                if (Profiles.Active is null)
                {
                    if (Content.XamlRoot is { } root && await Profiles.EditAsync(root, ((FrameworkElement)Content).ActualTheme, null) is not null) OpenProfile();
                    return;
                }
                OpenProfile();
            };
            // The picture springs a little under the pointer.
            avatar.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(160) };
            button.PointerEntered += (_, _) => { avatar.CenterPoint = new((float)avatar.Width / 2, (float)avatar.Height / 2, 0); avatar.Scale = new(1.08f, 1.08f, 1); };
            button.PointerExited += (_, _) => avatar.Scale = new(1, 1, 1);
            ProfileChip.Children.Add(button);
            if (hadFocus) button.Focus(FocusState.Programmatic);
        }
        catch (Exception ex) { Log.Warn($"Profile chip: {ex.Message}"); }
    }

    static bool IsInside(DependencyObject e, UIElement root)
    {
        for (DependencyObject? d = e; d is not null; d = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, root)) return true;
        return false;
    }

    /// <summary>The user's dashboard.</summary>
    public void OpenProfile()
    {
        try
        {
            if (Shown is not ProfilePage) ContentFrame.Navigate(typeof(ProfilePage));
            SyncSelection();
        }
        catch (Exception ex) { ShowError(L.T("Could not open the profile"), ex.Message, ex); }
    }
}

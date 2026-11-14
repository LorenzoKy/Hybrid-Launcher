using HybridLauncher.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace HybridLauncher.Helpers;

/// <summary>
/// The local users of Hybrid Launcher (2.1): each a name and a picture the user picks, shown under the title and leading to
/// their dashboard (Views.ProfilePage). One is in use at a time; switching is a click in the profile's menu. The library, play
/// time and achievements are this PC's, so every profile's dashboard sums up the same games.
/// </summary>
public static class Profiles
{
    static AppSettings S => AppState.Settings;

    /// <summary>The profile in use, or null before the first one is made.</summary>
    public static UserProfile? Active => S.Profiles.FirstOrDefault(p => p.Id == S.ActiveProfile) ?? S.Profiles.FirstOrDefault();

    /// <summary>Raised on the UI thread when a profile is made, changed, switched or removed.</summary>
    public static event Action? Changed;

    /// <summary>Adds a profile and puts it in use.</summary>
    public static void Add(UserProfile p)
    {
        S.Profiles.Add(p);
        S.ActiveProfile = p.Id;
        SettingsStore.Save();
        Changed?.Invoke();
    }

    public static void Switch(UserProfile p)
    {
        S.ActiveProfile = p.Id;
        SettingsStore.Save();
        Log.Info("Profile switched");
        Changed?.Invoke();
    }

    public static void Remove(UserProfile p)
    {
        S.Profiles.Remove(p);
        if (S.ActiveProfile == p.Id) S.ActiveProfile = S.Profiles.FirstOrDefault()?.Id;
        SettingsStore.Save();
        DeletePicture(p.Picture);
        Changed?.Invoke();
    }

    static void DeletePicture(string? path)
    {
        if (path is null) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log.Warn($"Profile picture: {ex.Message}"); }
    }

    /// <summary>The first letters of up to two words of the name ("Alex Rivera" → "AR").</summary>
    public static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var s = string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
        return s.Length > 0 ? s : "?";
    }

    /// <summary>
    /// A round avatar: the profile's picture, or its initials on the theme's accent. <paramref name="ring"/> adds a thin ring of
    /// light in the accent around it (the profile's chip and the dashboard's header).
    /// </summary>
    public static FrameworkElement Avatar(UserProfile? p, double size, bool ring = false)
    {
        var grid = new Grid { Width = size, Height = size };
        var accent = Palettes.Current?.Accent ?? Microsoft.UI.Colors.White;
        if (ring)
        {
            var glow = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1) };
            glow.GradientStops.Add(new GradientStop { Color = accent, Offset = 0 });
            glow.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x60, accent.R, accent.G, accent.B), Offset = 0.55 });
            glow.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), Offset = 1 });
            grid.Children.Add(new Ellipse { Fill = glow });
        }
        double inset = ring ? Math.Max(2, Math.Round(size * 0.05)) : 0;
        var face = new Ellipse { Margin = new Thickness(inset) };
        if (p?.Picture is { } pic && File.Exists(pic))
            face.Fill = new ImageBrush { Stretch = Stretch.UniformToFill, ImageSource = new BitmapImage { UriSource = new Uri(pic), DecodePixelWidth = (int)(size * 2) } };
        else
        {
            var bg = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1) };
            bg.GradientStops.Add(new GradientStop { Color = Mix(accent, Microsoft.UI.Colors.Black, 0.25), Offset = 0 });
            bg.GradientStops.Add(new GradientStop { Color = Mix(accent, Microsoft.UI.Colors.Black, 0.65), Offset = 1 });
            face.Fill = bg;
        }
        grid.Children.Add(face);
        if (p?.Picture is null || !File.Exists(p.Picture))
            grid.Children.Add(new TextBlock
            {
                Text = p is null ? "+" : Initials(p.Name), FontSize = Math.Round(size * 0.38), FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Contrast.On(Mix(accent, Microsoft.UI.Colors.Black, 0.45))),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextLineBounds = TextLineBounds.Tight,
            });
        return grid;
    }

    static Color Mix(Color a, Color b, double t) => Color.FromArgb(255,
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    /// <summary>
    /// Makes a profile (<paramref name="edit"/> null) or changes one: a name and a picture the user picks from their files.
    /// Returns the profile, or null when cancelled.
    /// </summary>
    public static async Task<UserProfile?> EditAsync(XamlRoot root, ElementTheme theme, UserProfile? edit)
    {
        // One dialog at a time: a second click while it opens would show a second one, which WinUI refuses with an exception
        // that ended the app (2.1-rc.3 audit).
        if (_editing) return null;
        _editing = true;
        try { return await EditCoreAsync(root, theme, edit); }
        catch (Exception ex)
        {
            Log.Error("Profile dialog", ex);
            return null;
        }
        finally { _editing = false; }
    }

    static bool _editing;

    static async Task<UserProfile?> EditCoreAsync(XamlRoot root, ElementTheme theme, UserProfile? edit)
    {
        string? picked = edit?.Picture is { } current && File.Exists(current) ? current : null;
        bool pickedNew = false;
        var name = new TextBox { Header = L.T("Name"), Text = edit?.Name ?? "", PlaceholderText = L.T("Your name or nickname"), MaxLength = 32 };
        var previewHost = new Border { HorizontalAlignment = HorizontalAlignment.Center };
        var hint = new TextBlock { Style = (Style)ThemeRes.Current["RowDescription"], TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
        void Preview()
        {
            var p = new UserProfile { Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : "?", Picture = picked };
            previewHost.Child = Avatar(p, 112, ring: true);
            hint.Text = picked is null ? L.T("Pick a picture for your profile: a photo, an avatar, your favourite character.") : L.T("Looking good. Pick another to change it.");
        }
        name.TextChanged += (_, _) => Preview();
        var pick = new Button { Content = L.T("Choose a picture…"), HorizontalAlignment = HorizontalAlignment.Center, Style = (Style)ThemeRes.Current["AccentButtonStyle"] };
        pick.Click += async (_, _) =>
        {
            if (App.Window is not { } w || await Pickers.PickFileAsync(w, ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif") is not { } file) return;
            picked = file;
            pickedNew = true;
            Preview();
        };
        var panel = new StackPanel { Spacing = 14, Width = 380 };
        panel.Children.Add(previewHost);
        panel.Children.Add(pick);
        panel.Children.Add(hint);
        panel.Children.Add(name);
        Preview();
        var dialog = new ContentDialog
        {
            XamlRoot = root, RequestedTheme = theme,
            Title = edit is null ? L.T("Create your profile") : L.T("Edit profile"),
            Content = panel,
            PrimaryButtonText = edit is null ? L.T("Create") : L.T("Save"),
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = (edit?.Name ?? "").Trim().Length > 0,
        };
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = name.Text.Trim().Length > 0;
        if (await dialog.ShowOnceAsync() != ContentDialogResult.Primary) return null;

        var profile = edit ?? new UserProfile();
        profile.Name = name.Text.Trim();
        if (pickedNew && picked is not null)
        {
            try
            {
                // A copy in the data folder (a new name each time, the old picture may still be decoded in memory).
                var target = System.IO.Path.Combine(AppInfo.ProfilesDir, $"{profile.Id}-{DateTime.UtcNow:yyyyMMddHHmmssfff}{System.IO.Path.GetExtension(picked).ToLowerInvariant()}");
                File.Copy(picked, target, overwrite: true);
                var old = profile.Picture;
                profile.Picture = target;
                if (old is not null && !old.Equals(target, StringComparison.OrdinalIgnoreCase)) DeletePicture(old);
            }
            catch (Exception ex) { Log.Warn($"Profile picture: {ex.Message}"); }
        }
        if (edit is null) Add(profile);
        else
        {
            SettingsStore.Save();
            Changed?.Invoke();
        }
        return profile;
    }
}

using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using HybridLauncher;

namespace HybridLauncher.Helpers;

/// <summary>
/// One depth scale for the whole app: opaque surfaces that sit above the page (covers, tiles, menus) cast a soft shadow from
/// WinUI's own ThemeShadow, raised by a Z of <see cref="Rest"/>; things you can click rise a little on hover (<see cref="Lifted"/>,
/// a few pixels up) and settle back, animated on the compositor so it never waits for the UI thread. See-through surfaces (cards,
/// rows) get no shadow: it would show through them as a dirty patch; their depth comes from their lit-from-above fill
/// (<c>Palettes</c>). In XAML: <c>helpers:Depth.Z="10"</c>, <c>helpers:Depth.Hover="True"</c>.
/// </summary>
public static class Depth
{
    public const float Rest = 10, Lifted = 26;

    static ThemeShadow? _shadow;
    static ThemeShadow Shadow => _shadow ??= new ThemeShadow();

    /// <summary>Raises <paramref name="e"/> to <paramref name="z"/> with the shared shadow.</summary>
    public static void Raise(UIElement e, float z = Rest)
    {
        // Settings → Appearance → Shading off (the default): flat, no shadow.
        if (!AppState.Settings.Shading) return;
        try
        {
            e.Shadow = Shadow;
            e.Translation = new Vector3(0, 0, z);
        }
        catch (Exception ex) { HybridLauncher.Core.Log.Warn($"Depth: {ex.Message}"); }
    }

    /// <summary>
    /// Hover: up by 3 px and higher (a longer, softer shadow); back down when the pointer leaves. The handlers are always on and
    /// read Settings → Appearance → Shading each time, so turning it on or off applies to covers already on screen.
    /// </summary>
    public static void LiftOnHover(UIElement e, float rest = Rest, float lifted = Lifted)
    {
        Raise(e, rest);
        e.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(160) };
        void Down(object? _, object __)
        {
            if (AppState.Settings.Shading) Raise(e, rest);
            else if (e.Translation != Vector3.Zero) { e.Translation = Vector3.Zero; e.Shadow = null; }
        }
        e.PointerEntered += (_, _) =>
        {
            if (!AppState.Settings.Shading) { Down(null, null!); return; }
            e.Shadow = Shadow;
            e.Translation = new Vector3(0, -3, lifted);
        };
        e.PointerExited += Down;
        e.PointerCanceled += Down;
        e.PointerCaptureLost += Down;
    }

    public static readonly DependencyProperty ZProperty = DependencyProperty.RegisterAttached("Z", typeof(double), typeof(Depth),
        new PropertyMetadata(0.0, (d, e) => { if (d is UIElement u && e.NewValue is double z && z > 0) Raise(u, (float)z); }));

    public static double GetZ(DependencyObject o) => (double)o.GetValue(ZProperty);
    public static void SetZ(DependencyObject o, double v) => o.SetValue(ZProperty, v);

    public static readonly DependencyProperty HoverProperty = DependencyProperty.RegisterAttached("Hover", typeof(bool), typeof(Depth),
        new PropertyMetadata(false, (d, e) => { if (d is UIElement u && e.NewValue is true) LiftOnHover(u); }));

    public static bool GetHover(DependencyObject o) => (bool)o.GetValue(HoverProperty);
    public static void SetHover(DependencyObject o, bool v) => o.SetValue(HoverProperty, v);
}

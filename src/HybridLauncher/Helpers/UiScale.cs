using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace HybridLauncher.Helpers;

/// <summary>
/// Interface size (Settings → Appearance, GitHub issue #7), on top of Windows' display scale. WinUI has no zoom of its own, so
/// the window's root grid sits in <see cref="ScaleHost"/>, which lays it out at the window's size divided by the scale and
/// scales it back with a ScaleTransform: it always fills the window exactly, with more room at a smaller size and less at a
/// bigger one. Text and icons are drawn again at the new size (a static scale is rasterized at its final size). Menus, dialogs
/// and pop-ups keep Windows' size.
/// </summary>
public static class UiScale
{
    public static readonly int[] Steps = [50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200];

    /// <summary>The current factor (1 = 100%). Pixel snapping multiplies the display scale by it.</summary>
    public static double Factor { get; private set; } = 1;

    static ScaleHost? _host;
    static FrameworkElement? _root;

    /// <summary>Called once from the window's constructor, before anything in it is loaded.</summary>
    public static void Attach(Window window, FrameworkElement root, int percent)
    {
        _root = root;
        _host = new ScaleHost { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), RequestedTheme = ElementTheme.Dark };
        window.Content = null;
        _host.Children.Add(root);
        window.Content = _host;
        Set(percent);
    }

    public static void Set(int percent)
    {
        percent = Math.Clamp(percent, 50, 200);
        Factor = percent / 100.0;
        if (_root is null) return;
        _root.RenderTransform = percent == 100 ? null : new ScaleTransform { ScaleX = Factor, ScaleY = Factor };
        _host?.InvalidateMeasure();
    }
}

/// <summary>
/// The window's root panel: its one child is measured and arranged at the window's size divided by <see cref="UiScale.Factor"/>
/// from the very first layout pass (a Canvas sized afterwards made the game list switch modes twice before it showed, and it
/// was left folded with its room still taken).
/// </summary>
public sealed partial class ScaleHost : Panel
{
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size available)
    {
        double f = UiScale.Factor;
        var inner = new Windows.Foundation.Size(available.Width / f, available.Height / f);
        foreach (var child in Children) child.Measure(inner);
        return new Windows.Foundation.Size(double.IsInfinity(available.Width) ? 0 : available.Width, double.IsInfinity(available.Height) ? 0 : available.Height);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size final)
    {
        double f = UiScale.Factor;
        foreach (var child in Children) child.Arrange(new Windows.Foundation.Rect(0, 0, final.Width / f, final.Height / f));
        return final;
    }
}

/// <summary>
/// "Hide scroll bars" (Settings → Appearance, GitHub issue #8): every vertical scroll bar hidden while scrolling keeps working.
/// New scroll views take it from an implicit style; the ones already on screen (and those inside control templates, which an
/// implicit style does not reach) are set by walking the tree when a page opens or the setting changes.
/// </summary>
public static class ScrollBars
{
    static ResourceDictionary? _styles;
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DependencyObject, object> Ours = new();
    static readonly object Mark = new();

    public static bool Hidden => AppState.Settings.HideScrollBars;

    // Every scroll bar's thickness follows the window (GitHub issue #8): thinner in a small window, WinUI's full size from
    // 1920 px wide up. One shared transform per direction, so a resize changes them all at once without walking the tree.
    static readonly ScaleTransform Vertical = new(), Horizontal = new();
    static ResourceDictionary? _fitStyle;

    public static readonly DependencyProperty FitProperty = DependencyProperty.RegisterAttached("Fit", typeof(bool), typeof(ScrollBars),
        new PropertyMetadata(false, (d, e) => { if (d is ScrollBar bar && e.NewValue is true) Hook(bar); }));

    public static bool GetFit(DependencyObject o) => (bool)o.GetValue(FitProperty);
    public static void SetFit(DependencyObject o, bool v) => o.SetValue(FitProperty, v);

    static void Hook(ScrollBar bar)
    {
        void Place()
        {
            bool vertical = bar.Orientation == Orientation.Vertical;
            bar.RenderTransform = vertical ? Vertical : Horizontal;
            // Thinner toward the page's edge, so the bar stays against it.
            bar.RenderTransformOrigin = vertical ? new Windows.Foundation.Point(1, 0.5) : new Windows.Foundation.Point(0.5, 1);
        }
        Place();
        bar.RegisterPropertyChangedCallback(ScrollBar.OrientationProperty, (_, _) => Place());
    }

    /// <summary>The thickness for a window <paramref name="width"/> DIPs wide: half at 800 or less, full from 1920.</summary>
    public static void FitToWindow(double width)
    {
        if (width <= 0) return;
        double s = Math.Round(Math.Clamp(0.5 + (width - 800) / (1920 - 800) * 0.5, 0.5, 1), 2);
        if (Math.Abs(Vertical.ScaleX - s) < 0.005) return;
        Vertical.ScaleX = s;
        Horizontal.ScaleY = s;
    }

    /// <summary>Once at start: every ScrollBar in the app (templates included) takes the window-sized thickness.</summary>
    static void InstallFit()
    {
        if (_fitStyle is not null) return;
        _fitStyle = new ResourceDictionary();
        var style = new Style(typeof(ScrollBar));
        if (Application.Current.Resources.TryGetValue("DefaultScrollBarStyle", out var b) && b is Style basis) style.BasedOn = basis;
        style.Setters.Add(new Setter(FitProperty, true));
        _fitStyle[typeof(ScrollBar)] = style;
        Application.Current.Resources.MergedDictionaries.Add(_fitStyle);
    }

    /// <summary>Adds or removes the implicit styles to match the setting.</summary>
    public static void UpdateStyles()
    {
        try { InstallFit(); }
        catch (Exception ex) { HybridLauncher.Core.Log.Warn($"Scroll bar size: {ex.Message}"); }
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_styles is not null) { merged.Remove(_styles); _styles = null; }
        if (!Hidden) return;
        _styles = new ResourceDictionary();
        var res = Application.Current.Resources;
        var sv = new Style(typeof(ScrollViewer));
        if (res.TryGetValue("DefaultScrollViewerStyle", out var svBase) && svBase is Style b1) sv.BasedOn = b1;
        sv.Setters.Add(new Setter(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden));
        _styles[typeof(ScrollViewer)] = sv;
        var view = new Style(typeof(ScrollView));
        if (res.TryGetValue("DefaultScrollViewStyle", out var vBase) && vBase is Style b2) view.BasedOn = b2;
        view.Setters.Add(new Setter(ScrollView.VerticalScrollBarVisibilityProperty, ScrollingScrollBarVisibility.Hidden));
        _styles[typeof(ScrollView)] = view;
        merged.Add(_styles);
    }

    /// <summary>Applies the setting to every scroll view under <paramref name="root"/>.</summary>
    public static void Apply(DependencyObject? root)
    {
        if (root is null) return;
        try { Walk(root, Hidden); }
        catch (Exception ex) { HybridLauncher.Core.Log.Warn($"Scroll bars: {ex.Message}"); }
    }

    static void Walk(DependencyObject d, bool hide)
    {
        if (d is ScrollViewer sv) Set(sv, ScrollViewer.VerticalScrollBarVisibilityProperty, hide, ScrollBarVisibility.Hidden, ScrollBarVisibility.Disabled);
        else if (d is ScrollView view) Set(view, ScrollView.VerticalScrollBarVisibilityProperty, hide, ScrollingScrollBarVisibility.Hidden, null);
        // A list's own scroller takes the list's attached value through its template.
        if (d is ListViewBase list) Set(list, ScrollViewer.VerticalScrollBarVisibilityProperty, hide, ScrollBarVisibility.Hidden, ScrollBarVisibility.Disabled);
        int n = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i), hide);
    }

    // Only scroll bars that were left to their default (or that this class hid) change: a bar hidden or turned off on purpose
    // stays as it was.
    static void Set(DependencyObject o, DependencyProperty p, bool hide, object hidden, object? off)
    {
        bool ours = Ours.TryGetValue(o, out _);
        var local = o.ReadLocalValue(p);
        if (hide)
        {
            if (ours) return;
            if (local != DependencyProperty.UnsetValue && (Equals(local, hidden) || (off is not null && Equals(local, off)))) return;
            if (local != DependencyProperty.UnsetValue && local is not ScrollBarVisibility and not ScrollingScrollBarVisibility) return;
            Ours.AddOrUpdate(o, (local, Mark));
            o.SetValue(p, hidden);
        }
        else if (ours && Ours.TryGetValue(o, out var saved) && saved is ValueTuple<object, object> t)
        {
            Ours.Remove(o);
            if (t.Item1 == DependencyProperty.UnsetValue) o.ClearValue(p);
            else o.SetValue(p, t.Item1);
        }
    }
}

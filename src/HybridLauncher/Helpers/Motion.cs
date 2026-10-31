using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace HybridLauncher.Helpers;

/// <summary>
/// Short, light animations for folding and opening parts of a page: the content fades and slides a few pixels, the chevron
/// turns. Only opacity and transforms are animated (no layout per frame), so they stay smooth at any refresh rate.
/// </summary>
public static class Motion
{
    static readonly TimeSpan OpenTime = TimeSpan.FromMilliseconds(200), CloseTime = TimeSpan.FromMilliseconds(130);

    // The storyboard running on an element: a new open / close stops it first, so a quick second click never fights the first.
    sealed class State
    {
        public int Gen;
        public Storyboard? Running;
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, State> States = new();

    static int Start(FrameworkElement e, Storyboard sb)
    {
        var s = States.GetOrCreateValue(e);
        s.Running?.Stop();
        s.Gen++;
        s.Running = sb;
        sb.Begin();
        return s.Gen;
    }

    /// <summary>Shows <paramref name="body"/> with a short fade and slide down.</summary>
    public static void Open(FrameworkElement body)
    {
        body.Visibility = Visibility.Visible;
        var move = Translate(body);
        var sb = new Storyboard { FillBehavior = FillBehavior.Stop };
        sb.Children.Add(Anim(body, "Opacity", 0, 1, OpenTime));
        sb.Children.Add(Anim(move, "Y", -10, 0, OpenTime));
        Start(body, sb);
    }

    /// <summary>Hides <paramref name="body"/> after a quick fade and slide up; <paramref name="done"/> runs once it is collapsed.</summary>
    public static void Close(FrameworkElement body, Action? done = null)
    {
        var move = Translate(body);
        var sb = new Storyboard();
        sb.Children.Add(Anim(body, "Opacity", 1, 0, CloseTime));
        sb.Children.Add(Anim(move, "Y", 0, -6, CloseTime));
        int gen = Start(body, sb);
        bool finished = false;
        void Finish()
        {
            if (finished) return;
            finished = true;
            if (!States.TryGetValue(body, out var s) || s.Gen != gen) return;
            body.Visibility = Visibility.Collapsed;
            sb.Stop();
            s.Running = null;
            done?.Invoke();
        }
        sb.Completed += (_, _) => Finish();
        // A page that is not drawn (minimised window) may never finish the storyboard: collapse anyway.
        if (body.DispatcherQueue is { } queue) Later.Run(queue, CloseTime + TimeSpan.FromMilliseconds(150), Finish);
    }

    /// <summary>A chevron that points down when open and right (−90°) when folded, turning between the two.</summary>
    public static FontIcon Chevron(bool open, double size = 12) => new()
    {
        Glyph = "",
        FontSize = size,
        RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
        RenderTransform = new RotateTransform { Angle = open ? 0 : -90 },
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Turns a rotation to <paramref name="to"/> degrees; the end angle stays as the real value.</summary>
    public static void Turn(RotateTransform r, double to, int ms)
    {
        var sb = new Storyboard();
        sb.Children.Add(Anim(r, "Angle", r.Angle, to, TimeSpan.FromMilliseconds(ms)));
        sb.Completed += (_, _) => { r.Angle = to; sb.Stop(); };
        sb.Begin();
    }

    public static void Turn(FontIcon chevron, bool open)
    {
        if (chevron.RenderTransform is not RotateTransform r)
        {
            r = new RotateTransform();
            chevron.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            chevron.RenderTransform = r;
        }
        double to = open ? 0 : -90;
        var a = Anim(r, "Angle", r.Angle, to, OpenTime);
        var sb = new Storyboard();
        sb.Children.Add(a);
        // The end angle is kept as the real value, so the next turn starts from it.
        sb.Completed += (_, _) => { r.Angle = to; sb.Stop(); };
        sb.Begin();
    }

    /// <summary>Fades an element in (pictures, freshly built content).</summary>
    public static void FadeIn(UIElement element, int ms = 250)
    {
        var sb = new Storyboard();
        sb.Children.Add(Anim(element, "Opacity", 0, 1, TimeSpan.FromMilliseconds(ms)));
        sb.Begin();
    }

    /// <summary>
    /// Eases a transform's property ("TranslateY", "ScaleX"...) from where it is to <paramref name="to"/>. The value is set to
    /// its end at once and the animation only draws the way there (it does not hold), so code can set the property again later.
    /// </summary>
    public static void To(CompositeTransform transform, string property, double to, int ms)
    {
        var dp = property switch
        {
            "TranslateX" => CompositeTransform.TranslateXProperty,
            "TranslateY" => CompositeTransform.TranslateYProperty,
            "ScaleX" => CompositeTransform.ScaleXProperty,
            "ScaleY" => CompositeTransform.ScaleYProperty,
            _ => throw new ArgumentException(property),
        };
        double from = (double)transform.GetValue(dp);
        transform.SetValue(dp, to);
        var a = new DoubleAnimation { From = from, To = to, Duration = TimeSpan.FromMilliseconds(ms), FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(a, transform);
        Storyboard.SetTargetProperty(a, property);
        var sb = new Storyboard();
        sb.Children.Add(a);
        sb.Begin();
    }

    static TranslateTransform Translate(UIElement e)
    {
        if (e.RenderTransform is TranslateTransform t) return t;
        t = new TranslateTransform();
        e.RenderTransform = t;
        return t;
    }

    static DoubleAnimation Anim(DependencyObject target, string property, double from, double to, TimeSpan time)
    {
        var a = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = time,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(a, target);
        Storyboard.SetTargetProperty(a, property);
        return a;
    }
}

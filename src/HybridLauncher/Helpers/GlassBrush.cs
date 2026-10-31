using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HybridLauncher.Helpers;

/// <summary>
/// Hybrid Launcher's glass, as Steam draws its library header: what is behind the element, blurred, under a tint. A brush like any
/// other (a Background, a button's resource), so the element's own rounded corners shape it smoothly and its states can swap
/// tints. Drawn by the compositor itself, not by Windows' acrylic: it stays glass when "Transparency effects" are off in
/// Windows (acrylic and Mica then turn into flat colour), and it costs nothing on the UI thread while scrolling.
///
/// Where it goes (Hybrid Launcher's surfaces, one rule for every page): anything laid over a picture (the banner's buttons and links,
/// the launch bar, the tab bar, overlays on covers and on the Downloads card) is glass; cards, rows and panels on the page's
/// own background are plain fills a step lighter than it, never outlined.
/// </summary>
public sealed partial class GlassBrush : XamlCompositionBrushBase
{
    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(nameof(Tint), typeof(Color), typeof(GlassBrush),
        new PropertyMetadata(Color.FromArgb(0x66, 0x16, 0x17, 0x1B), (d, _) => ((GlassBrush)d).Update()));

    public static readonly DependencyProperty BlurProperty = DependencyProperty.Register(nameof(Blur), typeof(double), typeof(GlassBrush),
        new PropertyMetadata(18.0, (d, _) => ((GlassBrush)d).Update()));

    /// <summary>The colour laid over the blur; its alpha is how much of the blurred picture still shows.</summary>
    public Color Tint { get => (Color)GetValue(TintProperty); set => SetValue(TintProperty, value); }

    /// <summary>The blur radius in pixels.</summary>
    public double Blur { get => (double)GetValue(BlurProperty); set => SetValue(BlurProperty, value); }

    public GlassBrush() => FallbackColor = Color.FromArgb(0xE0, 0x1C, 0x1D, 0x22);

    public GlassBrush(Color tint, double blur = 18) : this()
    {
        Tint = tint;
        Blur = blur;
    }

    // One effect description per compositor (it is compiled once); every brush is an instance of it with its own values.
    static CompositionEffectFactory? _factory;
    static Compositor? _factoryCompositor;

    protected override void OnConnected()
    {
        if (CompositionBrush is not null) return;
        try
        {
            var compositor = CompositionTarget.GetCompositorForCurrentThread();
            if (_factory is null || _factoryCompositor != compositor)
            {
                var graph = new CompositeEffect
                {
                    Mode = CanvasComposite.SourceOver,
                    Sources =
                    {
                        new SaturationEffect
                        {
                            Saturation = 1.15f,
                            Source = new GaussianBlurEffect
                            {
                                Name = "Blur", BlurAmount = 18, BorderMode = EffectBorderMode.Hard,
                                // Speed: the blur runs on a downsampled copy. At these radii it looks the same and costs a fraction of
                                // the GPU time, which is what every frame of a scroll pays for each glass surface on screen.
                                Optimization = EffectOptimization.Speed,
                                Source = new CompositionEffectSourceParameter("backdrop"),
                            },
                        },
                        new ColorSourceEffect { Name = "Tint", Color = Tint },
                    },
                };
                _factory = compositor.CreateEffectFactory(graph, ["Blur.BlurAmount", "Tint.Color"]);
                _factoryCompositor = compositor;
            }
            var brush = _factory.CreateBrush();
            brush.SetSourceParameter("backdrop", compositor.CreateBackdropBrush());
            CompositionBrush = brush;
            Update();
        }
        catch (Exception ex)
        {
            HybridLauncher.Core.Log.Warn($"Glass: {ex.Message}");
            CompositionBrush = CompositionTarget.GetCompositorForCurrentThread().CreateColorBrush(FallbackColor);
        }
    }

    protected override void OnDisconnected()
    {
        CompositionBrush?.Dispose();
        CompositionBrush = null;
    }

    void Update()
    {
        if (CompositionBrush is not CompositionEffectBrush brush) return;
        brush.Properties.InsertScalar("Blur.BlurAmount", (float)Blur);
        brush.Properties.InsertColor("Tint.Color", Tint);
    }
}

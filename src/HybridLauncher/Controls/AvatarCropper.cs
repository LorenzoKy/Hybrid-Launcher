using HybridLauncher.Core;
using Microsoft.UI.Input;
using HybridLauncher.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace HybridLauncher.Controls;

/// <summary>
/// The profile picture's framing (2.1.4): the picture under a round window, dragged to move it and zoomed with the wheel or
/// the slider, then saved as a square PNG of what the circle shows. The picture always covers the circle.
/// 2.3-rc.6: shown and saved from the same decode, turned as the photo's EXIF says (phone photos were saved squeezed or on
/// their side), the wheel zooms about the pointer and by how far it turns (a touchpad jumped 10% per tiny step), any file
/// name loads (a '#' in it showed nothing), a double-click starts over, and a picture Windows cannot read is said so.
/// </summary>
public sealed partial class AvatarCropper : StackPanel
{
    const double V = 220;
    const int OutSize = 512;
    const double MaxZoom = 4;

    readonly string _path;
    readonly Image _image = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    readonly TranslateTransform _move = new();
    readonly Slider _zoom = new() { Minimum = 1, Maximum = MaxZoom, StepFrequency = 0.01, Value = 1 };
    readonly TextBlock _problem = new()
    {
        TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Width = V - 40, FontSize = 12, Opacity = 0.8,
        Visibility = Visibility.Collapsed, IsHitTestVisible = false,
    };
    // The picture as it stands up (EXIF turn applied), in pixels.
    double _iw, _ih, _zoomValue = 1, _ox, _oy;
    // The point the next zoom keeps still, from the circle's centre (the wheel's pointer; the slider zooms about the centre).
    double _anchorX, _anchorY;
    Windows.Foundation.Point? _drag;

    /// <summary>Whether the user moved or zoomed the picture.</summary>
    public bool Changed { get; private set; }

    /// <summary>The picture was read and can be framed (false: Windows has no decoder for it, or the file is damaged).</summary>
    public bool Readable => _iw > 0;

    public AvatarCropper(string path)
    {
        _path = path;
        Spacing = 10;
        HorizontalAlignment = HorizontalAlignment.Center;

        var canvas = new Canvas { Width = V, Height = V, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x10, 0x10, 0x14)) };
        canvas.Clip = new RectangleGeometry { Rect = new(0, 0, V, V) };
        _image.RenderTransform = _move;
        canvas.Children.Add(_image);
        // Outside the circle dimmed, the circle ringed: what the avatar will show.
        var mask = new GeometryGroup { FillRule = FillRule.EvenOdd };
        mask.Children.Add(new RectangleGeometry { Rect = new(0, 0, V, V) });
        mask.Children.Add(new EllipseGeometry { Center = new(V / 2, V / 2), RadiusX = V / 2 - 1, RadiusY = V / 2 - 1 });
        canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = mask, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0xA8, 0, 0, 0)), IsHitTestVisible = false });
        canvas.Children.Add(new Ellipse { Width = V - 2, Height = V - 2, Margin = new Thickness(1), Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1.5, IsHitTestVisible = false });
        Canvas.SetLeft(_problem, 20);
        Canvas.SetTop(_problem, V / 2 - 30);
        canvas.Children.Add(_problem);
        var frame = new Border { CornerRadius = new CornerRadius(12), Child = canvas, HorizontalAlignment = HorizontalAlignment.Center };
        Children.Add(frame);

        canvas.PointerPressed += (_, e) =>
        {
            if (!Readable) return;
            _drag = e.GetCurrentPoint(canvas).Position;
            canvas.CapturePointer(e.Pointer);
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
            e.Handled = true;
        };
        canvas.PointerMoved += (_, e) =>
        {
            if (_drag is not { } from) return;
            var now = e.GetCurrentPoint(canvas).Position;
            if (now.X == from.X && now.Y == from.Y) return;
            _ox += now.X - from.X;
            _oy += now.Y - from.Y;
            _drag = now;
            Changed = true;
            Layout();
        };
        void EndDrag() { _drag = null; ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand); }
        canvas.PointerReleased += (_, e) => { canvas.ReleasePointerCapture(e.Pointer); EndDrag(); };
        canvas.PointerCaptureLost += (_, _) => EndDrag();
        canvas.PointerEntered += (_, _) => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
        canvas.PointerWheelChanged += (_, e) =>
        {
            e.Handled = true;
            if (!Readable) return;
            var point = e.GetCurrentPoint(canvas);
            // A notch of a mouse wheel is 120: 10% a notch, and a touchpad's small steps zoom by as little as they turn.
            double factor = Math.Pow(1.1, point.Properties.MouseWheelDelta / 120.0);
            ZoomTo(_zoom.Value * factor, point.Position.X - V / 2, point.Position.Y - V / 2);
        };
        canvas.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            if (!Readable) return;
            _ox = _oy = 0;
            ZoomTo(1, 0, 0);
            Layout();
        };

        var row = new Grid { ColumnSpacing = 8, Width = V, HorizontalAlignment = HorizontalAlignment.Center };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new FontIcon { Glyph = "", FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_zoom, 1);
        row.Children.Add(_zoom);
        var plus = new FontIcon { Glyph = "", FontSize = 14, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(plus, 2);
        row.Children.Add(plus);
        ToolTipService.SetToolTip(_zoom, L.T("Zoom"));
        Children.Add(row);
        _zoom.ValueChanged += (_, e) =>
        {
            // The point kept still (the wheel's pointer, else the circle's centre) stays under the same spot of the picture.
            double k = e.NewValue / _zoomValue;
            _ox = _anchorX - (_anchorX - _ox) * k;
            _oy = _anchorY - (_anchorY - _oy) * k;
            _anchorX = _anchorY = 0;
            _zoomValue = e.NewValue;
            if (Readable) Changed = true;
            Layout();
        };

        Loaded += async (_, _) => await LoadAsync();
    }

    void ZoomTo(double value, double anchorX, double anchorY)
    {
        value = Math.Clamp(value, _zoom.Minimum, _zoom.Maximum);
        if (Math.Abs(value - _zoom.Value) < 1e-6) return;
        _anchorX = anchorX;
        _anchorY = anchorY;
        _zoom.Value = value;
    }

    bool _loaded;

    async Task LoadAsync()
    {
        // Loaded comes again each time the dialog's content is laid out anew: the picture is read once.
        if (_loaded) return;
        _loaded = true;
        try
        {
            // The file's bytes (not a URI: a '#' or '%' in the name made the preview blank), turned upright as the photo's
            // EXIF says, at no more than the most zoom can show sharply: the same decode the saved picture comes from.
            var (bitmap, w, h) = await DecodeAsync(_path, full => Math.Min(1, V * MaxZoom * 2 / full));
            _iw = w;
            _ih = h;
            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(bitmap);
            bitmap.Dispose();
            _image.Source = source;
            _image.Opacity = 0;
            Layout();
            Motion.FadeIn(_image, 180);
        }
        catch (Exception ex)
        {
            _iw = _ih = 0;
            _problem.Text = L.T("This picture can't be framed. It will be used as it is.");
            _problem.Visibility = Visibility.Visible;
            _zoom.IsEnabled = false;
            Log.Warn($"Profile picture framing: {ex.GetType().Name} {ex.Message} (0x{ex.HResult:X8})");
        }
    }

    /// <summary>
    /// The picture at <paramref name="path"/>, upright (EXIF turn applied) and scaled by what <paramref name="scaleFor"/> gives
    /// for its shorter side, in BGRA premultiplied; with its upright size before scaling.
    /// </summary>
    static async Task<(SoftwareBitmap Bitmap, double Width, double Height)> DecodeAsync(string path, Func<double, double> scaleFor)
    {
        using var stream = await Helpers.LocalFile.OpenReadAsync(path);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        double w = decoder.OrientedPixelWidth, h = decoder.OrientedPixelHeight;
        if (w <= 0 || h <= 0) throw new InvalidDataException("the picture has no size");
        double k = scaleFor(Math.Min(w, h));
        // The scale is the same both ways, so it can be given in the stored (not yet turned) frame.
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Ceiling(decoder.PixelWidth * k)),
            ScaledHeight = (uint)Math.Max(1, Math.Ceiling(decoder.PixelHeight * k)),
            InterpolationMode = k < 1 ? BitmapInterpolationMode.Fant : BitmapInterpolationMode.Cubic,
        };
        var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        return (bitmap, w, h);
    }

    double BaseScale => V / Math.Min(_iw, _ih);

    void Layout()
    {
        if (_iw <= 0) return;
        double s = BaseScale * _zoomValue, w = _iw * s, h = _ih * s;
        double mx = (w - V) / 2, my = (h - V) / 2;
        _ox = Math.Clamp(_ox, -mx, mx);
        _oy = Math.Clamp(_oy, -my, my);
        _image.Width = w;
        _image.Height = h;
        _move.X = V / 2 + _ox - w / 2;
        _move.Y = V / 2 + _oy - h / 2;
    }

    /// <summary>Self-test: the picture's upright size once read (0 × 0 when it could not be).</summary>
    internal (double Width, double Height) SelfTestSize => (_iw, _ih);

    /// <summary>Self-test: the zoom (through the slider, about the given point from the centre) and a drag by dx, dy.</summary>
    internal void SelfTestFrame(double zoom, double anchorX, double anchorY, double dx, double dy)
    {
        ZoomTo(zoom, anchorX, anchorY);
        _ox += dx;
        _oy += dy;
        Changed = true;
        Layout();
    }

    /// <summary>
    /// Writes what the circle shows as a square PNG at <paramref name="target"/>. False when the picture could not be read
    /// (nothing is written: the caller keeps the file as it is).
    /// </summary>
    public async Task<bool> SaveAsync(string target)
    {
        if (!Readable) return false;
        // Scaled so the circle is OutSize pixels wide, upright; then the circle's square is cut out of it.
        double f = OutSize * _zoomValue / Math.Min(_iw, _ih);
        var (bitmap, _, _) = await DecodeAsync(_path, _ => f);
        byte[] square;
        using (bitmap)
        {
            int w = bitmap.PixelWidth, h = bitmap.PixelHeight;
            double s = BaseScale * _zoomValue;
            // The circle's left / top edge on the picture, in output pixels.
            double left = (_iw * s / 2 - V / 2 - _ox) / s * f, top = (_ih * s / 2 - V / 2 - _oy) / s * f;
            int x = (int)Math.Clamp(Math.Round(left), 0, Math.Max(0, w - OutSize)), y = (int)Math.Clamp(Math.Round(top), 0, Math.Max(0, h - OutSize));
            var all = new byte[w * h * 4];
            bitmap.CopyToBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(all));
            square = await Task.Run(() =>
            {
                var cut = new byte[OutSize * OutSize * 4];
                int rows = Math.Min(OutSize, h - y), bytes = Math.Min(OutSize, w - x) * 4;
                for (int r = 0; r < rows; r++) System.Buffer.BlockCopy(all, ((y + r) * w + x) * 4, cut, r * OutSize * 4, bytes);
                return cut;
            });
        }
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, OutSize, OutSize, 96, 96, square);
        await encoder.FlushAsync();
        output.Seek(0);
        await using var fs = File.Create(target);
        await output.AsStreamForRead().CopyToAsync(fs);
        return true;
    }
}

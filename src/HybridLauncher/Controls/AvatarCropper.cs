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
/// </summary>
public sealed class AvatarCropper : StackPanel
{
    const double V = 220;
    const int OutSize = 512;

    readonly string _path;
    readonly Image _image = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    readonly TranslateTransform _move = new();
    readonly Slider _zoom = new() { Minimum = 1, Maximum = 4, StepFrequency = 0.01, Value = 1, Width = V };
    double _iw, _ih, _zoomValue = 1, _ox, _oy;
    Windows.Foundation.Point? _drag;

    /// <summary>Whether the user moved or zoomed the picture.</summary>
    public bool Changed { get; private set; }

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
        var frame = new Border { CornerRadius = new CornerRadius(12), Child = canvas, HorizontalAlignment = HorizontalAlignment.Center };
        Children.Add(frame);

        canvas.PointerPressed += (_, e) =>
        {
            _drag = e.GetCurrentPoint(canvas).Position;
            canvas.CapturePointer(e.Pointer);
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
            e.Handled = true;
        };
        canvas.PointerMoved += (_, e) =>
        {
            if (_drag is not { } from) return;
            var now = e.GetCurrentPoint(canvas).Position;
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
            int delta = e.GetCurrentPoint(canvas).Properties.MouseWheelDelta;
            _zoom.Value = Math.Clamp(_zoom.Value * (delta > 0 ? 1.1 : 1 / 1.1), _zoom.Minimum, _zoom.Maximum);
            e.Handled = true;
        };

        var row = new Grid { ColumnSpacing = 8, Width = V, HorizontalAlignment = HorizontalAlignment.Center };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new FontIcon { Glyph = "", FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
        _zoom.Width = double.NaN;
        Grid.SetColumn(_zoom, 1);
        row.Children.Add(_zoom);
        var plus = new FontIcon { Glyph = "", FontSize = 14, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(plus, 2);
        row.Children.Add(plus);
        ToolTipService.SetToolTip(_zoom, L.T("Zoom"));
        Children.Add(row);
        _zoom.ValueChanged += (_, e) =>
        {
            // Zoom about the circle's centre: the point under it stays put.
            double k = e.NewValue / _zoomValue;
            _ox *= k;
            _oy *= k;
            _zoomValue = e.NewValue;
            Changed = true;
            Layout();
        };

        Loaded += async (_, _) => await LoadAsync();
    }

    async Task LoadAsync()
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(_path);
            using (var stream = await file.OpenReadAsync())
            {
                var decoder = await BitmapDecoder.CreateAsync(stream);
                _iw = decoder.OrientedPixelWidth;
                _ih = decoder.OrientedPixelHeight;
            }
            if (_iw <= 0 || _ih <= 0) return;
            // Decoded no larger than the most zoom can show sharply.
            int decode = (int)Math.Min(_iw, Math.Ceiling(V * _zoom.Maximum * 2 * _iw / Math.Min(_iw, _ih)));
            _image.Source = new BitmapImage { UriSource = new Uri(_path), DecodePixelWidth = decode };
            _image.Opacity = 0;
            _image.ImageOpened += (_, _) => Motion.FadeIn(_image, 180);
            Layout();
        }
        catch (Exception ex) { Log.Warn($"Profile picture framing: {ex.Message}"); }
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

    /// <summary>Writes what the circle shows as a square PNG at <paramref name="target"/>.</summary>
    public async Task SaveAsync(string target)
    {
        if (_iw <= 0) { File.Copy(_path, target, overwrite: true); return; }
        double s = BaseScale * _zoomValue;
        double f = OutSize * s / V;                       // output pixels per source pixel
        uint sw = (uint)Math.Max(OutSize, Math.Round(_iw * f)), sh = (uint)Math.Max(OutSize, Math.Round(_ih * f));
        double left = (_iw * s / 2 - V / 2 - _ox) / s * f, top = (_ih * s / 2 - V / 2 - _oy) / s * f;
        uint x = (uint)Math.Clamp(Math.Round(left), 0, sw - OutSize), y = (uint)Math.Clamp(Math.Round(top), 0, sh - OutSize);
        var file = await StorageFile.GetFileFromPathAsync(_path);
        byte[] pixels;
        using (var stream = await file.OpenReadAsync())
        {
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var transform = new BitmapTransform
            {
                ScaledWidth = sw, ScaledHeight = sh, InterpolationMode = BitmapInterpolationMode.Fant,
                Bounds = new BitmapBounds { X = x, Y = y, Width = OutSize, Height = OutSize },
            };
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
            pixels = data.DetachPixelData();
        }
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, OutSize, OutSize, 96, 96, pixels);
        await encoder.FlushAsync();
        output.Seek(0);
        await using var fs = File.Create(target);
        await output.AsStreamForRead().CopyToAsync(fs);
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace HybridLauncher.Controls;

/// <summary>
/// The Dashboard's layout (1.4.0-rc.7): as many columns as the width holds (each at least <see cref="MinColumn"/> wide, at most
/// <see cref="MaxColumns"/>), each card placed in order at the highest spot it fits (a hole left above by a wide card is filled
/// by the next card that fits it), so the page fills both across and down without the holes of two fixed stacks. A card's width in columns is its
/// <see cref="SpanProperty"/> (1, or 2 for a wide card; never more than the columns there are). Collapsed cards take no room.
/// </summary>
public sealed partial class MasonryPanel : Panel
{
    public static readonly DependencyProperty SpanProperty =
        DependencyProperty.RegisterAttached("Span", typeof(int), typeof(MasonryPanel), new PropertyMetadata(1, (d, _) => (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(d) as MasonryPanel)?.InvalidateMeasure()));

    public static int GetSpan(DependencyObject d) => (int)d.GetValue(SpanProperty);
    public static void SetSpan(DependencyObject d, int span) => d.SetValue(SpanProperty, span);

    public double MinColumn { get; set; } = 360;
    public int MaxColumns { get; set; } = 4;
    public double Gap { get; set; } = 14;

    /// <summary>
    /// Rows instead of the masonry (2.0.0-rc.11): the cards in reading order, <see cref="Columns"/> to a row, each row as tall
    /// as its tallest card and every card in it stretched to that height. A card that grows only makes its row taller; no card
    /// changes place. Spans are ignored.
    /// </summary>
    public bool Rows { get; set; }

    /// <summary>The columns of the last layout (the cards ask it to pick their own inner layout).</summary>
    public int Columns { get; private set; } = 1;

    public event Action<int>? ColumnsChanged;

    Rect[] _rects = [];

    int ColumnsFor(double width) => Math.Clamp((int)((width + Gap) / (MinColumn + Gap)), 1, MaxColumns);

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? MinColumn * 2 + Gap : available.Width;
        int cols = ColumnsFor(width);
        if (cols != Columns)
        {
            Columns = cols;
            ColumnsChanged?.Invoke(cols);
        }
        double colWidth = (width - Gap * (cols - 1)) / cols;
        if (Rows) return MeasureRows(cols, colWidth, width);
        // Placed cards as (first column, columns, top, bottom incl. the gap below).
        var placed = new List<(int Col, int Span, double Top, double Bottom)>();
        _rects = new Rect[Children.Count];
        double height = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            // A collapsed child is measured too (it takes no room): WinUI tells a panel that a child became visible only when the
            // panel measured that child before. Skipped, the Dashboard's side cards held back by RevealInTurns (2.0.0-rc.9) came
            // back Visible but never measured, so the side column stayed empty (rc.10).
            if (child.Visibility == Visibility.Collapsed) { child.Measure(new Size(colWidth, double.PositiveInfinity)); _rects[i] = default; continue; }
            int span = Math.Clamp(GetSpan(child), 1, cols);
            double w = colWidth * span + Gap * (span - 1);
            child.Measure(new Size(w, double.PositiveInfinity));
            double h = child.DesiredSize.Height;
            if (h <= 0) { _rects[i] = new Rect(0, 0, w, 0); continue; }
            // Dense: the highest spot where the card fits, a hole left above by a wide card included (the first column on a tie).
            int bestCol = 0;
            double bestTop = double.MaxValue;
            var tops = placed.Select(p => p.Bottom).Prepend(0).Distinct().OrderBy(t => t).ToList();
            for (int c = 0; c + span <= cols; c++)
                foreach (var top in tops)
                {
                    if (top >= bestTop - 0.5) break;
                    bool free = true;
                    foreach (var p in placed)
                        if (p.Col < c + span && c < p.Col + p.Span && p.Top < top + h + Gap - 0.5 && top < p.Bottom - 0.5) { free = false; break; }
                    if (free) { bestTop = top; bestCol = c; break; }
                }
            placed.Add((bestCol, span, bestTop, bestTop + h + Gap));
            _rects[i] = new Rect(bestCol * (colWidth + Gap), bestTop, w, h);
            height = Math.Max(height, bestTop + h);
        }
        return new Size(width, height);
    }

    Size MeasureRows(int cols, double colWidth, double width)
    {
        _rects = new Rect[Children.Count];
        double top = 0;
        bool any = false;
        var row = new List<int>(cols);
        void Close()
        {
            if (row.Count == 0) return;
            double h = row.Max(i => Children[i].DesiredSize.Height);
            for (int k = 0; k < row.Count; k++) _rects[row[k]] = new Rect(k * (colWidth + Gap), top, colWidth, h);
            top += h + Gap;
            any = true;
            row.Clear();
        }
        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            // Collapsed ones measured too (see MeasureOverride), and take no place in a row.
            child.Measure(new Size(colWidth, double.PositiveInfinity));
            if (child.Visibility == Visibility.Collapsed || child.DesiredSize.Height <= 0) { _rects[i] = default; continue; }
            row.Add(i);
            if (row.Count == cols) Close();
        }
        Close();
        return new Size(width, any ? top - Gap : 0);
    }

    protected override Size ArrangeOverride(Size final)
    {
        for (int i = 0; i < Children.Count && i < _rects.Length; i++)
            Children[i].Arrange(_rects[i]);
        return final;
    }
}

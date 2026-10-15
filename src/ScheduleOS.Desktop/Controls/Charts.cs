using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ScheduleOS.Desktop.Converters;

namespace ScheduleOS.Desktop.Controls;

public sealed record ChartPoint(string Label, double Value, string Display, bool IsHighlight = false);

public sealed record ChartSlice(string Label, double Value, Color Color, string Display)
{
    public Brush Brush => Palette.Frozen(Color);
}

/// <summary>Lightweight vertical bar chart rendered with WPF drawing primitives.</summary>
public sealed class BarChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points), typeof(IReadOnlyList<ChartPoint>), typeof(BarChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AxisFormatProperty = DependencyProperty.Register(nameof(AxisFormat), typeof(string), typeof(BarChart),
        new FrameworkPropertyMetadata("number", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(nameof(EmptyText), typeof(string), typeof(BarChart),
        new FrameworkPropertyMetadata("No data for this period", FrameworkPropertyMetadataOptions.AffectsRender));

    private int _hover = -1;

    public IReadOnlyList<ChartPoint>? Points
    {
        get => (IReadOnlyList<ChartPoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public string AxisFormat
    {
        get => (string)GetValue(AxisFormatProperty);
        set => SetValue(AxisFormatProperty, value);
    }

    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    private Brush R(string key, Color fallback) => TryFindResource(key) as Brush ?? Palette.Frozen(fallback);

    private string FormatAxis(double v) => AxisFormat == "money" ? Formats.Currency.FormatCompact((long)v) : v.ToString("0.#", CultureInfo.InvariantCulture);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var pts = Points;
        if (pts is null || pts.Count == 0) return;
        var x = e.GetPosition(this).X;
        const double left = 46;
        var slot = (ActualWidth - left) / pts.Count;
        var idx = slot <= 0 ? -1 : (int)((x - left) / slot);
        if (idx < 0 || idx >= pts.Count) idx = -1;
        if (idx == _hover) return;
        _hover = idx;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = -1;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 40 || h < 40) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var text = R("Brush.TextSecondary", Colors.Gray);
        var grid = new Pen(R("Brush.GridLine", Colors.LightGray), 1);
        grid.Freeze();
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Txt(string s, double size, Brush b) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, b, dpi);

        var pts = Points;
        const double left = 46, bottom = 26, top = 18;
        var plotH = h - bottom - top;
        if (pts is null || pts.Count == 0 || pts.All(p => p.Value == 0))
        {
            for (var i = 0; i <= 4; i++)
            {
                var y = top + plotH * i / 4.0;
                dc.DrawLine(grid, new Point(left, y), new Point(w, y));
            }

            var empty = Txt(EmptyText, 12, text);
            dc.DrawText(empty, new Point(left + (w - left - empty.Width) / 2, top + plotH / 2 - 10));
            if (pts is not null) DrawLabels(dc, pts, left, w, h, bottom, Txt, text);
            return;
        }

        var max = Math.Max(1, pts.Max(p => p.Value));
        var step = NiceStep(max / 4);
        var axisMax = Math.Ceiling(max / step) * step;
        var ticks = (int)Math.Round(axisMax / step);
        for (var i = 0; i <= ticks; i++)
        {
            var v = step * i;
            var y = top + plotH - plotH * v / axisMax;
            dc.DrawLine(grid, new Point(left, y), new Point(w, y));
            var label = Txt(FormatAxis(v), 10.5, text);
            dc.DrawText(label, new Point(left - 8 - label.Width, y - label.Height / 2));
        }

        var slot = (w - left) / pts.Count;
        var barW = Math.Min(54, slot * 0.58);
        var normal = R("Brush.ChartBar", Color.FromRgb(0xB4, 0xE6, 0xE3));
        var strong = R("Brush.ChartBarStrong", Color.FromRgb(0x00, 0x8C, 0x95));
        for (var i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            var bh = Math.Max(p.Value > 0 ? 3 : 0, plotH * Math.Max(0, p.Value) / axisMax);
            var x = left + slot * i + (slot - barW) / 2;
            var rect = new Rect(x, top + plotH - bh, barW, bh);
            var brush = p.IsHighlight || i == _hover ? strong : normal;
            dc.DrawGeometry(brush, null, TopRounded(rect, Math.Min(6, barW / 3)));
            if (i == _hover)
            {
                var tip = Txt(p.Display, 11.5, Brushes.White);
                var tw = tip.Width + 16;
                var tx = Math.Clamp(x + barW / 2 - tw / 2, 0, w - tw);
                var ty = Math.Max(0, rect.Top - 30);
                dc.DrawRoundedRectangle(R("Brush.Tooltip", Colors.Black), null, new Rect(tx, ty, tw, 24), 6, 6);
                dc.DrawText(Txt(p.Display, 11.5, R("Brush.TooltipText", Colors.White)), new Point(tx + 8, ty + 4));
            }
        }

        DrawLabels(dc, pts, left, w, h, bottom, Txt, text);
    }

    private static void DrawLabels(DrawingContext dc, IReadOnlyList<ChartPoint> pts, double left, double w, double h, double bottom,
        Func<string, double, Brush, FormattedText> txt, Brush brush)
    {
        var slot = (w - left) / pts.Count;
        var every = Math.Max(1, (int)Math.Ceiling(40 / Math.Max(1, slot)));
        for (var i = 0; i < pts.Count; i += every)
        {
            var label = txt(pts[i].Label, 10.5, brush);
            dc.DrawText(label, new Point(left + slot * i + (slot - label.Width) / 2, h - bottom + 7));
        }
    }

    private static Geometry TopRounded(Rect r, double radius)
    {
        if (r.Height < radius * 2) radius = r.Height / 2;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(r.Left, r.Bottom), true, true);
            ctx.LineTo(new Point(r.Left, r.Top + radius), true, false);
            ctx.ArcTo(new Point(r.Left + radius, r.Top), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(r.Right - radius, r.Top), true, false);
            ctx.ArcTo(new Point(r.Right, r.Top + radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(r.Right, r.Bottom), true, false);
        }

        g.Freeze();
        return g;
    }

    private static double NiceStep(double raw)
    {
        if (raw <= 0) return 1;
        var exp = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var f = raw / exp;
        var nice = f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10;
        return nice * exp;
    }
}

/// <summary>Donut chart with a centre caption.</summary>
public sealed class DonutChart : FrameworkElement
{
    public static readonly DependencyProperty SlicesProperty = DependencyProperty.Register(nameof(Slices), typeof(IReadOnlyList<ChartSlice>), typeof(DonutChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CenterValueProperty = DependencyProperty.Register(nameof(CenterValue), typeof(string), typeof(DonutChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CenterLabelProperty = DependencyProperty.Register(nameof(CenterLabel), typeof(string), typeof(DonutChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(DonutChart),
        new FrameworkPropertyMetadata(24.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ChartSlice>? Slices
    {
        get => (IReadOnlyList<ChartSlice>?)GetValue(SlicesProperty);
        set => SetValue(SlicesProperty, value);
    }

    public string CenterValue
    {
        get => (string)GetValue(CenterValueProperty);
        set => SetValue(CenterValueProperty, value);
    }

    public string CenterLabel
    {
        get => (string)GetValue(CenterLabelProperty);
        set => SetValue(CenterLabelProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 40) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = size / 2 - Thickness / 2 - 2;
        var total = Slices?.Sum(s => s.Value) ?? 0;
        var track = new Pen(TryFindResource("Brush.GridLine") as Brush ?? Brushes.LightGray, Thickness);
        dc.DrawEllipse(null, track, center, radius, radius);
        if (total > 0 && Slices is not null)
        {
            var angle = -90.0;
            var gap = Slices.Count(s => s.Value > 0) > 1 ? 2.2 : 0;
            foreach (var s in Slices.Where(s => s.Value > 0))
            {
                var sweep = 360.0 * s.Value / total;
                var drawSweep = Math.Max(0.5, sweep - gap);
                if (drawSweep >= 359.9)
                {
                    dc.DrawEllipse(null, new Pen(s.Brush, Thickness), center, radius, radius);
                }
                else
                {
                    var g = Arc(center, radius, angle + gap / 2, drawSweep);
                    var pen = new Pen(s.Brush, Thickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
                    dc.DrawGeometry(null, pen, g);
                }

                angle += sweep;
            }
        }

        var typeface = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var labelFace = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var textBrush = TryFindResource("Brush.Text") as Brush ?? Brushes.Black;
        var muted = TryFindResource("Brush.TextSecondary") as Brush ?? Brushes.Gray;
        var value = new FormattedText(CenterValue ?? "", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, Math.Max(16, size * 0.16), textBrush, dpi);
        var label = new FormattedText(CenterLabel ?? "", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, labelFace, 11.5, muted, dpi)
        {
            MaxTextWidth = Math.Max(10, radius * 1.4),
            TextAlignment = TextAlignment.Center,
        };
        var totalH = value.Height + label.Height;
        dc.DrawText(value, new Point(center.X - value.Width / 2, center.Y - totalH / 2));
        dc.DrawText(label, new Point(center.X - label.MaxTextWidth / 2, center.Y - totalH / 2 + value.Height));
    }

    private static Geometry Arc(Point c, double r, double startDeg, double sweepDeg)
    {
        static Point P(Point c, double r, double deg) => new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(P(c, r, startDeg), false, false);
            ctx.ArcTo(P(c, r, startDeg + sweepDeg), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }

        g.Freeze();
        return g;
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace FujinTerm.Controls;

// A per-category high-low bar chart — the stock-chart view where each x slot (here,
// a loop step) shows a vertical bar spanning a value's min→max range on a FIXED
// vertical axis. Draws up to two range series grouped side by side per slot (e.g.
// an HP band and a mana band), each in its own colour, so a glance reads the range
// each loop step drove the vitals through. Values are clamped to [Min, Max].
//
// Deliberately tiny and self-contained: no axes, labels, gridlines, or interaction
// — the panel supplies the frame and the scale ticks. Fixed axis (not auto-scaled)
// so several charts, or repeated snapshots, stay comparable. A re-assigned series
// repaints via AffectsRender.
public sealed class RangeBarChart : Control
{
    // Primary series low / high (e.g. HP), one value per x slot. The two must match
    // in length; an empty or mismatched pair draws nothing.
    public static readonly StyledProperty<IReadOnlyList<double>?> LowProperty =
        AvaloniaProperty.Register<RangeBarChart, IReadOnlyList<double>?>(nameof(Low));

    public static readonly StyledProperty<IReadOnlyList<double>?> HighProperty =
        AvaloniaProperty.Register<RangeBarChart, IReadOnlyList<double>?>(nameof(High));

    // Optional second series low / high (e.g. mana), drawn as a second bar beside
    // the primary in each slot. Null / empty / length-mismatched hides it — so a
    // no-mana class simply shows the HP bars alone.
    public static readonly StyledProperty<IReadOnlyList<double>?> SecondaryLowProperty =
        AvaloniaProperty.Register<RangeBarChart, IReadOnlyList<double>?>(nameof(SecondaryLow));

    public static readonly StyledProperty<IReadOnlyList<double>?> SecondaryHighProperty =
        AvaloniaProperty.Register<RangeBarChart, IReadOnlyList<double>?>(nameof(SecondaryHigh));

    // Optional per-slot trend value for each series (the mean), drawn as a line
    // threaded through the bars — the trajectory, on top of the range. Null / wrong
    // length simply omits the line. Same length as the matching Low/High.
    public static readonly StyledProperty<IReadOnlyList<double>?> PrimaryTrendProperty =
        AvaloniaProperty.Register<RangeBarChart, IReadOnlyList<double>?>(nameof(PrimaryTrend));

    public static readonly StyledProperty<IReadOnlyList<double>?> SecondaryTrendProperty =
        AvaloniaProperty.Register<RangeBarChart, IReadOnlyList<double>?>(nameof(SecondaryTrend));

    public static readonly StyledProperty<IBrush> PrimaryBrushProperty =
        AvaloniaProperty.Register<RangeBarChart, IBrush>(
            nameof(PrimaryBrush), new SolidColorBrush(Color.Parse("#E06060")));

    public static readonly StyledProperty<IBrush> SecondaryBrushProperty =
        AvaloniaProperty.Register<RangeBarChart, IBrush>(
            nameof(SecondaryBrush), new SolidColorBrush(Color.Parse("#5FB3D9")));

    // Fixed vertical axis bounds. Defaults suit a percent scale.
    public static readonly StyledProperty<double> MinProperty =
        AvaloniaProperty.Register<RangeBarChart, double>(nameof(Min), 0);

    public static readonly StyledProperty<double> MaxProperty =
        AvaloniaProperty.Register<RangeBarChart, double>(nameof(Max), 100);

    // Windowing, so a long loop stays legible: only WindowSize slots are drawn at
    // once, filling the full width, and Offset selects which run of steps that is —
    // a slider drives Offset from step 1 to the tail. WindowSize <= 0 (or a loop
    // already shorter than the window) shows every step from Offset. Offset is a
    // double so it can bind straight to a Slider's Value; it's floored + clamped in
    // Render.
    public static readonly StyledProperty<double> OffsetProperty =
        AvaloniaProperty.Register<RangeBarChart, double>(nameof(Offset), 0);

    public static readonly StyledProperty<int> WindowSizeProperty =
        AvaloniaProperty.Register<RangeBarChart, int>(nameof(WindowSize), 15);

    // Scrub cursor: a vertical line marking the centre of the visible window (the
    // step CenterStep names). Shown while the user drags the pan slider so they can
    // see exactly which step's bar they're centred on.
    public static readonly StyledProperty<bool> ShowCursorProperty =
        AvaloniaProperty.Register<RangeBarChart, bool>(nameof(ShowCursor));

    public static readonly StyledProperty<IBrush> CursorBrushProperty =
        AvaloniaProperty.Register<RangeBarChart, IBrush>(
            nameof(CursorBrush), new SolidColorBrush(Color.Parse("#C0FFFFFF")));

    public IReadOnlyList<double>? Low
    {
        get => GetValue(LowProperty);
        set => SetValue(LowProperty, value);
    }

    public IReadOnlyList<double>? High
    {
        get => GetValue(HighProperty);
        set => SetValue(HighProperty, value);
    }

    public IReadOnlyList<double>? SecondaryLow
    {
        get => GetValue(SecondaryLowProperty);
        set => SetValue(SecondaryLowProperty, value);
    }

    public IReadOnlyList<double>? SecondaryHigh
    {
        get => GetValue(SecondaryHighProperty);
        set => SetValue(SecondaryHighProperty, value);
    }

    public IReadOnlyList<double>? PrimaryTrend
    {
        get => GetValue(PrimaryTrendProperty);
        set => SetValue(PrimaryTrendProperty, value);
    }

    public IReadOnlyList<double>? SecondaryTrend
    {
        get => GetValue(SecondaryTrendProperty);
        set => SetValue(SecondaryTrendProperty, value);
    }

    public IBrush PrimaryBrush
    {
        get => GetValue(PrimaryBrushProperty);
        set => SetValue(PrimaryBrushProperty, value);
    }

    public IBrush SecondaryBrush
    {
        get => GetValue(SecondaryBrushProperty);
        set => SetValue(SecondaryBrushProperty, value);
    }

    public double Min
    {
        get => GetValue(MinProperty);
        set => SetValue(MinProperty, value);
    }

    public double Max
    {
        get => GetValue(MaxProperty);
        set => SetValue(MaxProperty, value);
    }

    public double Offset
    {
        get => GetValue(OffsetProperty);
        set => SetValue(OffsetProperty, value);
    }

    public int WindowSize
    {
        get => GetValue(WindowSizeProperty);
        set => SetValue(WindowSizeProperty, value);
    }

    public bool ShowCursor
    {
        get => GetValue(ShowCursorProperty);
        set => SetValue(ShowCursorProperty, value);
    }

    public IBrush CursorBrush
    {
        get => GetValue(CursorBrushProperty);
        set => SetValue(CursorBrushProperty, value);
    }

    static RangeBarChart()
    {
        AffectsRender<RangeBarChart>(
            LowProperty, HighProperty, SecondaryLowProperty, SecondaryHighProperty,
            PrimaryTrendProperty, SecondaryTrendProperty,
            PrimaryBrushProperty, SecondaryBrushProperty, MinProperty, MaxProperty,
            OffsetProperty, WindowSizeProperty, ShowCursorProperty, CursorBrushProperty);
    }

    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        IReadOnlyList<double>? low = Low, high = High;
        if (low is null || high is null || low.Count == 0 || low.Count != high.Count
            || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        int n = low.Count;
        IReadOnlyList<double>? secLow = SecondaryLow, secHigh = SecondaryHigh;
        bool hasSecondary = secLow is not null && secHigh is not null
            && secLow.Count == n && secHigh.Count == n;

        double range = Max - Min;
        if (range <= 0) return;

        // Visible window: only WindowSize steps at once, starting at Offset, so a
        // long loop reads a step at a time instead of a smear. The window fills the
        // full width, so bars stay wide regardless of loop length.
        int window = WindowSize > 0 ? Math.Min(WindowSize, n) : n;
        int start = Math.Clamp((int)Math.Round(Offset), 0, n - window);
        int visible = Math.Min(window, n - start);
        if (visible <= 0) return;

        // Inset top/bottom so a bar at the extreme doesn't clip the frame.
        const double inset = 1.0;
        double plotH = Math.Max(bounds.Height - 2 * inset, 0);
        double slotW = bounds.Width / visible;

        double Y(double v)
        {
            double c = v < Min ? Min : v > Max ? Max : v;
            double norm = (c - Min) / range;
            return inset + (1 - norm) * plotH;
        }

        // Bar width per slot: one centred bar, or two grouped bars with a hairline
        // gap. A minimum 1px keeps it legible if the window is ever widened.
        double gap = slotW * 0.08;
        double barW = hasSecondary
            ? Math.Max((slotW * 0.72 - gap) / 2, 1.0)
            : Math.Max(slotW * 0.5, 1.0);

        // Each metric's bar centre x within a slot (grouped left / right, or dead
        // centre when solo). The trend line rides these same centres.
        double PrimaryCenter(int k) => hasSecondary
            ? (k + 0.5) * slotW - gap / 2 - barW / 2
            : (k + 0.5) * slotW;
        double SecondaryCenter(int k) => (k + 0.5) * slotW + gap / 2 + barW / 2;

        // Bars carry the range as a translucent column so the solid trend line on
        // top stays legible against them.
        IBrush primaryFill = Translucent(PrimaryBrush, 140);
        IBrush secondaryFill = Translucent(SecondaryBrush, 140);
        for (int k = 0; k < visible; k++)
        {
            int i = start + k;
            DrawBar(context, PrimaryCenter(k) - barW / 2, barW, high[i], low[i], primaryFill, Y);
            if (hasSecondary)
                DrawBar(context, SecondaryCenter(k) - barW / 2, barW, secHigh![i], secLow![i], secondaryFill, Y);
        }

        // Trend lines (per-step mean) threaded through the bar centres, on top.
        DrawTrend(context, PrimaryTrend, n, start, visible, PrimaryCenter, Y, PrimaryBrush);
        if (hasSecondary)
            DrawTrend(context, SecondaryTrend, n, start, visible, SecondaryCenter, Y, SecondaryBrush);

        // Scrub cursor: a dashed vertical line down the centre of the window (the
        // step the readout names), drawn on top while the slider is held.
        if (ShowCursor)
        {
            double cx = (visible / 2 + 0.5) * slotW;
            context.DrawLine(
                new Pen(CursorBrush, 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) },
                new Point(cx, 0), new Point(cx, bounds.Height));
        }
    }

    // One floating bar from high→low. A flat step (high == low) still shows a 1px
    // tick so the step isn't invisible.
    private static void DrawBar(DrawingContext context, double x, double w,
        double high, double low, IBrush brush, Func<double, double> y)
    {
        double top = y(high);
        double bottom = y(low);
        double h = Math.Max(bottom - top, 1.0);
        context.FillRectangle(brush, new Rect(x, top, w, h));
    }

    // Polyline through each visible step's trend value at its bar centre. Skipped
    // when the series is absent or length-mismatched.
    private static void DrawTrend(DrawingContext context, IReadOnlyList<double>? trend,
        int n, int start, int visible, Func<int, double> centerX, Func<double, double> y, IBrush brush)
    {
        if (trend is null || trend.Count != n) return;
        StreamGeometry geo = new();
        using (StreamGeometryContext g = geo.Open())
        {
            g.BeginFigure(new Point(centerX(0), y(trend[start])), isFilled: false);
            for (int k = 1; k < visible; k++) g.LineTo(new Point(centerX(k), y(trend[start + k])));
            g.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(brush, 1.6)
        {
            LineJoin = PenLineJoin.Round,
            LineCap = PenLineCap.Round,
        }, geo);
    }

    // A translucent copy of a solid-colour brush; non-solid brushes pass through.
    private static IBrush Translucent(IBrush brush, byte alpha)
    {
        if (brush is ISolidColorBrush s)
        {
            Color c = s.Color;
            return new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        }
        return brush;
    }
}

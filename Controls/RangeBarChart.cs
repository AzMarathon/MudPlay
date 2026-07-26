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

    static RangeBarChart()
    {
        AffectsRender<RangeBarChart>(
            LowProperty, HighProperty, SecondaryLowProperty, SecondaryHighProperty,
            PrimaryBrushProperty, SecondaryBrushProperty, MinProperty, MaxProperty);
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

        // Inset top/bottom so a bar at the extreme doesn't clip the frame.
        const double inset = 1.0;
        double plotH = Math.Max(bounds.Height - 2 * inset, 0);
        double slotW = bounds.Width / n;

        double Y(double v)
        {
            double c = v < Min ? Min : v > Max ? Max : v;
            double norm = (c - Min) / range;
            return inset + (1 - norm) * plotH;
        }

        // Bar width per slot: one centred bar, or two grouped bars with a hairline
        // gap. A minimum 1px keeps a dense circuit (many steps) legible.
        double gap = slotW * 0.08;
        double barW = hasSecondary
            ? Math.Max((slotW * 0.72 - gap) / 2, 1.0)
            : Math.Max(slotW * 0.5, 1.0);

        for (int i = 0; i < n; i++)
        {
            double center = (i + 0.5) * slotW;
            if (hasSecondary)
            {
                DrawBar(context, center - gap / 2 - barW, barW, high[i], low[i], PrimaryBrush, Y);
                DrawBar(context, center + gap / 2, barW, secHigh![i], secLow![i], SecondaryBrush, Y);
            }
            else
            {
                DrawBar(context, center - barW / 2, barW, high[i], low[i], PrimaryBrush, Y);
            }
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
}

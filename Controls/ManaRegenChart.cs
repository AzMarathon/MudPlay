using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MudPlay.ViewModels.CharacterWorkshop;

namespace MudPlay.Controls;

// Hand-drawn line chart for the Mana Regen calculator: X = character level, Y =
// mana tick (MP / 30 s), one polyline per stat-value series so the integer-tick
// step-ups read as the breakpoints. No third-party charting — rendered directly
// like the terminal / map controls. The calculator VM supplies a whole
// ManaRegenChartData on each recompute; AffectsRender redraws.
public sealed class ManaRegenChart : Control
{
    public static readonly StyledProperty<ManaRegenChartData?> DataProperty =
        AvaloniaProperty.Register<ManaRegenChart, ManaRegenChartData?>(nameof(Data));

    static ManaRegenChart() => AffectsRender<ManaRegenChart>(DataProperty);

    public ManaRegenChartData? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    // Semi-transparent greys so the chart reads on either theme's panel fill.
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromArgb(0xD0, 0xC8, 0xC8, 0xC8));
    private static readonly IPen AxisPen = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0x90, 0x90, 0x90)), 1.0);
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)), 1.0);
    private static readonly IPen CurrentMarkerPen = new Pen(new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xD2, 0x4D)), 2.0);
    private static readonly IBrush CurrentMarkerFill = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xE0, 0x3A));
    private static readonly Typeface Tf = new("Inter");

    public override void Render(DrawingContext ctx)
    {
        if (Data is not { Series.Count: > 0 } d) return;
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 60 || h < 60) return;

        int yMin = int.MaxValue, yMax = int.MinValue;
        foreach (ManaRegenChartSeries s in d.Series)
            foreach (int t in s.Ticks) { yMin = Math.Min(yMin, t); yMax = Math.Max(yMax, t); }
        if (yMin == int.MaxValue) return;
        if (yMax == yMin) yMax = yMin + 1;

        const double padL = 44, padR = 14, padT = 28, padB = 30;
        Rect plot = new(padL, padT, Math.Max(1, w - padL - padR), Math.Max(1, h - padT - padB));
        int span = d.MaxLevel - d.MinLevel;

        double XFor(int level) => span <= 0
            ? plot.X + plot.Width / 2
            : plot.X + (double)(level - d.MinLevel) / span * plot.Width;
        double YFor(int tick) => plot.Bottom - (double)(tick - yMin) / (yMax - yMin) * plot.Height;

        // Y gridlines + MP labels (thinned when the span is tall).
        int yStep = Math.Max(1, (int)Math.Ceiling((yMax - yMin) / 8.0));
        for (int t = yMin; t <= yMax; t += yStep)
        {
            double y = YFor(t);
            ctx.DrawLine(GridPen, new Point(plot.X, y), new Point(plot.Right, y));
            FormattedText ft = Label(t.ToString());
            ctx.DrawText(ft, new Point(plot.X - ft.Width - 6, y - ft.Height / 2));
        }

        // X gridlines + level labels.
        for (int lv = d.MinLevel; lv <= d.MaxLevel; lv++)
        {
            double x = XFor(lv);
            ctx.DrawLine(GridPen, new Point(x, plot.Y), new Point(x, plot.Bottom));
            FormattedText ft = Label(lv.ToString());
            ctx.DrawText(ft, new Point(x - ft.Width / 2, plot.Bottom + 4));
        }

        ctx.DrawLine(AxisPen, new Point(plot.X, plot.Y), new Point(plot.X, plot.Bottom));
        ctx.DrawLine(AxisPen, new Point(plot.X, plot.Bottom), new Point(plot.Right, plot.Bottom));

        // Axis captions.
        FormattedText xcap = Label("level");
        ctx.DrawText(xcap, new Point(plot.X + (plot.Width - xcap.Width) / 2, plot.Bottom + 15));
        FormattedText ycap = Label("MP/30s");
        ctx.DrawText(ycap, new Point(plot.X - padL + 2, plot.Y - 16));

        // Series polylines + point dots + a compact legend across the top.
        double legendX = plot.X + ycap.Width + 12;
        foreach (ManaRegenChartSeries s in d.Series)
        {
            IBrush brush = new SolidColorBrush(Color.FromUInt32(s.ColorArgb));
            IPen pen = new Pen(brush, 2.0);
            Point? prev = null;
            for (int i = 0; i < s.Ticks.Count; i++)
            {
                Point p = new(XFor(d.MinLevel + i), YFor(s.Ticks[i]));
                if (prev is { } pr) ctx.DrawLine(pen, pr, p);
                ctx.DrawEllipse(brush, null, p, 2.5, 2.5);
                prev = p;
            }
            FormattedText ft = Label(s.Label);
            ctx.FillRectangle(brush, new Rect(legendX, plot.Y - 15, 10, 3));
            ctx.DrawText(ft, new Point(legendX + 14, plot.Y - 21));
            legendX += 14 + ft.Width + 14;
        }

        // "You are here": a prominent ring at the current level / tick, only when
        // it falls inside the plotted level + tick window.
        if (d.CurrentLevel >= d.MinLevel && d.CurrentLevel <= d.MaxLevel
            && d.CurrentTick >= yMin && d.CurrentTick <= yMax)
        {
            Point here = new(XFor(d.CurrentLevel), YFor(d.CurrentTick));
            ctx.DrawEllipse(null, CurrentMarkerPen, here, 5, 5);
            ctx.DrawEllipse(CurrentMarkerFill, null, here, 2, 2);
        }
    }

    private static FormattedText Label(string text)
        => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Tf, 11, TextBrush);
}

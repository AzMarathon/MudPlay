using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using FujinTerm.ViewModels.CharacterWorkshop;

namespace FujinTerm.Controls;

// Compact step chart for the Mana Regen calculator's roll breakpoints: X = the
// spell's roll value across its level-scaled range, Y = the mana tick (MP) that
// roll yields. The tick holds the worst value from the low end, then steps up at
// each breakpoint — so every step IS a breakpoint, and the recommended reroll
// threshold is the roll at which the top step is first reached (marked in amber).
// Replaces a variable-length table with one fixed-height graph. Hand-drawn like
// ManaRegenChart; the VM supplies a ManaBreakpointStripData on each recompute.
public sealed class ManaBreakpointStrip : Control
{
    public static readonly StyledProperty<ManaBreakpointStripData?> DataProperty =
        AvaloniaProperty.Register<ManaBreakpointStrip, ManaBreakpointStripData?>(nameof(Data));

    static ManaBreakpointStrip() => AffectsRender<ManaBreakpointStrip>(DataProperty);

    public ManaBreakpointStripData? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromArgb(0xD0, 0xC8, 0xC8, 0xC8));
    private static readonly IPen AxisPen = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0x90, 0x90, 0x90)), 1.0);
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)), 1.0);
    private static readonly IPen StepPen = new Pen(new SolidColorBrush(Color.FromArgb(0xFF, 0x43, 0x63, 0xD8)), 2.0)
    { LineJoin = PenLineJoin.Round };
    private static readonly IBrush AmberBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xD2, 0x4D));
    private static readonly IPen RecommendedPen = new Pen(new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xD2, 0x4D)), 2.0);
    private static readonly Typeface Tf = new("Inter");

    public override void Render(DrawingContext ctx)
    {
        if (Data is not { } d) return;
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 60 || h < 40) return;

        int rollMin = d.RollMin, rollMax = Math.Max(d.RollMin + 1, d.RollMax);
        int yMin = d.WorstTick, yMax = Math.Max(d.WorstTick + 1, d.BestTick);

        const double padL = 40, padR = 14, padT = 12, padB = 22;
        Rect plot = new(padL, padT, Math.Max(1, w - padL - padR), Math.Max(1, h - padT - padB));

        double XForRoll(int r) => plot.X + (double)(Math.Clamp(r, rollMin, rollMax) - rollMin) / (rollMax - rollMin) * plot.Width;
        double YForTick(int t) => plot.Bottom - (double)(t - yMin) / (yMax - yMin) * plot.Height;

        // Y gridlines + MP labels — only at the actual step levels (worst tick + each
        // breakpoint's tick), not every integer MP. The tick range can span dozens of
        // values while only a handful are real steps, so labeling all of them clutters
        // the axis into noise.
        var levels = new SortedSet<int> { yMin };
        foreach (ManaBreakpointMark m in d.Marks) levels.Add(m.Tick);
        foreach (int t in levels)
        {
            double y = YForTick(t);
            ctx.DrawLine(GridPen, new Point(plot.X, y), new Point(plot.Right, y));
            FormattedText ft = Label(t.ToString(CultureInfo.InvariantCulture));
            ctx.DrawText(ft, new Point(plot.X - ft.Width - 6, y - ft.Height / 2));
        }

        ctx.DrawLine(AxisPen, new Point(plot.X, plot.Y), new Point(plot.X, plot.Bottom));
        ctx.DrawLine(AxisPen, new Point(plot.X, plot.Bottom), new Point(plot.Right, plot.Bottom));

        // Step line: hold the worst tick from the low roll, step up at each mark.
        var pts = new List<Point> { new(XForRoll(rollMin), YForTick(yMin)) };
        int curTick = yMin;
        foreach (ManaBreakpointMark m in d.Marks.OrderBy(m => m.Roll))
        {
            pts.Add(new Point(XForRoll(m.Roll), YForTick(curTick)));   // run to the step
            pts.Add(new Point(XForRoll(m.Roll), YForTick(m.Tick)));    // step up
            curTick = m.Tick;
        }
        pts.Add(new Point(XForRoll(rollMax), YForTick(curTick)));
        for (int i = 1; i < pts.Count; i++) ctx.DrawLine(StepPen, pts[i - 1], pts[i]);

        // Recommended reroll threshold — a vertical amber marker + "≥N" label.
        if (d.RecommendedRoll is int rec)
        {
            double x = XForRoll(rec);
            ctx.DrawLine(RecommendedPen, new Point(x, plot.Y), new Point(x, plot.Bottom));
            FormattedText ft = Label($"≥{rec}", AmberBrush);
            double lx = Math.Min(x + 4, plot.Right - ft.Width);
            ctx.DrawText(ft, new Point(Math.Max(plot.X, lx), plot.Y - 1));
        }

        // X end labels (the roll range) + caption.
        FormattedText lo = Label(rollMin.ToString(CultureInfo.InvariantCulture));
        ctx.DrawText(lo, new Point(plot.X, plot.Bottom + 4));
        FormattedText hi = Label(rollMax.ToString(CultureInfo.InvariantCulture));
        ctx.DrawText(hi, new Point(plot.Right - hi.Width, plot.Bottom + 4));
        FormattedText cap = Label("roll");
        ctx.DrawText(cap, new Point(plot.X + (plot.Width - cap.Width) / 2, plot.Bottom + 4));
    }

    private static FormattedText Label(string text, IBrush? brush = null)
        => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Tf, 11, brush ?? TextBrush);
}

using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace FujinTerm.Controls;

// A minimal hand-drawn line chart for the Session Stats panels. Takes a numeric
// series via Samples (oldest → newest, left → right) and draws it as a single
// polyline auto-scaled to the control's bounds, with an optional translucent area
// fill underneath. Set HighSamples too for a low→high BAND (a min/max range per x
// position, e.g. HP per loop step), and RangeMin/RangeMax to pin the vertical axis
// so several overlaid sparklines share one scale.
//
// Deliberately tiny and self-contained: no axes, labels, gridlines, or
// interaction — a sparkline, not a charting library. The series is normalised
// to its own min/max each render so it always fills the vertical space; a flat
// series pins to the centre line rather than dividing by zero. The data shape
// (a plain IReadOnlyList<double>) is generic on purpose — the window's VM owns
// "what's a kill rate"; this control only knows how to draw numbers. If the
// bound series implements INotifyCollectionChanged the control re-renders when
// it mutates, so a live ObservableCollection<double> animates as samples
// append; a re-assigned list is picked up via AffectsRender.
public sealed class SparklineControl : Control
{
    // The series to plot, oldest first. Fewer than two points draws nothing.
    public static readonly StyledProperty<IReadOnlyList<double>?> SamplesProperty =
        AvaloniaProperty.Register<SparklineControl, IReadOnlyList<double>?>(nameof(Samples));

    // Colour of the plotted line.
    public static readonly StyledProperty<IBrush> StrokeProperty =
        AvaloniaProperty.Register<SparklineControl, IBrush>(
            nameof(Stroke), new SolidColorBrush(Color.Parse("#7AB870")));

    // Line thickness, in device-independent pixels.
    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<SparklineControl, double>(nameof(StrokeThickness), 1.5);

    // Optional brush for the fill. In single-series mode it fills the area below
    // the line; when HighSamples is set (band mode) it fills between the low and
    // high series. null (default) leaves the area empty.
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<SparklineControl, IBrush?>(nameof(Fill));

    // Optional upper series, same length + order as Samples. When set the control
    // draws a low→high BAND (Samples = low edge, HighSamples = high edge) filled
    // with Fill and outlined top and bottom — for a min/max range per x position.
    // Null (default) keeps the plain single-line sparkline.
    public static readonly StyledProperty<IReadOnlyList<double>?> HighSamplesProperty =
        AvaloniaProperty.Register<SparklineControl, IReadOnlyList<double>?>(nameof(HighSamples));

    // Optional fixed vertical range. When both are set (and Max > Min) the series
    // normalise against [Min, Max] instead of their own extent — so several
    // overlaid sparklines (e.g. an HP band and a mana band on one 0–100% axis)
    // share a common scale and line up. Null (default) auto-scales to the data.
    public static readonly StyledProperty<double?> RangeMinProperty =
        AvaloniaProperty.Register<SparklineControl, double?>(nameof(RangeMin));

    public static readonly StyledProperty<double?> RangeMaxProperty =
        AvaloniaProperty.Register<SparklineControl, double?>(nameof(RangeMax));

    public IReadOnlyList<double>? Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public IBrush Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public IReadOnlyList<double>? HighSamples
    {
        get => GetValue(HighSamplesProperty);
        set => SetValue(HighSamplesProperty, value);
    }

    public double? RangeMin
    {
        get => GetValue(RangeMinProperty);
        set => SetValue(RangeMinProperty, value);
    }

    public double? RangeMax
    {
        get => GetValue(RangeMaxProperty);
        set => SetValue(RangeMaxProperty, value);
    }

    // The currently-subscribed series, so a live collection's mutations repaint.
    private INotifyCollectionChanged? _observed;

    static SparklineControl()
    {
        AffectsRender<SparklineControl>(StrokeProperty, StrokeThicknessProperty, FillProperty,
            HighSamplesProperty, RangeMinProperty, RangeMaxProperty);
        SamplesProperty.Changed.AddClassHandler<SparklineControl>((c, e) =>
            c.OnSamplesChanged(e.NewValue as IReadOnlyList<double>));
    }

    private void OnSamplesChanged(IReadOnlyList<double>? newSamples)
    {
        if (_observed is not null)
        {
            _observed.CollectionChanged -= OnCollectionChanged;
            _observed = null;
        }
        if (newSamples is INotifyCollectionChanged incc)
        {
            incc.CollectionChanged += OnCollectionChanged;
            _observed = incc;
        }
        InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        IReadOnlyList<double>? low = Samples;
        if (low is null || low.Count < 2 || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        IReadOnlyList<double>? high = HighSamples;
        bool band = high is not null && high.Count == low.Count;

        // Inset top/bottom by the stroke radius so the line at the extremes
        // isn't clipped against the edge.
        double inset = Math.Max(StrokeThickness, 1.0);
        double plotH = Math.Max(bounds.Height - 2 * inset, 0);
        double plotW = bounds.Width;

        // Normalise against an explicit fixed range when set (so overlaid
        // sparklines share one axis), else the data's own extent — spanning both
        // edges in band mode so the whole band fits.
        double min, max;
        if (RangeMin is { } rmin && RangeMax is { } rmax && rmax > rmin)
        {
            min = rmin;
            max = rmax;
        }
        else
        {
            min = low[0];
            max = low[0];
            Extend(low, ref min, ref max);
            if (band) Extend(high!, ref min, ref max);
        }
        double range = max - min;

        // A flat range has no spread to normalise against, so points sit on the
        // centre line instead of dividing by zero.
        Point Project(IReadOnlyList<double> s, int i)
        {
            double x = plotW * i / (s.Count - 1);
            double norm = range > 0 ? (s[i] - min) / range : 0.5;
            return new Point(x, inset + (1 - norm) * plotH);
        }

        void DrawLine(IReadOnlyList<double> s)
        {
            StreamGeometry line = new();
            using (StreamGeometryContext g = line.Open())
            {
                g.BeginFigure(Project(s, 0), isFilled: false);
                for (int i = 1; i < s.Count; i++) g.LineTo(Project(s, i));
                g.EndFigure(false);
            }
            context.DrawGeometry(null, new Pen(Stroke, StrokeThickness)
            {
                LineJoin = PenLineJoin.Round,
                LineCap = PenLineCap.Round,
            }, line);
        }

        if (band)
        {
            // Filled band bounded by the high series across, then the low series
            // back, and both edges outlined.
            if (Fill is { } bandFill)
            {
                StreamGeometry area = new();
                using (StreamGeometryContext g = area.Open())
                {
                    g.BeginFigure(Project(high!, 0), isFilled: true);
                    for (int i = 1; i < high!.Count; i++) g.LineTo(Project(high, i));
                    for (int i = low.Count - 1; i >= 0; i--) g.LineTo(Project(low, i));
                    g.EndFigure(true);
                }
                context.DrawGeometry(bandFill, null, area);
            }
            DrawLine(low);
            DrawLine(high!);
            return;
        }

        // Single-series mode: optional area fill down to the baseline.
        if (Fill is { } fill)
        {
            StreamGeometry area = new();
            using (StreamGeometryContext g = area.Open())
            {
                g.BeginFigure(new Point(0, bounds.Height), isFilled: true);
                for (int i = 0; i < low.Count; i++) g.LineTo(Project(low, i));
                g.LineTo(new Point(plotW, bounds.Height));
                g.EndFigure(true);
            }
            context.DrawGeometry(fill, null, area);
        }
        DrawLine(low);
    }

    private static void Extend(IReadOnlyList<double> s, ref double min, ref double max)
    {
        foreach (double v in s)
        {
            if (v < min) min = v;
            if (v > max) max = v;
        }
    }
}

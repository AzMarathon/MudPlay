using Avalonia;
using Avalonia.Controls;

namespace MudPlay.Controls;

// A toolbar row of two children: the first (a WrapPanel of toggles) takes the width it
// needs, the second (a search box and its menu) fills the rest of the row. The first is
// measured against what's left once the second has its MinWidth, so when the row runs
// short the toggles wrap onto more lines while the second keeps its minimum and its place
// at the row's end, never squeezed off the window. A Grid can't do this: an Auto column
// measures its child with unlimited width, so a WrapPanel in one never wraps.
public sealed class WrapThenFillPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<WrapThenFillPanel, double>(nameof(Spacing));

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    static WrapThenFillPanel() => AffectsMeasure<WrapThenFillPanel>(SpacingProperty);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count != 2) return base.MeasureOverride(availableSize);
        Control lead = Children[0], fill = Children[1];

        lead.Measure(availableSize.WithWidth(LeadRoom(availableSize.Width, fill)));
        double fillRoom = Math.Max(FillMin(fill), availableSize.Width - lead.DesiredSize.Width - Spacing);
        fill.Measure(availableSize.WithWidth(fillRoom));

        double width = lead.DesiredSize.Width + Spacing + fill.DesiredSize.Width;
        if (!double.IsInfinity(availableSize.Width)) width = Math.Min(width, availableSize.Width);
        return new Size(width, Math.Max(lead.DesiredSize.Height, fill.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count != 2) return base.ArrangeOverride(finalSize);
        Control lead = Children[0], fill = Children[1];

        double leadWidth = Math.Min(lead.DesiredSize.Width, LeadRoom(finalSize.Width, fill));
        double fillX = leadWidth + Spacing;
        double fillWidth = Math.Max(FillMin(fill), finalSize.Width - fillX);
        // Both centred on the row's height: a single line of toggles sits level with the
        // taller search box, and a wrapped block keeps the search box beside its middle.
        lead.Arrange(new Rect(0, (finalSize.Height - lead.DesiredSize.Height) / 2,
                              leadWidth, lead.DesiredSize.Height));
        fill.Arrange(new Rect(fillX, (finalSize.Height - fill.DesiredSize.Height) / 2,
                              fillWidth, fill.DesiredSize.Height));
        return finalSize;
    }

    private double LeadRoom(double width, Control fill) => Math.Max(0, width - FillMin(fill) - Spacing);

    private static double FillMin(Control fill) => fill.MinWidth + fill.Margin.Left + fill.Margin.Right;
}

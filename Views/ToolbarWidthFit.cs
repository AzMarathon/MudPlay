using System;

namespace MudPlay.Views;

// The main window's width as its horizontal toolbars ask for it. A shortcut added
// to a toolbar widens the window by what the strip outgrew, so the new button shows
// instead of being clipped past the right edge. Removing shortcuts gives that width
// back, but only what the client itself added: never below the width the window
// had before a toolbar grew it, never so narrow a toolbar is clipped again, and not
// at all once the user has resized the window since, because that size is theirs.
public sealed class ToolbarWidthFit
{
    // Layout rounds widths through the screen's scaling, so a width read back after
    // being set can differ from it by a fraction of a pixel.
    private const double Tolerance = 1.0;

    // Width this fit added to the window and hasn't given back yet.
    private double _added;
    // The width it last gave the window; NaN until it has given one.
    private double _given = double.NaN;

    // The width to give the window, or null to leave it as it is. width is the
    // window's width now. needed is what the toolbars need: for a grow, the strip
    // that grew; for a shrink, the widest strip still showing. minWidth and maxWidth
    // bound the result (the window's minimum, the screen's working width).
    public double? Next(double width, double needed, bool grew, double minWidth, double maxWidth)
    {
        if (!double.IsNaN(_given) && Math.Abs(width - _given) > Tolerance)
        {
            _added = 0;
            _given = double.NaN;
        }

        double target;
        if (grew)
        {
            target = Math.Min(maxWidth, needed);
            if (target <= width) return null;
            _added += target - width;
        }
        else
        {
            if (_added <= 0) return null;
            target = Math.Max(Math.Max(width - _added, needed), minWidth);
            if (target >= width) return null;
            _added -= width - target;
        }
        _given = target;
        return target;
    }
}

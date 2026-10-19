using System;
using Avalonia.Controls;

namespace MudPlay.Views;

// The main window's width as its horizontal toolbars ask for it (user, 2026-10-19).
// A shortcut added to a toolbar widens the window by what the strip outgrew, so the
// new button shows instead of being clipped past the right edge. A toolbar that
// gets narrower narrows the window to what the widest toolbar now needs — but never
// so far that the terminal's grid is cut off, nor below the window's minimum. Only
// the user's own toolbar edit does either: a profile loaded, closed or opened at
// startup brings its own toolbar without touching the window's saved size. A
// maximized or full-screen window is the screen's size and is left alone.
public static class ToolbarWidthFit
{
    // The width to give the window, or null to leave it as it is. userEdit says the
    // toolbar changed because the user edited its buttons. width is the window's
    // width now. needed is what the toolbars need: for a grow, the strip that grew;
    // for a shrink, the widest strip still showing. terminalNeeds is the window
    // width that shows the terminal's whole grid. maxWidth is the screen's working
    // width.
    public static double? Next(bool userEdit, WindowState state, double width, double needed, bool grew,
        double terminalNeeds, double minWidth, double maxWidth)
    {
        if (!userEdit || state != WindowState.Normal) return null;
        if (grew)
        {
            double wider = Math.Min(maxWidth, needed);
            return wider > width ? wider : null;
        }
        double narrower = Math.Max(Math.Max(needed, terminalNeeds), minWidth);
        return narrower < width ? narrower : null;
    }
}

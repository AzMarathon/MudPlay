using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Threading;

namespace MudPlay.Controls;

// Attached behavior: when a TreeViewItem expands, scroll its HEADER to the top of
// the enclosing scroll viewport so the folder stays put and the children it just
// revealed flow into view below it. Opt in via TreeViewItemExpandScroll.Enable="True"
// on a TreeViewItem style — used by the Navigation rail's folder trees and the
// Navigation Management dialog's trees (loops / auto-lairs / goto favourites).
//
// Two things fight us here and shaped this implementation:
//  1. Alignment. Plain item.BringIntoView() reveals the item's FULL rectangle —
//     header plus the tall subtree it just expanded — and for a subtree taller than
//     the viewport that bottom-aligns, shoving the header off the top. We instead
//     bring a rectangle exactly one viewport tall, anchored at the item's top, into
//     view: a viewport-tall rect can only be shown by putting its top at the viewport
//     top, so the header top-aligns. This propagates through both the tree's own
//     MaxHeight scroll and the outer rail scroll.
//  2. Timing. These trees use a VirtualizingStackPanel, which RE-MEASURES on expand
//     and resets the scroll offset toward the top on that pass — which lands AFTER a
//     Loaded-priority scroll, undoing it (the folder shot back to the top). So run at
//     Background priority (after that re-measure) and re-assert once more on the next
//     frame, both before the user could scroll themselves.
public static class TreeViewItemExpandScroll
{
    public static readonly AttachedProperty<bool> EnableProperty =
        AvaloniaProperty.RegisterAttached<TreeViewItem, bool>(
            "Enable", typeof(TreeViewItemExpandScroll));

    public static bool GetEnable(TreeViewItem item) => item.GetValue(EnableProperty);
    public static void SetEnable(TreeViewItem item, bool value) => item.SetValue(EnableProperty, value);

    static TreeViewItemExpandScroll()
    {
        // One class handler for every TreeViewItem; the Enable flag gates which ones
        // actually scroll, and leaves never raise an expand so the check is cheap.
        TreeViewItem.IsExpandedProperty.Changed.AddClassHandler<TreeViewItem>((item, e) =>
        {
            if (e.GetNewValue<bool>() && GetEnable(item))
                Dispatcher.UIThread.Post(() => ScrollHeaderToTop(item), DispatcherPriority.Background);
        });
    }

    private static void ScrollHeaderToTop(TreeViewItem item)
    {
        BringHeaderToTop(item);
        // The virtualized tree can reset the offset toward the top on a follow-up
        // measure that lands after this first attempt; re-assert on the next frame.
        Dispatcher.UIThread.Post(() => BringHeaderToTop(item), DispatcherPriority.Background);
    }

    private static void BringHeaderToTop(TreeViewItem item)
    {
        // Target the nearest ancestor that can actually scroll vertically — the tree's
        // own MaxHeight viewport when it's overflowing, otherwise the outer rail scroll.
        if (FindScrollableAncestor(item) is not { Viewport.Height: > 0 } scroll) return;
        item.BringIntoView(new Rect(0, 0, 1, scroll.Viewport.Height));
    }

    private static ScrollViewer? FindScrollableAncestor(Visual from)
    {
        for (Visual? v = from.GetVisualParent(); v is not null; v = v.GetVisualParent())
            if (v is ScrollViewer sv && sv.Extent.Height - sv.Viewport.Height > 0.5)
                return sv;
        return null;
    }
}

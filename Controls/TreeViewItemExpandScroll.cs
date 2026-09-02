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
// Why not item.BringIntoView(): that brings the WHOLE item rectangle — header plus
// its now-expanded subtree — into view with a minimal scroll. For a folder whose
// expanded contents are taller than the viewport, the minimal scroll bottom-aligns
// the item, pushing the header clean off the top; for one already near the top it
// snaps the offset. Both read as the list "jumping away" the moment you open a
// folder. Aligning the header to the viewport top instead keeps the folder you
// clicked exactly where you expect it, with its loops listed beneath.
public static class TreeViewItemExpandScroll
{
    public static readonly AttachedProperty<bool> EnableProperty =
        AvaloniaProperty.RegisterAttached<TreeViewItem, bool>(
            "Enable", typeof(TreeViewItemExpandScroll));

    public static bool GetEnable(TreeViewItem item) => item.GetValue(EnableProperty);
    public static void SetEnable(TreeViewItem item, bool value) => item.SetValue(EnableProperty, value);

    static TreeViewItemExpandScroll()
    {
        // One class handler for every TreeViewItem; the Enable flag gates which
        // ones actually scroll, and leaves never raise an expand so the check is
        // cheap. Deferred to Loaded priority so the expanded children are laid out
        // (and the viewport's extent has grown) before we compute the offset.
        TreeViewItem.IsExpandedProperty.Changed.AddClassHandler<TreeViewItem>((item, e) =>
        {
            if (e.GetNewValue<bool>() && GetEnable(item))
                Dispatcher.UIThread.Post(() => ScrollHeaderToTop(item), DispatcherPriority.Loaded);
        });
    }

    private static void ScrollHeaderToTop(TreeViewItem item)
    {
        // Request a rectangle one viewport tall, starting at the item's top, be
        // brought into view. A rectangle as tall as the viewport can only be shown
        // by putting its top at the viewport top, so this TOP-aligns the folder
        // header — and it propagates through both the tree's own MaxHeight scroll
        // and the outer rail scroll. A folder near the end simply scrolls as far as
        // it can (header as near the top as possible, every child still shown).
        //
        // Why not compute the offset by hand: these trees use a VirtualizingStackPanel,
        // whose row positions/extent are estimates mid-realisation. Reading them right
        // after an expand (even at Loaded priority) gave a stale offset that snapped
        // the list toward the top instead of onto the folder. Handing the request to
        // BringIntoView lets the framework realise the row and scroll it correctly.
        double viewportHeight = item.FindAncestorOfType<ScrollViewer>()?.Viewport.Height ?? 0;
        if (viewportHeight <= 0) return;
        item.BringIntoView(new Rect(0, 0, 1, viewportHeight));
    }
}

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
        // The nearest ScrollViewer ancestor is the folder tree's own (MaxHeight-
        // bounded) viewport — the one whose offset jumps on expand.
        if (item.FindAncestorOfType<ScrollViewer>() is not { } scroll) return;

        // Header top relative to the viewport frame; add the current scroll offset
        // to get its content-space Y, then park the viewport there so the header
        // sits at the top. ScrollViewer clamps to its valid range, so a folder near
        // the end scrolls only as far as it can (header as near the top as possible,
        // every child still shown).
        if (item.TranslatePoint(default, scroll) is not { } headerTop) return;
        scroll.Offset = scroll.Offset.WithY(scroll.Offset.Y + headerTop.Y);
    }
}

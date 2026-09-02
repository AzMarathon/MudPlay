using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Threading;

namespace MudPlay.Controls;

// Attached behavior: when a TreeViewItem expands, scroll so the folder and the
// children it just revealed come into view. Opt in via
// TreeViewItemExpandScroll.Enable="True" on a TreeViewItem style — used by the
// Navigation folder trees (loops / auto-lairs / goto favourites).
//
// What DIDN'T work, and why this shape:
//  - item.BringIntoView() (the whole item) is a NO-OP once the header is even
//    partially on screen — which it always is the instant you click it — so nothing
//    scrolls and the children stay below the fold.
//  - Hand-setting the ScrollViewer offset lands on the wrong rows: the tree is
//    virtualized and its offset is in ESTIMATED coordinates that drift as children
//    realise (logging caught the header reporting "at the top" while the panel painted
//    other folders there).
//
// Instead, bring a point ONE VIEWPORT below the header into view. That point is
// currently below the fold, so the framework must scroll down to reach it — and since
// it sits ~a viewport beneath the header, aligning it to the viewport's bottom lifts
// the header to the top with the first children below it. It's still BringIntoView, so
// the framework realises rows as it scrolls and stays accurate where estimated-offset
// math didn't. For a folder shorter than the viewport the point is its own bottom, so
// the whole folder simply comes into view. Deferred to Background priority so the
// children are realised (and the item has re-measured) before we scroll.
public static class TreeViewItemExpandScroll
{
    public static readonly AttachedProperty<bool> EnableProperty =
        AvaloniaProperty.RegisterAttached<TreeViewItem, bool>(
            "Enable", typeof(TreeViewItemExpandScroll));

    public static bool GetEnable(TreeViewItem item) => item.GetValue(EnableProperty);
    public static void SetEnable(TreeViewItem item, bool value) => item.SetValue(EnableProperty, value);

    static TreeViewItemExpandScroll()
    {
        TreeViewItem.IsExpandedProperty.Changed.AddClassHandler<TreeViewItem>((item, e) =>
        {
            if (e.GetNewValue<bool>() && GetEnable(item))
                Dispatcher.UIThread.Post(() => RevealExpanded(item), DispatcherPriority.Background);
        });
    }

    private static void RevealExpanded(TreeViewItem item)
    {
        double viewport = item.FindAncestorOfType<ScrollViewer>()?.Viewport.Height ?? 0;
        if (viewport <= 0) return;

        // A point one viewport below the header — clamped to the folder's own height so
        // a small folder just reveals its own bottom rather than dragging the next
        // folder up.
        double targetY = Math.Min(item.Bounds.Height, viewport) - 1;
        if (targetY <= 0) return;

        item.BringIntoView(new Rect(0, targetY, 1, 1));
    }
}

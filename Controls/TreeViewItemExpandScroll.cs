using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace MudPlay.Controls;

// Attached behavior: when a TreeViewItem expands, scroll its HEADER to the top of
// the enclosing scroll viewport so the folder stays put and the children it just
// revealed flow into view below it. Opt in via TreeViewItemExpandScroll.Enable="True"
// on a TreeViewItem style — used by the Navigation rail's folder trees and the
// Navigation Management dialog's trees (loops / auto-lairs / goto favourites).
//
// Two problems, and why the code looks the way it does:
//  1. Alignment. Plain item.BringIntoView() reveals the item's FULL rectangle —
//     header plus the tall subtree it just expanded — and for a subtree taller than
//     the viewport that bottom-aligns, shoving the header off the top. We instead
//     bring a rectangle exactly one viewport tall, anchored at the item's top, into
//     view: a viewport-tall rect can only be shown by putting its top at the viewport
//     top, so the header top-aligns. It propagates through the tree's own MaxHeight
//     scroll and the outer rail scroll alike.
//  2. Timing. These trees use a VirtualizingStackPanel, which re-measures across
//     SEVERAL layout passes as it settles an expand and resets the scroll offset
//     toward the top on one of them — a single post-expand scroll (at any priority)
//     gets undone by a later pass, so the folder snaps back to the top. Rather than
//     guess which pass resets it, re-assert the header-to-top on EACH layout pass
//     until the position holds for two passes running, then stop (a hard cap bounds
//     it so we never keep fighting the user's own scrolling afterward).
public static class TreeViewItemExpandScroll
{
    public static readonly AttachedProperty<bool> EnableProperty =
        AvaloniaProperty.RegisterAttached<TreeViewItem, bool>(
            "Enable", typeof(TreeViewItemExpandScroll));

    public static bool GetEnable(TreeViewItem item) => item.GetValue(EnableProperty);
    public static void SetEnable(TreeViewItem item, bool value) => item.SetValue(EnableProperty, value);

    // Two stable passes = settled; the cap is the escape hatch if it never settles
    // (both are just a few frames of layout — imperceptible, and over before the
    // user could scroll themselves).
    private const int StablePassesToStop = 2;
    private const int MaxPasses = 16;

    static TreeViewItemExpandScroll()
    {
        TreeViewItem.IsExpandedProperty.Changed.AddClassHandler<TreeViewItem>((item, e) =>
        {
            if (e.GetNewValue<bool>() && GetEnable(item))
                HoldHeaderAtTop(item);
        });
    }

    private static void HoldHeaderAtTop(TreeViewItem item)
    {
        int stable = 0, total = 0;
        void OnLayout(object? sender, EventArgs e)
        {
            bool atTop = BringHeaderToTop(item);
            stable = atTop ? stable + 1 : 0;
            if (stable >= StablePassesToStop || ++total >= MaxPasses)
                item.LayoutUpdated -= OnLayout;
        }
        item.LayoutUpdated += OnLayout;
        BringHeaderToTop(item);
    }

    // Aligns the header to the top of its scroll viewport; returns true once it is
    // already there (so the caller can tell the list has settled).
    private static bool BringHeaderToTop(TreeViewItem item)
    {
        if (FindScrollableAncestor(item) is not { Viewport.Height: > 0 } scroll) return false;

        // Already at the top (header within ~1px of the viewport top) → nothing to do.
        if (item.TranslatePoint(default, scroll) is { } p && p.Y is > -1 and < 2) return true;

        item.BringIntoView(new Rect(0, 0, 1, scroll.Viewport.Height));
        return false;
    }

    // Nearest ancestor that can actually scroll vertically — the tree's own MaxHeight
    // viewport when it's overflowing, otherwise the outer rail scroll.
    private static ScrollViewer? FindScrollableAncestor(Visual from)
    {
        for (Visual? v = from.GetVisualParent(); v is not null; v = v.GetVisualParent())
            if (v is ScrollViewer sv && sv.Extent.Height - sv.Viewport.Height > 0.5)
                return sv;
        return null;
    }
}

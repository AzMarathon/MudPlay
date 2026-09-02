using System;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace MudPlay.Controls;

// Attached behavior: when a TreeViewItem expands, scroll its HEADER to the top of
// the enclosing scroll viewport so the folder stays put and the children it just
// revealed flow into view below it. Opt in via TreeViewItemExpandScroll.Enable="True".
//
// DIAGNOSTIC BUILD: the fix keeps snapping the folder back to the top on expand and
// four blind attempts haven't caught the cause, so this version logs the full
// ancestor scroll-chain state (viewport / extent / offset + the header's Y in each)
// at every layout pass to the Program Log under source "NavScroll". Expand one
// folder, read the lines, and the reset shows itself. Strip the logging once the
// cause is understood.
public static class TreeViewItemExpandScroll
{
    public static readonly AttachedProperty<bool> EnableProperty =
        AvaloniaProperty.RegisterAttached<TreeViewItem, bool>(
            "Enable", typeof(TreeViewItemExpandScroll));

    public static bool GetEnable(TreeViewItem item) => item.GetValue(EnableProperty);
    public static void SetEnable(TreeViewItem item, bool value) => item.SetValue(EnableProperty, value);

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
        string name = FolderName(item);
        Log($"EXPAND '{name}' enable=true{ScrollLine(item)}");

        int stable = 0, total = 0;
        void OnLayout(object? sender, EventArgs e)
        {
            Log($"  '{name}' p{total} pre{ScrollLine(item)}");
            bool atTop = BringHeaderToTop(item, name, total);
            stable = atTop ? stable + 1 : 0;
            if (stable >= StablePassesToStop || ++total >= MaxPasses)
            {
                item.LayoutUpdated -= OnLayout;
                Log($"  '{name}' STOP after {total} pass(es), stable={stable}{ScrollLine(item)}");
            }
        }
        item.LayoutUpdated += OnLayout;
        BringHeaderToTop(item, name, -1);
    }

    private static bool BringHeaderToTop(TreeViewItem item, string name, int pass)
    {
        if (FindScrollableAncestor(item) is not { Viewport.Height: > 0 } scroll)
        {
            Log($"    '{name}' p{pass} no-scrollable-ancestor");
            return false;
        }

        // BringIntoView is a no-op once the header is even partially visible, so set
        // the offset ourselves: the header's Y within the viewport, added to the
        // current offset, is its content-space Y — park the viewport there so the
        // header sits at the top and its children fill the space below. Clamp to the
        // scrollable range (a folder near the end scrolls only as far as it can). The
        // virtualized panel keeps re-estimating the extent for a few passes after an
        // expand, which drifts the header; re-asserting each pass converges on it.
        if (item.TranslatePoint(default, scroll)?.Y is not { } headerY) return false;
        double max = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        double target = Math.Clamp(scroll.Offset.Y + headerY, 0, max);

        if (Math.Abs(headerY) < 2)
        {
            Log($"    '{name}' p{pass} at-top off={scroll.Offset.Y:0}");
            return true;
        }

        Log($"    '{name}' p{pass} set off={scroll.Offset.Y:0}->{target:0} headerY={headerY:0} max={max:0}");
        scroll.Offset = scroll.Offset.WithY(target);
        return false;
    }

    private static ScrollViewer? FindScrollableAncestor(Visual from)
    {
        for (Visual? v = from.GetVisualParent(); v is not null; v = v.GetVisualParent())
            if (v is ScrollViewer sv && sv.Extent.Height - sv.Viewport.Height > 0.5)
                return sv;
        return null;
    }

    // ----- diagnostics ---------------------------------------------------

    private static void Log(string message) =>
        MudPlay.Services.AppServices.CurrentOrNull?.Log.Info("NavScroll", message);

    // The folder's display name, read reflectively so this Controls-layer class
    // doesn't take a ViewModels dependency for a temporary diagnostic.
    private static string FolderName(TreeViewItem item) =>
        item.DataContext?.GetType().GetProperty("Name")?.GetValue(item.DataContext) as string
        ?? item.DataContext?.GetType().Name ?? "?";

    // Every ancestor ScrollViewer on one line: index, viewport/extent/offset, and
    // the header's Y relative to that viewport (negative = above the fold).
    private static string ScrollLine(TreeViewItem item)
    {
        var sb = new StringBuilder();
        int i = 0;
        for (Visual? v = item.GetVisualParent(); v is not null; v = v.GetVisualParent())
            if (v is ScrollViewer sv)
            {
                double hy = item.TranslatePoint(default, sv)?.Y ?? double.NaN;
                sb.Append($" [sv{i} vp{sv.Viewport.Height:0} ext{sv.Extent.Height:0} off{sv.Offset.Y:0} hY{hy:0}]");
                i++;
            }
        return sb.Length == 0 ? " [no-scrollviewer-ancestor]" : sb.ToString();
    }
}

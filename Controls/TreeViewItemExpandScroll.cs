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
// DIAGNOSTIC BUILD (round 2): the offset-set converges in sv0's own coordinates
// (headerY -> 0) yet the folder still isn't visually where expected, so this logs the
// header's position relative to the WINDOW too (winY) — if winY isn't near the top of
// the visible list, sv0 isn't the scroller that matters and we're setting the wrong
// one. Source "NavScroll". Strip once understood.
public static class TreeViewItemExpandScroll
{
    public static readonly AttachedProperty<bool> EnableProperty =
        AvaloniaProperty.RegisterAttached<TreeViewItem, bool>(
            "Enable", typeof(TreeViewItemExpandScroll));

    public static bool GetEnable(TreeViewItem item) => item.GetValue(EnableProperty);
    public static void SetEnable(TreeViewItem item, bool value) => item.SetValue(EnableProperty, value);

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
        Log($"EXPAND '{name}'{ScrollLine(item)}{WindowLine(item)}");

        int passes = 0;
        void OnLayout(object? sender, EventArgs e)
        {
            bool done = BringHeaderToTop(item, name, passes);
            if (done || ++passes >= MaxPasses)
            {
                item.LayoutUpdated -= OnLayout;
                Log($"  '{name}' STOP done={done} passes={passes}{ScrollLine(item)}{WindowLine(item)}");
            }
        }
        item.LayoutUpdated += OnLayout;
        BringHeaderToTop(item, name, -1);
    }

    private static bool BringHeaderToTop(TreeViewItem item, string name, int pass)
    {
        if (FindScrollableAncestor(item) is not { Viewport.Height: > 0 } scroll)
        {
            Log($"    '{name}' p{pass} no-scrollable{WindowLine(item)}");
            return false;
        }
        if (item.TranslatePoint(default, scroll)?.Y is not { } headerY)
        {
            Log($"    '{name}' p{pass} header-not-realised");
            return false;
        }

        double max = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        double target = Math.Clamp(scroll.Offset.Y + headerY, 0, max);
        if (Math.Abs(target - scroll.Offset.Y) < 0.5)
        {
            Log($"    '{name}' p{pass} settled off={scroll.Offset.Y:0} headerY={headerY:0}{WindowLine(item)}");
            return true;
        }

        Log($"    '{name}' p{pass} set off={scroll.Offset.Y:0}->{target:0} headerY={headerY:0}{WindowLine(item)}");
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

    private static string FolderName(TreeViewItem item) =>
        item.DataContext?.GetType().GetProperty("Name")?.GetValue(item.DataContext) as string
        ?? item.DataContext?.GetType().Name ?? "?";

    // Header Y relative to the window + the window's client height — the unambiguous
    // "where is it on screen" that a scroll-viewport-local number can't reveal.
    private static string WindowLine(TreeViewItem item)
    {
        if (TopLevel.GetTopLevel(item) is not { } top) return " [no-toplevel]";
        double y = item.TranslatePoint(default, top)?.Y ?? double.NaN;
        return $" winY={y:0}/{top.Bounds.Height:0}";
    }

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
        return sb.Length == 0 ? " [no-sv]" : sb.ToString();
    }
}

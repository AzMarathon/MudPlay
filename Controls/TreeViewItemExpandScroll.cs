using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace MudPlay.Controls;

// Attached behavior: when a TreeViewItem expands, bring it into view so the children
// it just revealed aren't left below the fold. Opt in via
// TreeViewItemExpandScroll.Enable="True" on a TreeViewItem style — used by the
// Navigation folder trees (loops / auto-lairs / goto favourites).
//
// Why BringIntoView and not a hand-set offset: these trees are virtualized
// (VirtualizingStackPanel), whose scroll offset lives in ESTIMATED coordinates that
// shift as a folder's children realise. Setting the offset by hand to pin the header
// to the top therefore lands on the wrong rows — confirmed by logging, where the
// header reported "at the top" while the panel painted entirely different folders
// there. The framework's own BringIntoView realises the row and scrolls to its real
// position, which is reliable. For a folder taller than the viewport it reveals the
// lower portion rather than pinning the header to the very top — an accepted tradeoff
// for keeping the tree virtualized (precise top-alignment isn't achievable while it
// is). Deferred to Background priority so the expanded children are realised — and
// the estimated extent has settled — before we scroll.
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
        // scroll, and leaves never raise an expand so the check is cheap.
        TreeViewItem.IsExpandedProperty.Changed.AddClassHandler<TreeViewItem>((item, e) =>
        {
            if (e.GetNewValue<bool>() && GetEnable(item))
                Dispatcher.UIThread.Post(item.BringIntoView, DispatcherPriority.Background);
        });
    }
}

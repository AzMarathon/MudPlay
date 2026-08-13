using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace MudPlay.Controls;

// Attached behavior: when a TreeViewItem expands, scroll it into view so the
// children it just revealed aren't left below the fold. Opt in via
// TreeViewItemExpandScroll.Enable="True" on a TreeViewItem style — used by the
// Navigation rail's folder trees (loops / auto-lairs / goto favourites), whose
// outer rail ScrollViewer otherwise doesn't follow an expand, forcing the user to
// scroll down to find the section they just opened.
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
        // before we bring the item into view.
        TreeViewItem.IsExpandedProperty.Changed.AddClassHandler<TreeViewItem>((item, e) =>
        {
            if (e.GetNewValue<bool>() && GetEnable(item))
                Dispatcher.UIThread.Post(item.BringIntoView, DispatcherPriority.Loaded);
        });
    }
}

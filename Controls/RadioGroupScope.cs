using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace MudPlay.Controls;

// Keeps a view's named radio groups to that one copy of the view.
//
// Avalonia files a named radio button that is checked while not attached to a
// window under one registry shared by the whole app: a view built before it is
// shown, a window's buttons before Show and after Close, a closed popup's. Every
// Settings window builds all its section views before they are shown (for its
// search), so a closed window's views, until they are collected, share their groups
// with the next window's. Checking a box in the new view then unchecks its twin in
// the old one, which writes into the old view's model. Where that model checks the
// pair's other half in answer (a pair bound to one bool and its inverse), Avalonia
// adds the re-checked box to the very list it is walking, every time, so the walk
// never ends: the UI thread spins, allocating, until the client is killed (report
// paradigm-20261019-095332).
//
// ToThisView suffixes each group name with the view's own number, so each copy has
// groups of its own. Call it in the constructor, right after InitializeComponent,
// before any DataContext reaches the buttons. Buttons made from a template (a
// DataTemplate, a ContentTemplate) are not there yet and are not renamed.
//
// Each scoped name is a key of its own in the shared registry, kept while any of its
// buttons is filed there. Release, when the view is done with, takes them out, so
// closing a window leaves nothing behind. A window scoped here releases itself when
// it closes; the owner of a view built for a window releases it then.
public static class RadioGroupScope
{
    private static int _views;

    public static void ToThisView(Control view)
    {
        ArgumentNullException.ThrowIfNull(view);
        string suffix = $"#{Interlocked.Increment(ref _views)}";
        foreach (RadioButton button in Named(view))
            button.GroupName += suffix;
        if (view is Window window)
            window.Closed += (_, _) => Release(window);
    }

    // Clearing the name has Avalonia drop the button from its group, and the group's
    // key once it is empty. The buttons no longer exclude each other by name.
    public static void Release(Control view)
    {
        ArgumentNullException.ThrowIfNull(view);
        foreach (RadioButton button in Named(view).ToList())
            button.GroupName = null;
    }

    private static IEnumerable<RadioButton> Named(Control view) =>
        view.GetLogicalDescendants().OfType<RadioButton>().Where(b => !string.IsNullOrEmpty(b.GroupName));
}

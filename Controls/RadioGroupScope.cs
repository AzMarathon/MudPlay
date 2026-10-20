using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace MudPlay.Controls;

// Keeps a view's named radio groups to that one copy of the view.
//
// Avalonia files a named radio button that is in no window under one registry
// shared by the whole app, and every Settings window builds all its section views
// before they are shown (for its search), so a closed window's views, until they
// are collected, share their groups with the next window's. Checking a box in the
// new view then unchecks its twin in the old one, which writes into the old view's
// model. Where that model checks the pair's other half in answer (a pair bound to one
// bool and its inverse), Avalonia adds the re-checked box to the very list it is
// walking, every time, so the walk never ends: the UI thread spins, allocating,
// until the client is killed (reports paradigm-20261019-095332 and others).
//
// Suffixing each group name with the view's own number gives each copy groups of
// its own. Call it in the view's constructor, after InitializeComponent and before
// any DataContext reaches the buttons. Buttons made later from a template are not
// renamed; they exist only once the view is in a window, whose own registry holds
// them.
public static class RadioGroupScope
{
    private static int _views;

    public static void ToThisView(Control view)
    {
        ArgumentNullException.ThrowIfNull(view);
        string suffix = $"#{Interlocked.Increment(ref _views)}";
        foreach (RadioButton button in view.GetLogicalDescendants().OfType<RadioButton>())
            if (!string.IsNullOrEmpty(button.GroupName))
                button.GroupName += suffix;
    }
}

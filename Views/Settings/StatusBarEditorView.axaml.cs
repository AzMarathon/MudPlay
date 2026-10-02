using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MudPlay.Services;
using MudPlay.ViewModels.Settings;

namespace MudPlay.Views.Settings;

// The status-bar layout editor. Its two menus are built here on click, since their
// contents depend on what was clicked: the Add menu (the catalogue, one fly-out per
// group) and an item's own menu (move within its side, move to another side or
// row, remove).
public partial class StatusBarEditorView : UserControl
{
    public StatusBarEditorView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnAddClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: StatusBarZoneEditor zone } button) return;

        MenuFlyout menu = new();
        foreach (IGrouping<string, StatusBarItemDef> group in StatusBarItemCatalogue.Groups)
        {
            MenuItem groupItem = new() { Header = group.Key };
            foreach (StatusBarItemDef def in group)
            {
                MenuItem item = new() { Header = def.Label };
                ToolTip.SetTip(item, def.Description);
                item.Click += (_, _) => zone.Add(def);
                groupItem.Items.Add(item);
            }
            menu.Items.Add(groupItem);
        }
        menu.ShowAt(button);
    }

    private void OnEntryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: StatusBarEntryEditor entry } button) return;
        StatusBarZoneEditor zone = entry.Zone;

        MenuFlyout menu = new();
        menu.Items.Add(Action("Move earlier", zone.CanMove(entry, -1), () => zone.Move(entry, -1)));
        menu.Items.Add(Action("Move later", zone.CanMove(entry, +1), () => zone.Move(entry, +1)));

        MenuItem moveTo = new() { Header = "Move to" };
        foreach (StatusBarRowEditor row in zone.Row.AllRows)
            foreach (StatusBarZoneEditor target in row.Zones)
            {
                if (ReferenceEquals(target, zone)) continue;
                string where = zone.Row.AllRows.Count > 1 ? $"{row.Title} — {target.Title}" : target.Title;
                moveTo.Items.Add(Action(where, true, () => target.MoveHere(entry)));
            }
        menu.Items.Add(moveTo);

        menu.Items.Add(new Separator());
        menu.Items.Add(Action("Remove", true, () => zone.Remove(entry)));
        menu.ShowAt(button);
    }

    private static MenuItem Action(string header, bool enabled, Action run)
    {
        MenuItem item = new() { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => run();
        return item;
    }
}

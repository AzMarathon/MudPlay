using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MudPlay.ViewModels;

namespace MudPlay.Views;

// The Round Totals window (View → Round Totals): the latest round's damage table.
// It sizes itself to its content, so only its position is remembered, and it opens
// without taking focus from the terminal.
public partial class RoundTotalsWindow : Window
{
    public RoundTotalsWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        MudPlay.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "round-totals", autoSize: true);
        Closed += (_, _) => (DataContext as System.IDisposable)?.Dispose();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // The Rows menu: this window's own choice of rows, each a tick that applies at
    // once.
    private void OnRowsClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not RoundTotalsViewModel vm) return;

        MenuFlyout menu = new();
        menu.Items.Add(Tick("Me", vm.ShowSelf, on => vm.ShowSelf = on));
        menu.Items.Add(Tick("Party", vm.ShowParty, on => vm.ShowParty = on));
        menu.Items.Add(Tick("Other players", vm.ShowPlayers, on => vm.ShowPlayers = on));
        menu.Items.Add(Tick("Monsters", vm.ShowMonsters, on => vm.ShowMonsters = on));
        menu.Items.Add(new Separator());
        menu.Items.Add(Tick("One row per monster", vm.EachMonster, on => vm.EachMonster = on));
        menu.Items.Add(Tick("Cap at monster HP", vm.CapAtMonsterHp, on => vm.CapAtMonsterHp = on));
        menu.ShowAt(button);
    }

    private static MenuItem Tick(string header, bool on, System.Action<bool> set)
    {
        MenuItem item = new() { Header = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = on };
        item.Click += (_, _) => set(!on);
        return item;
    }
}

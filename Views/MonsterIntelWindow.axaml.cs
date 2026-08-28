using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MudPlay.ViewModels;

namespace MudPlay.Views;

// Modeless Monster Intel window. Bound to MonsterIntelViewModel; code-behind
// only attaches the persisted window layout, wires global hotkeys, and closes
// the window when the VM's in-window Close button fires.
public partial class MonsterIntelWindow : Window
{
    public MonsterIntelWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        MudPlay.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "monster-intel");
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MonsterIntelViewModel vm) vm.CloseRequested += Close;
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using MudPlay.ViewModels.CharacterWorkshop;

namespace MudPlay.Views.CharacterWorkshop;

public partial class DeathSectionView : UserControl
{
    public DeathSectionView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Double-click a death row → centre the map on that death's map/room. The
    // first tap of the double selects the row (sets SelectedRecord), so the VM
    // acts on the row just clicked. With nothing selected the VM does nothing; a
    // double-tap on a header while a row is selected still re-centres on that row.
    private void OnRecordDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is DeathSectionViewModel vm) vm.ShowSelectedOnMap();
    }
}

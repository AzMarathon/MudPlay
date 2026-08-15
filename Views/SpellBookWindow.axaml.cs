using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using MudPlay.ViewModels;

namespace MudPlay.Views;

// Modeless Spell Book window. Bound to SpellBookViewModel;
// code-behind only attaches the persisted window-layout, wires the
// global-hotkeys handler, and disposes the VM on close.
public partial class SpellBookWindow : Window
{
    public SpellBookWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        MudPlay.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "spellbook");
        Closed += OnClosed;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Double-click a spell row → open the record of the item that teaches it.
    // Trainer-only spells (no teaching item) do nothing. The VM owns the lookup;
    // the record dialog is opened here to keep the VM free of AppServices.
    private void OnSpellRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (SpellGrid.SelectedItem is SpellBookRowViewModel row
            && DataContext is SpellBookViewModel vm
            && vm.TeachingItemNumberFor(row) is int number && number > 0)
            _ = MudPlay.Services.AppServices.Current.ItemRecord.OpenAsync(number);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is SpellBookViewModel vm) vm.Dispose();
    }
}

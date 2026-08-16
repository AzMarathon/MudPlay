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

    // Double-click a spell row → open the record of whatever teaches it: the item
    // for a normal spell, or the trainer NPC's record for a spell learned from an NPC
    // (a Paladin's divine disfavour / greater healing). Spells with neither do
    // nothing. The VM owns the lookups; the records are opened here to keep the VM
    // free of AppServices.
    //
    // Reach the grid via `sender`, NOT an x:Name field: this window's own
    // InitializeComponent (=> AvaloniaXamlLoader.Load) doesn't populate the
    // generated named-control fields (only the Avalonia-generated
    // InitializeComponent does), so a `SpellGrid` field reference would be null.
    private void OnSpellRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not DataGrid grid
            || grid.SelectedItem is not SpellBookRowViewModel row
            || DataContext is not SpellBookViewModel vm)
            return;

        if (vm.TeachingItemNumberFor(row) is int item && item > 0)
            _ = MudPlay.Services.AppServices.Current.ItemRecord.OpenAsync(item);
        else if (vm.TeachingNpcNumberFor(row) is int npc && npc > 0)
            MudPlay.Services.AppServices.Current.OpenMonsterGameData(npc);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is SpellBookViewModel vm) vm.Dispose();
    }
}

using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MudPlay.Game.Combat;
using MudPlay.ViewModels;

namespace MudPlay.Views;

// Modeless Monster Intel window. Bound to MonsterIntelViewModel; code-behind
// attaches the persisted window layout, wires global hotkeys, closes the
// window when the VM's in-window Close button fires, syncs the DataGrid's
// multi-selection into the VM's SelectedEntries for the comparison view,
// persists the list/detail pane split ratio, and disposes the VM on close (it
// holds live room / observation / inventory / spellbook / player-state
// subscriptions).
public partial class MonsterIntelWindow : Window
{
    // Stable id under which the list/detail column split persists in
    // CharacterProfile.SplitterRatios (mirrors MonsterEditDialog's own use of
    // SplitterLayoutStore for the same kind of two-pane split).
    private const string SplitterId = "MonsterIntelWindow";

    public MonsterIntelWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        MudPlay.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "monster-intel");
        MudPlay.Services.AppServices.Current.SplitterLayouts.AttachGrid(
            owner: this, grid: this.FindControl<Grid>("PanesGrid")!,
            leftColumnIndex: 0, rightColumnIndex: 2, id: SplitterId);
        Closed += OnClosed;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MonsterIntelViewModel vm) vm.CloseRequested += Close;
        };
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is MonsterIntelViewModel vm) vm.Dispose();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Avalonia's DataGrid exposes SelectedItems as a non-bindable IList, so
    // this has to be wired imperatively (mirrors GameDataTableSectionView's
    // own SelectedRows sync for the same limitation). Reached via `sender`,
    // not a named-control field — this window's own InitializeComponent
    // (=> AvaloniaXamlLoader.Load) doesn't populate those (see
    // MonsterIntelWindow's SpellBookWindow-style constructor / that window's
    // own OnSpellRowDoubleTapped comment for the same constraint).
    private void OnMonsterGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not DataGrid grid || DataContext is not MonsterIntelViewModel vm) return;
        vm.SelectedEntries.Clear();
        foreach (object? item in grid.SelectedItems)
            if (item is MonsterIntelEntry entry) vm.SelectedEntries.Add(entry);
        vm.NotifyComparisonChanged();
    }
}

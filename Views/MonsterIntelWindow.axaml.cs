using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using MudPlay.Game.Combat;
using MudPlay.ViewModels;

namespace MudPlay.Views;

// Modeless Monster Intel window. Bound to MonsterIntelViewModel; code-behind
// attaches the persisted window layout, wires global hotkeys, persists the
// list/detail pane split ratio, and disposes the VM on close (it holds live
// room / observation / inventory / spellbook / player-state subscriptions).
// The title-bar X (or the toggle hotkey) closes it — there's no in-window
// Close button.
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
    }

    // Double-click a monster row → open its full record in the Game Data Browser
    // (the same record the Browser's Monsters tab opens). Reach the grid via
    // `sender`, not an x:Name field — see SpellBookWindow for why that field
    // would be null under AvaloniaXamlLoader.Load.
    private void OnMonsterRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: MonsterIntelEntry entry } && entry.Number > 0)
            _ = MudPlay.Services.AppServices.Current.OpenMonsterRecordAsync(entry.Number);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is MonsterIntelViewModel vm) vm.Dispose();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

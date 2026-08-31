using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using MudPlay.Game.Combat;
using MudPlay.Services;
using MudPlay.ViewModels;

namespace MudPlay.Views;

// Modeless Monster Intel window. Bound to MonsterIntelViewModel; code-behind
// attaches the persisted window layout, wires global hotkeys, closes the
// window when the VM's in-window Close button fires, persists the
// list/detail pane split ratio, and disposes the VM on close (it holds live
// room / observation / inventory / spellbook / player-state subscriptions).
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

    // Double-click a monster → jump to its Game Data Browser record (opens
    // or re-focuses the browser at the Monsters section and selects the
    // matching row). Mirrors ItemFinderWindow's OnRowDoubleTapped. A
    // double-tap also selects the row, so MonsterGrid.SelectedItem is the
    // double-clicked entry; Monster Intel stays open (modeless) alongside it.
    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (this.FindControl<DataGrid>("MonsterGrid")?.SelectedItem is MonsterIntelEntry entry)
            AppServices.Current.OpenMonsterGameData(entry.Number);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

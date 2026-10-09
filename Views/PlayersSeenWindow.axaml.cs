using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MudPlay.Models.Profile;
using MudPlay.ViewModels;

namespace MudPlay.Views;

// Modeless Session Stats → Players Seen window. Bound to PlayersSeenViewModel;
// code-behind hands a row's double-click to the VM and disposes the VM on close
// (unsubscribing it from the tracker) — everything else is XAML.
public partial class PlayersSeenWindow : Window
{
    public PlayersSeenWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        MudPlay.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "playersseen");
        Closed += OnClosed;
        // The row's text is selectable and takes the double-tap for itself (it selects
        // a word), so listen for handled ones too.
        this.FindControl<ListBox>("RowsList")?.AddHandler(
            DoubleTappedEvent, OnRowDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is PlayersSeenViewModel vm
            && e.Source is StyledElement { DataContext: PlayerSighting sighting })
            vm.ShowOnMap(sighting);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is PlayersSeenViewModel vm) vm.Dispose();
    }
}

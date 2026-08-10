using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MudPlay.ViewModels;

namespace MudPlay.Views;

// Modeless Session Stats → Players Seen window. Bound to PlayersSeenViewModel;
// code-behind only disposes the VM on close (unsubscribing it from the tracker)
// — everything else is XAML.
public partial class PlayersSeenWindow : Window
{
    public PlayersSeenWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        MudPlay.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "playersseen");
        Closed += OnClosed;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is PlayersSeenViewModel vm) vm.Dispose();
    }
}

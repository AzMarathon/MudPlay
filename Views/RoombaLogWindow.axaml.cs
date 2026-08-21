using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MudPlay.ViewModels;

namespace MudPlay.Views;

// Modeless, read-only Roomba run log opened from the GH Management tab. Code-behind
// only disposes the VM on close (unsubscribing it from the sweep) — everything else
// is XAML.
public partial class RoombaLogWindow : Window
{
    public RoombaLogWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        MudPlay.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "roombalog");
        Closed += OnClosed;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is RoombaLogViewModel vm) vm.Dispose();
    }
}

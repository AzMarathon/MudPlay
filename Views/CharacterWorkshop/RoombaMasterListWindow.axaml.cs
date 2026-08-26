using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MudPlay.ViewModels.CharacterWorkshop;

namespace MudPlay.Views.CharacterWorkshop;

// Modeless, read-only Roomba master inventory list opened from the GH
// Management tab. Code-behind only disposes the VM on close (unsubscribing it
// from GhItemLocationStore) — everything else is XAML.
public partial class RoombaMasterListWindow : Window
{
    public RoombaMasterListWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        MudPlay.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "roombamasterlist");
        Closed += OnClosed;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is RoombaMasterListViewModel vm) vm.Dispose();
    }
}

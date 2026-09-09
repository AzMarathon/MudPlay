using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MudPlay.Views;

// Modeless Profile Management window. Bound to ViewModels.ProfileManagerViewModel;
// code-behind only attaches the persisted window-layout and wires the
// global-hotkeys handler so chord forwards still work when it has focus.
public partial class ProfileManagerWindow : Window
{
    public ProfileManagerWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        MudPlay.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "profilemgr");
        Closed += OnClosed;
    }

    // Dispose the VM so it detaches from ProfileService events — otherwise the
    // closed window's VM lingers, re-created fresh on every reopen (a slow leak).
    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is ViewModels.ProfileManagerViewModel vm) vm.Dispose();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

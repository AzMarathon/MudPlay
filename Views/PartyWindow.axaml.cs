using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MudPlay.Views;

// Modeless Party window. Bound to ViewModels.PartyViewModel;
// code-behind only attaches the persisted window-layout and wires the
// global-hotkeys handler so chord forwards (Ctrl+G etc.) still work
// when the Party window has focus.
public partial class PartyWindow : Window
{
    public PartyWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        MudPlay.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "party");
        Closed += OnClosed;
    }

    // Dispose the VM so it detaches from app-lifetime PartyState — otherwise the
    // closed window's VM lingers, re-created fresh on every reopen (a slow leak).
    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is ViewModels.PartyViewModel vm) vm.Dispose();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

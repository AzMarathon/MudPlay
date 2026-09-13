using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

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

    // Double-click a BBS row → its Settings page. Gated on the click landing on a
    // row: a double-click in the empty space below the list shouldn't pop the
    // editor for whatever happened to be selected.
    private void OnBbsRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is null) return;
        if (DataContext is ViewModels.ProfileManagerViewModel vm && vm.EditBbsCommand.CanExecute(null))
            vm.EditBbsCommand.Execute(null);
    }

    // Dispose the VM so it detaches from ProfileService events — otherwise the
    // closed window's VM lingers, re-created fresh on every reopen (a slow leak).
    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is ViewModels.ProfileManagerViewModel vm) vm.Dispose();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MudPlay.Views;

// Modeless Buff Watchdog window. Bound to ViewModels.BuffWatchdogViewModel;
// code-behind only attaches the persisted window-layout and the global-hotkeys
// handler (so chord forwards still work when this window has focus), and disposes
// the VM on close so it detaches from the tick / spellbook / profile events.
public partial class BuffWatchdogWindow : Window
{
    public BuffWatchdogWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        MudPlay.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "buffwatchdog");
        Closed += OnClosed;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is ViewModels.BuffWatchdogViewModel vm) vm.Dispose();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

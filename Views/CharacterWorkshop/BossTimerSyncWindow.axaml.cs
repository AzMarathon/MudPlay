using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MudPlay.ViewModels.CharacterWorkshop;

namespace MudPlay.Views.CharacterWorkshop;

// Modeless @timer sync merge window. Code-behind only disposes the VM on close so its
// BossTimerSyncCollector unsubscribes from ChatRouter — everything else is XAML +
// DialogService (which sets DataContext and closes on CloseRequested).
public partial class BossTimerSyncWindow : Window
{
    public BossTimerSyncWindow()
    {
        InitializeComponent();
        Closed += OnClosed;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is BossTimerSyncViewModel vm) vm.Dispose();
    }
}

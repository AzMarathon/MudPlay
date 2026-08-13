using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using MudPlay.Game.Cash;
using MudPlay.ViewModels;

namespace MudPlay.Views;

// Modeless Session Stats → Transaction history window. Bound to
// TransactionHistoryViewModel; code-behind disposes the VM on close
// (unsubscribing it from the tracker) and routes a row double-click to the VM's
// centre-on-map — everything else is XAML.
public partial class TransactionHistoryWindow : Window
{
    public TransactionHistoryWindow()
    {
        InitializeComponent();
        GlobalHotkeys.Attach(this);
        MudPlay.Services.AppServices.Current.WindowLayouts.AttachWindow(this, "transactions");
        Closed += OnClosed;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Double-click a transaction → open the Navigation window centred on its room.
    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: TransactionEntry entry }
            && DataContext is TransactionHistoryViewModel vm)
            vm.ShowOnMap(entry);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is TransactionHistoryViewModel vm) vm.Dispose();
    }
}

using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using MudPlay.ViewModels.Navigation;

namespace MudPlay.Views.Navigation;

// Modeless free-vs-direct route picker. Shown when a user-initiated walk found a
// shorter route through an acquirable gate. See
// ViewModels.Navigation.RouteChoiceDialogViewModel.
public partial class RouteChoiceDialog : Window
{
    private RouteChoiceDialogViewModel? _vm;

    public RouteChoiceDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as RouteChoiceDialogViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
    }

    // When the picker opened in the "Calculating…" state and then populated
    // (IsCalculating → false), the cards appear and the window must grow to fit them.
    // SizeToContent="Height" doesn't reliably re-measure on a content swap after the
    // window is already shown, so nudge it: toggle SizeToContent off and back to force
    // a fresh height measure. Posted so it runs after the populate's binding refresh.
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName)
            && e.PropertyName != nameof(RouteChoiceDialogViewModel.IsCalculating))
            return;

        Dispatcher.UIThread.Post(() =>
        {
            SizeToContent = SizeToContent.Manual;
            SizeToContent = SizeToContent.Height;
        }, DispatcherPriority.Loaded);
    }
}

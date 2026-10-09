using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using MudPlay.ViewModels.GameData;

namespace MudPlay.Views.GameData;

public partial class QuestFlagStepsWindow : Window
{
    private QuestFlagStepsViewModel? _viewModel;

    public QuestFlagStepsWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as QuestFlagStepsViewModel);
        // The view model listens for game-data set swaps for as long as it lives, and the
        // cache outlives this window, so let go when the window closes (the title-bar X
        // never reaches the view model).
        Closed += (_, _) =>
        {
            Attach(null);
            (DataContext as IDisposable)?.Dispose();
        };
    }

    private void Attach(QuestFlagStepsViewModel? viewModel)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel = viewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelChanged;
    }

    // Swapping to another flag starts its steps from the top.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(QuestFlagStepsViewModel.Heading)) return;
        ScrollViewer? scroll = this.FindControl<ScrollViewer>("StepsScroll");
        if (scroll is not null) scroll.Offset = default(Vector);
    }
}

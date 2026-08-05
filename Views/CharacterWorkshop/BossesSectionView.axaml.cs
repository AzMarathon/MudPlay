using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FujinTerm.ViewModels.CharacterWorkshop;

namespace FujinTerm.Views.CharacterWorkshop;

public partial class BossesSectionView : UserControl
{
    // Column indices for the three early-window columns (see BossesSectionView.axaml).
    private const int Early1 = 4, Early2 = 5, Early3 = 6;

    private BossesSectionViewModel? _vm;

    public BossesSectionView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as BossesSectionViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
        ApplyRealmColumns();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BossesSectionViewModel.IsParadigmRealm))
            ApplyRealmColumns();
    }

    // Paradigm shows three early-window columns (5 / 10 / 20% off); Stock collapses
    // them to a single 87.5% column.
    private void ApplyRealmColumns()
    {
        // The x:Name field is null when this runs off DataContextChanged: the view
        // is freshly built and not yet attached, so the generated field isn't
        // assigned (the control IS in the name scope). Reach it through the name
        // scope instead of dereferencing the raw field — same idiom as
        // CalculatorsSectionView. Dereferencing the null field here threw out of the
        // View getter and left the whole tab blank.
        DataGrid? grid = BossGrid ?? this.FindControl<DataGrid>("BossGrid");
        if (grid is null || grid.Columns.Count <= Early3) return;
        bool para = _vm?.IsParadigmRealm ?? true;
        grid.Columns[Early1].Header = para ? "5%" : "87.5%";
        grid.Columns[Early2].IsVisible = para;
        grid.Columns[Early3].IsVisible = para;
    }
}

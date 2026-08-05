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
        if (BossGrid.Columns.Count <= Early3) return;
        bool para = _vm?.IsParadigmRealm ?? true;
        BossGrid.Columns[Early1].Header = para ? "5%" : "87.5%";
        BossGrid.Columns[Early2].IsVisible = para;
        BossGrid.Columns[Early3].IsVisible = para;
    }
}

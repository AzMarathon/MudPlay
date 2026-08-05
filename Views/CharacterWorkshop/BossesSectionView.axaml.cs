using System;
using System.Collections;
using System.ComponentModel;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FujinTerm.ViewModels.CharacterWorkshop;

namespace FujinTerm.Views.CharacterWorkshop;

public partial class BossesSectionView : UserControl
{
    // Column indices for the three early-window columns (see BossesSectionView.axaml).
    private const int Early1 = 4, Early2 = 5, Early3 = 6;

    private BossesSectionViewModel? _vm;
    private string? _timerSortPath;
    private ListSortDirection _timerSortDir = ListSortDirection.Ascending;

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

    // Timer columns sort in three fixed groups regardless of direction — cleanup
    // spawns (DEAD/ALIVE) first, then active (counting) timed respawns, then unset /
    // expired rows — with the toggled direction ordering only within each group. The
    // built-in single-key sort can't express that (it would float the inactive
    // sentinel to the top when descending). Non-timer columns fall through to the
    // DataGrid's default sort.
    private void OnSorting(object? sender, DataGridColumnEventArgs e)
    {
        Func<BossRowViewModel, long>? key = e.Column.SortMemberPath switch
        {
            "FullSortKey" => static r => r.FullSortKey,
            "Early1SortKey" => static r => r.Early1SortKey,
            "Early2SortKey" => static r => r.Early2SortKey,
            "Early3SortKey" => static r => r.Early3SortKey,
            _ => null,
        };
        if (key is null || _vm is null) return;
        e.Handled = true;

        string path = e.Column.SortMemberPath!;
        _timerSortDir = _timerSortPath == path && _timerSortDir == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        _timerSortPath = path;

        _vm.Rows.SortDescriptions.Clear();
        _vm.Rows.SortDescriptions.Add(DataGridSortDescription.FromComparer(
            new ActiveFirstComparer(key, _timerSortDir == ListSortDirection.Descending)));
    }

    // Three fixed groups: cleanup spawns (0), active timed respawns (1), unset /
    // expired (2). Groups never reverse; the toggled direction orders within a group.
    private sealed class ActiveFirstComparer : IComparer
    {
        private readonly Func<BossRowViewModel, long> _key;
        private readonly bool _descending;

        public ActiveFirstComparer(Func<BossRowViewModel, long> key, bool descending)
        {
            _key = key;
            _descending = descending;
        }

        public int Compare(object? x, object? y)
        {
            if (x is not BossRowViewModel a || y is not BossRowViewModel b) return 0;
            int ga = Group(a), gb = Group(b);
            if (ga != gb) return ga.CompareTo(gb);     // cleanup, then active, then inactive
            if (ga == 2) return 0;                     // both inactive → stable
            int c = _key(a).CompareTo(_key(b));
            return _descending ? -c : c;
        }

        private int Group(BossRowViewModel r)
        {
            if (r.IsCleanup) return 0;                 // cleanup spawns always at the top
            return _key(r) == long.MaxValue ? 2 : 1;   // active respawns, then unset / expired
        }
    }
}

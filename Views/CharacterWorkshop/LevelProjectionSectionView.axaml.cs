using System;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using MudPlay.ViewModels.CharacterWorkshop;

namespace MudPlay.Views.CharacterWorkshop;

public partial class LevelProjectionSectionView : UserControl
{
    private LevelProjectionSectionViewModel? _wired;

    public LevelProjectionSectionView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // The grid's columns are a per-character choice, so they're built here rather
    // than declared in XAML. Re-subscribes on every DataContext swap and drops the
    // previous handler — the section view-model is recreated per profile.
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_wired is not null) _wired.ColumnsChanged -= RebuildColumns;
        _wired = DataContext as LevelProjectionSectionViewModel;
        if (_wired is not null) _wired.ColumnsChanged += RebuildColumns;
        RebuildColumns();
    }

    private void RebuildColumns()
    {
        if (_wired is null) return;

        ProjectionGrid.Columns.Clear();
        this.TryFindResource("FontMono", out object? mono);
        this.TryFindResource("ChromeFgBrush", out object? fg);
        this.TryFindResource("ChromeFgSecBrush", out object? fgSec);

        foreach (LevelProjectionColumn col in _wired.VisibleColumns)
        {
            var column = new DataGridTextColumn
            {
                Header = col.Header,
                Width = new DataGridLength(col.Width),
                // The row type exposes each cell as a plain string property, so the
                // catalogue's Binding name is the property path.
                Binding = new Binding(col.Binding),
            };
            if (mono is FontFamily font) column.FontFamily = font;
            if ((col.Muted ? fgSec : fg) is IBrush brush) column.Foreground = brush;
            ProjectionGrid.Columns.Add(column);
        }
    }

    // Tint the live character's current-level row. DataGrid recycles row
    // containers, so the background is both set and cleared on every load.
    private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        bool isCurrent = e.Row.DataContext is LevelProjectionRow { IsCurrentLevel: true };
        e.Row.Background = isCurrent
            && this.TryFindResource("ChromeBgElevBrush", out object? res) && res is IBrush brush
            ? brush
            : null;
    }
}

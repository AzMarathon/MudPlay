using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using MudPlay.ViewModels.CharacterWorkshop;

namespace MudPlay.Views.CharacterWorkshop;

// Builds the projection grid's columns from the section view-model's per-character
// picker, so which ones show can't be authored in XAML.
//
// No hand-written InitializeComponent here: the Avalonia name generator owns that
// method (per AvaloniaNameGeneratorBehavior = InitializeComponent) so the
// x:Name="ProjectionGrid" field gets populated. Overriding it manually
// short-circuits the generator and leaves x:Name fields null — which is how the
// column picker first shipped and drew an empty tab, an NRE on
// ProjectionGrid.Columns swallowed by the section's lazy View property.
public partial class LevelProjectionSectionView : UserControl
{
    private LevelProjectionSectionViewModel? _wired;

    public LevelProjectionSectionView()
    {
        InitializeComponent();
        // Either trigger can fire first depending on layout timing. RebuildColumns
        // clears before it builds, so the second one is a harmless no-op.
        DataContextChanged += (_, _) => { WireColumnPicker(); RebuildColumns(); };
        AttachedToVisualTree += (_, _) => { WireColumnPicker(); RebuildColumns(); };
    }

    // Follow the section's column picker across DataContext swaps — the section
    // view-model is rebuilt per profile, and the old one must be released.
    private void WireColumnPicker()
    {
        LevelProjectionSectionViewModel? vm = DataContext as LevelProjectionSectionViewModel;
        if (ReferenceEquals(vm, _wired)) return;
        if (_wired is not null) _wired.ColumnsChanged -= RebuildColumns;
        _wired = vm;
        if (_wired is not null) _wired.ColumnsChanged += RebuildColumns;
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

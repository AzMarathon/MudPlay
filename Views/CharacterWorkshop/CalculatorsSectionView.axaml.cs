using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using MudPlay.ViewModels.CharacterWorkshop;

namespace MudPlay.Views.CharacterWorkshop;

public partial class CalculatorsSectionView : UserControl
{
    private CalculatorsSectionViewModel? _vm;

    // The column path whose sort we just forced (below) and expect to see loop
    // back through the Sorting event — non-null only for that one re-entry.
    private string? _pendingForcedSortPath;

    public CalculatorsSectionView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // A leaderboard reads best largest-first, but the DataGrid's built-in header
    // click always leads with Ascending — burying the top scores at the bottom on
    // the first click. Take the sort over: cancel that default and re-issue the
    // column's sort leading with Descending, toggling to Ascending on a re-click.
    // Sort() reposts through the same Sorting event, so a one-shot path flag lets
    // that forced pass fall through to the grid's own (now direction-honouring) logic.
    private void OnLeaderboardSorting(object? sender, DataGridColumnEventArgs e)
    {
        if (_vm is null) return;
        string? path = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(path)) return;

        if (_pendingForcedSortPath == path)
        {
            _pendingForcedSortPath = null;
            return;
        }

        ListSortDirection dir = ListSortDirection.Descending;
        var sorts = _vm.LeaderboardView.SortDescriptions;
        if (sorts.Count == 1
            && string.Equals(sorts[0].PropertyPath, path, StringComparison.Ordinal)
            && sorts[0].Direction == ListSortDirection.Descending)
            dir = ListSortDirection.Ascending;

        _pendingForcedSortPath = path;
        e.Handled = true;
        e.Column.Sort(dir);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null) _vm.LeaderboardRebuilt -= SizeLeaderboardColumns;
        _vm = DataContext as CalculatorsSectionViewModel;
        if (_vm is not null) _vm.LeaderboardRebuilt += SizeLeaderboardColumns;
    }

    // Size on Loaded, not DataContextChanged: DataContext is set at construction
    // while the view is still detached, where FontMono doesn't resolve and the
    // grid's FontSize isn't settled — measuring there under a fallback font sizes
    // the Experience column too narrow, clipping big (hundreds-of-billions) values
    // on first open. By Loaded the view is attached and laid out, so the measure
    // matches what actually renders.
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        SizeLeaderboardColumns();
    }

    // Avalonia's DataGrid sizes Auto columns against only the REALIZED (visible)
    // rows, so the grid creeps wider as fresh rows — longer gang names and the like —
    // scroll into view. Measure the whole capture up front and pin each column to a
    // fixed pixel width instead, so a capture sizes once and holds; the last column
    // also carries a trailing buffer so the numbers don't butt the right edge.
    private void SizeLeaderboardColumns()
    {
        // The x:Name field can be null when this runs off DataContextChanged: on
        // a freshly-built, not-yet-attached view the generated field isn't
        // assigned yet (the control IS registered in the name scope, though), so
        // reach it through the name scope instead of dereferencing the raw field.
        DataGrid? grid = LeaderboardGrid ?? this.FindControl<DataGrid>("LeaderboardGrid");
        if (_vm is null || grid is null || grid.Columns.Count < 6) return;
        IReadOnlyList<LeaderboardRow> rows = _vm.Leaderboard;
        if (rows.Count == 0) return;

        double fontSize = grid.FontSize > 0 ? grid.FontSize : 13;
        FontFamily mono = ResolveMono();
        var cellFace = new Typeface(mono);
        var headerFace = new Typeface(mono, FontStyle.Normal, FontWeight.Bold);

        // The Rank / XP cells carry their move / trend suffix, so measure that too.
        Func<LeaderboardRow, string>[] cellText =
        {
            row => $"{row.Rank} {row.RankMove}",
            row => row.Name,
            row => row.Class,
            row => row.Guild,
            row => row.Experience,
            row => $"{row.XpPerHour} {row.RateTrend}",
        };
        string[] headers = { "Rank", "Name", "Class", "Gang/Guild", "Experience", "XP/HR" };

        const double cellPad = 22;     // left + right cell padding
        const double sortReserve = 18; // room for the header's sort glyph
        const double tailBuffer = 20;  // gap past the last column to the right edge

        // The Experience column carries the largest, most-scrutinised numbers, so
        // give it two extra mono characters of headroom past its widest value.
        double expHeadroom = Measure("00", cellFace, fontSize);

        double total = 0;
        for (int c = 0; c < 6; c++)
        {
            double widest = Measure(headers[c], headerFace, fontSize) + sortReserve;
            foreach (LeaderboardRow row in rows)
                widest = Math.Max(widest, Measure(cellText[c](row), cellFace, fontSize));

            double px = Math.Ceiling(widest) + cellPad;
            if (c == 4) px += expHeadroom;
            if (c == 5) px += tailBuffer;
            grid.Columns[c].Width = new DataGridLength(px);
            total += px;
        }

        // Pin the reroll-suspect notice to the table's own width so a long
        // suspect list wraps at the grid's right edge instead of running past
        // it. Resolve through the name scope for the same detached-view reason
        // the grid does above — the raw field can be null here.
        TextBlock? notice = RerollNoticeText ?? this.FindControl<TextBlock>("RerollNoticeText");
        if (notice is not null) notice.MaxWidth = total;
    }

    private FontFamily ResolveMono()
        => this.TryFindResource("FontMono", out object? res) switch
        {
            true when res is FontFamily ff => ff,
            true when res is string s => new FontFamily(s),
            _ => FontFamily.Default,
        };

    private static double Measure(string text, Typeface face, double fontSize)
        => new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                             face, fontSize, Brushes.White).Width;
}

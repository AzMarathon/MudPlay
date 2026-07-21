using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using FujinTerm.ViewModels.CharacterWorkshop;

namespace FujinTerm.Views.CharacterWorkshop;

public partial class CalculatorsSectionView : UserControl
{
    private CalculatorsSectionViewModel? _vm;

    public CalculatorsSectionView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null) _vm.LeaderboardRebuilt -= SizeLeaderboardColumns;
        _vm = DataContext as CalculatorsSectionViewModel;
        if (_vm is not null) _vm.LeaderboardRebuilt += SizeLeaderboardColumns;
        SizeLeaderboardColumns();
    }

    // Avalonia's DataGrid sizes Auto columns against only the REALIZED (visible)
    // rows, so the grid creeps wider as fresh rows — longer gang names and the like —
    // scroll into view. Measure the whole capture up front and pin each column to a
    // fixed pixel width instead, so a capture sizes once and holds; the last column
    // also carries a trailing buffer so the numbers don't butt the right edge.
    private void SizeLeaderboardColumns()
    {
        if (_vm is null || LeaderboardGrid.Columns.Count < 6) return;
        IReadOnlyList<LeaderboardRow> rows = _vm.Leaderboard;
        if (rows.Count == 0) return;

        double fontSize = LeaderboardGrid.FontSize > 0 ? LeaderboardGrid.FontSize : 13;
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

        for (int c = 0; c < 6; c++)
        {
            double widest = Measure(headers[c], headerFace, fontSize) + sortReserve;
            foreach (LeaderboardRow row in rows)
                widest = Math.Max(widest, Measure(cellText[c](row), cellFace, fontSize));

            double px = Math.Ceiling(widest) + cellPad;
            if (c == 5) px += tailBuffer;
            LeaderboardGrid.Columns[c].Width = new DataGridLength(px);
        }
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

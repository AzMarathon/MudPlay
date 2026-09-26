using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using MudPlay.Services;

namespace MudPlay.Views.Help;

// Turns a Help topic's markdown body into a stack of Avalonia blocks for the
// content pane: paragraphs (each source line kept on its own line so the guide's
// "**Default:** …" field labels stay separate), "- " bullet lists, and "| … |"
// tables rendered as a bordered Grid. Inline styling (**bold** / *italic* /
// `code` / links) is tokenized by the pure HelpMarkup.ParseInline; this file only
// builds controls, so there's nothing worth unit-testing here — the parsing that
// is lives in HelpMarkup / HelpBook.
public static class HelpContentRenderer
{
    // Body text a notch larger than the app default, and a roomier gap between
    // paragraph blocks — the guide reads as prose, so it wants breathing room
    // rather than the denser sizing the rest of the chrome uses.
    private const double BodyFontSize = 14.5;
    private const double ParagraphGap = 14;

    private static readonly FontFamily MonoFont = new("Consolas, Menlo, monospace");
    private static readonly IBrush CodeBrush = new SolidColorBrush(Color.Parse("#2AA198"));
    private static readonly IBrush GridLineBrush = new SolidColorBrush(Color.Parse("#40808080"));

    // Field-label accent — the guide's recurring "**Default:** …", "**What it
    // does:** …", "**Important notes:** …" convention. Coloring just the label
    // (not the value text after it) gives every one of those ~150 entries a
    // scannable field name without touching the markdown itself.
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#4E9BDE"));

    // ⚠️-prefixed paragraphs (the guide's "not currently functional" callouts)
    // get a tinted box instead of sitting flush with normal prose.
    private static readonly IBrush WarningBackground = new SolidColorBrush(Color.Parse("#22E0A030"));
    private static readonly IBrush WarningBorderBrush = new SolidColorBrush(Color.Parse("#80E0A030"));

    // Search-box matches inside the body — the same amber as a matching topic in the
    // tree, so the eye links the two.
    private static readonly IBrush MatchBackground = new SolidColorBrush(Color.Parse("#99E0B040"));

    // One render's search text, and the first block holding a match (what the pane
    // scrolls to) — threaded through the block builders.
    private sealed class RenderContext(string? highlight)
    {
        public string? Highlight { get; } = highlight;
        public Control? FirstMatch { get; set; }
    }

    public static Control Render(string? body, string? highlight, out Control? firstMatch)
    {
        RenderContext ctx = new(string.IsNullOrWhiteSpace(highlight) ? null : highlight.Trim());
        StackPanel panel = new() { Spacing = ParagraphGap };
        if (!string.IsNullOrWhiteSpace(body))
        {
            string[] lines = body.Replace("\r\n", "\n").Split('\n');
            int i = 0;
            while (i < lines.Length)
            {
                if (lines[i].Trim().Length == 0) { i++; continue; }
                i = IsTableRow(lines[i]) ? AppendTable(ctx, panel, lines, i)
                  : IsBullet(lines[i])   ? AppendBullets(ctx, panel, lines, i)
                  :                        AppendParagraph(ctx, panel, lines, i);
            }
        }
        firstMatch = ctx.FirstMatch;
        return panel;
    }

    private static bool IsTableRow(string line) => line.TrimStart().StartsWith('|');
    private static bool IsBullet(string line) => line.TrimStart().StartsWith("- ", System.StringComparison.Ordinal);
    private static bool IsTableSeparator(string line)
    {
        foreach (char c in line)
            if (c != '|' && c != '-' && c != ':' && c != ' ') return false;
        return line.Contains('-');
    }

    private static bool IsWarning(string line)
    {
        string t = line.TrimStart();
        if (t.StartsWith("**", System.StringComparison.Ordinal)) t = t[2..];
        return t.StartsWith("⚠️", System.StringComparison.Ordinal);
    }

    // Each non-blank source line becomes its own wrapping paragraph block so the
    // guide's per-field labels ("**Default:** …", "**What it does:** …") get space
    // between them (via the panel's Spacing) instead of stacking into a brick. A
    // ⚠️-led line gets a tinted callout box instead of a plain paragraph, so a
    // "not currently functional" note reads as a warning at a glance rather than
    // blending into the surrounding explanation.
    private static int AppendParagraph(RenderContext ctx, StackPanel panel, string[] lines, int i)
    {
        TextBlock tb = new() { TextWrapping = TextWrapping.Wrap, FontSize = BodyFontSize };
        AppendRuns(ctx, tb, lines[i]);

        if (IsWarning(lines[i]))
            panel.Children.Add(new Border
            {
                Background = WarningBackground,
                BorderBrush = WarningBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 7),
                Child = tb,
            });
        else
            panel.Children.Add(tb);
        return i + 1;
    }

    private static int AppendBullets(RenderContext ctx, StackPanel panel, string[] lines, int i)
    {
        StackPanel list = new() { Spacing = 5 };
        while (i < lines.Length && IsBullet(lines[i]))
        {
            string item = lines[i].TrimStart()[2..];
            Grid row = new()
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                Margin = new Thickness(6, 0, 0, 0),
            };
            TextBlock dot = new() { Text = "•", Margin = new Thickness(0, 0, 6, 0), FontSize = BodyFontSize };
            TextBlock text = new() { TextWrapping = TextWrapping.Wrap, FontSize = BodyFontSize };
            AppendRuns(ctx, text, item);
            Grid.SetColumn(text, 1);
            row.Children.Add(dot);
            row.Children.Add(text);
            list.Children.Add(row);
            i++;
        }
        panel.Children.Add(list);
        return i;
    }

    // Markdown pipe table: header row, a "|---|" separator, then data rows.
    private static int AppendTable(RenderContext ctx, StackPanel panel, string[] lines, int i)
    {
        List<string[]> rows = new();
        while (i < lines.Length && IsTableRow(lines[i]))
        {
            if (!IsTableSeparator(lines[i])) rows.Add(SplitCells(lines[i]));
            i++;
        }
        if (rows.Count == 0) return i;

        int cols = rows.Max(r => r.Length);
        // Star columns so the table fills — and never exceeds — the pane width;
        // cells wrap rather than forcing a nested horizontal scroller (kinder to
        // scroll performance, and nothing clips).
        Grid grid = new();
        for (int c = 0; c < cols; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (int r = 0; r < rows.Count; r++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (int r = 0; r < rows.Count; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                string cell = c < rows[r].Length ? rows[r][c] : string.Empty;
                TextBlock tb = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 5), FontSize = BodyFontSize - 1 };
                AppendRuns(ctx, tb, cell, headerBold: r == 0);
                Border box = new()
                {
                    BorderBrush = GridLineBrush,
                    BorderThickness = new Thickness(0, 0, c == cols - 1 ? 0 : 1, r == rows.Count - 1 ? 0 : 1),
                    Child = tb,
                };
                Grid.SetRow(box, r);
                Grid.SetColumn(box, c);
                grid.Children.Add(box);
            }
        }

        panel.Children.Add(new Border
        {
            BorderBrush = GridLineBrush,
            BorderThickness = new Thickness(1),
            Child = grid,
        });
        return i;
    }

    private static string[] SplitCells(string line)
    {
        string t = line.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|')) t = t[..^1];
        return t.Split('|').Select(c => c.Trim()).ToArray();
    }

    private static void AppendRuns(RenderContext ctx, TextBlock owner, string line, bool headerBold = false)
    {
        InlineCollection inlines = owner.Inlines!;
        bool first = true;
        foreach (HelpInline seg in HelpMarkup.ParseInline(line))
        {
            // A bold run ending in ':' that opens the paragraph is a field label
            // ("Default:", "What it does:", "Important notes:", …) — accent it so
            // the field name is scannable at a glance, distinct from the value
            // text that follows on the same line.
            bool label = first && seg.Style == HelpInlineStyle.Bold && seg.Text.TrimEnd().EndsWith(':');
            // Each styled segment is split again around the search matches, so a hit
            // inside bold or code text keeps its styling and gains the highlight.
            foreach ((string text, bool match) in HelpMarkup.SplitMatches(seg.Text, ctx.Highlight))
            {
                Run run = new(text);
                switch (seg.Style)
                {
                    case HelpInlineStyle.Bold: run.FontWeight = FontWeight.Bold; break;
                    case HelpInlineStyle.Italic: run.FontStyle = FontStyle.Italic; break;
                    case HelpInlineStyle.Code:
                        run.FontFamily = MonoFont;
                        run.Foreground = CodeBrush;
                        break;
                }
                if (label) run.Foreground = LabelBrush;
                if (headerBold) run.FontWeight = FontWeight.Bold;
                if (match)
                {
                    run.Background = MatchBackground;
                    ctx.FirstMatch ??= owner;
                }
                inlines.Add(run);
            }
            first = false;
        }
    }
}

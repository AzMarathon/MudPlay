using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using MudPlay.Controls;
using MudPlay.Services;

namespace MudPlay.Views.Settings;

// The Settings search box's view side, the counterpart of Help's HelpMarkdown:
//  - ShownText reads the text a section's settings show (labels, button and check
//    box text, tooltips) off its View, so the sidebar finds a section by what is on
//    screen and not only by its hand-kept SearchableLabels.
//  - Highlight, attached to the content pane, marks the matches in the open
//    section: every occurrence in a label gets Help's solid body-hit look, a setting
//    that matched only through its tooltip gets the softer SearchMatch look on its
//    label, and the pane scrolls to the first one.
//   <ContentControl Content="{Binding SelectedSection.View}"
//                   settings:SettingsSearch.Highlight="{Binding SearchText}"/>
// Both read the same controls (Walk), so a highlighted sidebar entry always has
// something to show once opened.
public static class SettingsSearch
{
    public static readonly AttachedProperty<string?> HighlightProperty =
        AvaloniaProperty.RegisterAttached<ContentControl, string?>(
            "Highlight", typeof(SettingsSearch));

    public static void SetHighlight(ContentControl target, string? value) =>
        target.SetValue(HighlightProperty, value);

    public static string? GetHighlight(ContentControl target) =>
        target.GetValue(HighlightProperty);

    // What each content pane has marked, so the next search (or section) can put
    // it back.
    private sealed class Marks
    {
        public List<(TextBlock Block, string Text)> Hits { get; } = new();
        public List<TextBlock> Labels { get; } = new();
    }

    private static readonly ConditionalWeakTable<ContentControl, Marks> MarksByPane = new();

    static SettingsSearch()
    {
        HighlightProperty.Changed.AddClassHandler<ContentControl>((t, _) => Schedule(t));
        ContentControl.ContentProperty.Changed.AddClassHandler<ContentControl>((t, _) =>
        {
            if (t.IsSet(HighlightProperty)) Schedule(t);
        });
    }

    public static IEnumerable<string> ShownText(Control root)
    {
        foreach (Control c in Walk(root))
        {
            switch (c)
            {
                case TextBlock tb:
                    string? text = TextOf(tb);
                    if (!string.IsNullOrEmpty(text)) yield return text;
                    break;
                case HeaderedContentControl { Header: string header }:
                    yield return header;
                    break;
            }
            if (c is ContentControl { Content: string content }) yield return content;
            if (ToolTip.GetTip(c) is string tip) yield return tip;
        }
    }

    // The controls whose text is a setting's: visible ones only. Value editors and
    // data-bound lists are not entered — what they show is the user's values, not
    // what the settings are called — but their own tooltips still count.
    private static IEnumerable<Control> Walk(Control root)
    {
        if (!root.IsVisible) yield break;
        yield return root;
        if (root is TextBox or NumericUpDown or ComboBox or AutoCompleteBox
            || root is ItemsControl { ItemsSource: not null })
            yield break;
        foreach (ILogical child in root.GetLogicalChildren())
            if (child is Control c)
                foreach (Control d in Walk(c))
                    yield return d;
    }

    // A new section's controls are only templated (a check box's text block made)
    // once laid out, so the marks go on after layout — the moment Help's pane
    // scrolls to its first hit too.
    private static void Schedule(ContentControl pane) =>
        Dispatcher.UIThread.Post(() => Apply(pane), DispatcherPriority.Loaded);

    private static void Apply(ContentControl pane)
    {
        Marks marks = MarksByPane.GetOrCreateValue(pane);
        Clear(marks);

        string? query = GetHighlight(pane);
        if (SearchMatch.IsBlank(query) || pane.Content is not Control root) return;

        Control? first = null;
        foreach (Control c in Walk(root).ToList())
        {
            bool hit = c is TextBlock tb && MarkHits(tb, query!, marks);
            // A label holding the query itself carries the solid hit look already
            // (or will, further down the walk) — the soft look would only muddy it.
            if (!hit && ToolTip.GetTip(c) is string tip && SearchMatch.Matches(tip, query)
                && LabelOf(c) is { } label && !SearchMatch.Matches(TextOf(label), query))
            {
                if (!label.Classes.Contains("SearchMatch"))
                {
                    label.Classes.Add("SearchMatch");
                    marks.Labels.Add(label);
                }
                hit = true;
            }
            if (hit && first is null && TopLevel.GetTopLevel(c) is not null) first = c;
        }

        if (first is not null)
            Dispatcher.UIThread.Post(() => first.BringIntoView(), DispatcherPriority.Loaded);
    }

    // Rewrite the block's text as runs split around the query. The Text is blanked
    // with SetCurrentValue so a bound label keeps its binding: a value change while
    // marked simply replaces the runs with the new, unmarked text.
    private static bool MarkHits(TextBlock tb, string query, Marks marks)
    {
        if (tb.Inlines is { Count: > 0 }) return false;   // authored rich text — left alone
        string? text = tb.Text;
        if (text is null || !SearchMatch.Matches(text, query)) return false;

        tb.SetCurrentValue(TextBlock.TextProperty, string.Empty);
        foreach ((string piece, bool match) in SearchMatch.Split(text, query))
        {
            Run run = new(piece);
            if (match) SearchHighlight.MarkHit(run);
            tb.Inlines!.Add(run);
        }
        marks.Hits.Add((tb, text));
        return true;
    }

    private static void Clear(Marks marks)
    {
        foreach ((TextBlock block, string text) in marks.Hits)
            if (block.Inlines is { Count: > 0 })   // none left when a binding replaced the text
                block.SetCurrentValue(TextBlock.TextProperty, text);
        foreach (TextBlock label in marks.Labels)
            label.Classes.Remove("SearchMatch");
        marks.Hits.Clear();
        marks.Labels.Clear();
    }

    // The label that names a setting whose tooltip matched: a text block is its own
    // label, a check box / button its text, and a number box or drop-down the text
    // block beside it — in a Grid the nearest one to its left on the same row, in
    // any other panel the one just before it.
    private static TextBlock? LabelOf(Control c)
    {
        if (c is TextBlock tb) return tb;
        if (c is ContentControl)
            return c.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(HasText);
        if (c.Parent is not Panel panel) return null;

        if (panel is Grid)
        {
            int row = Grid.GetRow(c), col = Grid.GetColumn(c);
            return panel.Children.OfType<TextBlock>()
                .Where(t => Grid.GetRow(t) == row && Grid.GetColumn(t) < col && HasText(t))
                .OrderByDescending(Grid.GetColumn)
                .FirstOrDefault();
        }
        int at = panel.Children.IndexOf(c);
        return at > 0 && panel.Children[at - 1] is TextBlock before && HasText(before) ? before : null;
    }

    // A marked block holds its text in runs, its Text blanked.
    private static string? TextOf(TextBlock tb) => tb.Inlines is { Count: > 0 } runs ? runs.Text : tb.Text;

    private static bool HasText(TextBlock tb) => !string.IsNullOrEmpty(TextOf(tb));
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace MudPlay.Views.Help;

// Attached properties that render a Help topic's markdown body into a
// ContentControl's Content via HelpContentRenderer, rebuilding whenever the body or
// the search text changes. Highlight is the search box's text: every occurrence in
// the body is highlighted and the pane scrolls to the first one.
//   <ContentControl help:HelpMarkdown.Source="{Binding SelectedTopic.Body}"
//                   help:HelpMarkdown.Highlight="{Binding SearchText}"/>
// Mirrors the ConversationMessageInlines attached-property pattern.
public static class HelpMarkdown
{
    public static readonly AttachedProperty<string?> SourceProperty =
        AvaloniaProperty.RegisterAttached<ContentControl, string?>(
            "Source", typeof(HelpMarkdown));

    public static readonly AttachedProperty<string?> HighlightProperty =
        AvaloniaProperty.RegisterAttached<ContentControl, string?>(
            "Highlight", typeof(HelpMarkdown));

    public static void SetSource(ContentControl target, string? value) =>
        target.SetValue(SourceProperty, value);

    public static string? GetSource(ContentControl target) =>
        target.GetValue(SourceProperty);

    public static void SetHighlight(ContentControl target, string? value) =>
        target.SetValue(HighlightProperty, value);

    public static string? GetHighlight(ContentControl target) =>
        target.GetValue(HighlightProperty);

    static HelpMarkdown()
    {
        SourceProperty.Changed.AddClassHandler<ContentControl>((t, _) => Rebuild(t));
        HighlightProperty.Changed.AddClassHandler<ContentControl>((t, _) => Rebuild(t));
    }

    private static void Rebuild(ContentControl target)
    {
        target.Content = HelpContentRenderer.Render(GetSource(target), GetHighlight(target), out Control? firstMatch);
        // Scroll the first hit into view once it has been laid out — before layout
        // it has no position for the ScrollViewer to bring into view.
        if (firstMatch is not null)
            Dispatcher.UIThread.Post(() => firstMatch.BringIntoView(), DispatcherPriority.Loaded);
    }
}

using Avalonia;
using Avalonia.Controls;

namespace MudPlay.Views.Help;

// Attached property that renders a Help topic's markdown body into a
// ContentControl's Content via HelpContentRenderer, rebuilding whenever the bound
// string changes. Bind it to the selected topic's body:
//   <ContentControl help:HelpMarkdown.Source="{Binding SelectedTopic.Body}"/>
// Mirrors the ConversationMessageInlines attached-property pattern.
public static class HelpMarkdown
{
    public static readonly AttachedProperty<string?> SourceProperty =
        AvaloniaProperty.RegisterAttached<ContentControl, string?>(
            "Source", typeof(HelpMarkdown));

    public static void SetSource(ContentControl target, string? value) =>
        target.SetValue(SourceProperty, value);

    public static string? GetSource(ContentControl target) =>
        target.GetValue(SourceProperty);

    static HelpMarkdown() =>
        SourceProperty.Changed.AddClassHandler<ContentControl>(OnSourceChanged);

    private static void OnSourceChanged(ContentControl target, AvaloniaPropertyChangedEventArgs e) =>
        target.Content = HelpContentRenderer.Render(e.NewValue as string);
}

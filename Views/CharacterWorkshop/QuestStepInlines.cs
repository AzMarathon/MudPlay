using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using MudPlay.ViewModels.CharacterWorkshop;

namespace MudPlay.Views.CharacterWorkshop;

// Attached behaviour that renders a quest step's QuestStepSegmentViewModel list
// into a TextBlock's inline runs: plain segments become text Runs (so the line
// still wraps at word boundaries) and link segments become an inline, underlined
// hot-text button wired to the segment's walk-to command. Bind SegmentsProperty
// in XAML; the handler rebuilds the inlines whenever the bound list changes.
public static class QuestStepInlines
{
    public static readonly AttachedProperty<IReadOnlyList<QuestStepSegmentViewModel>?> SegmentsProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IReadOnlyList<QuestStepSegmentViewModel>?>(
            "Segments", typeof(QuestStepInlines));

    public static void SetSegments(TextBlock target, IReadOnlyList<QuestStepSegmentViewModel>? value) =>
        target.SetValue(SegmentsProperty, value);

    public static IReadOnlyList<QuestStepSegmentViewModel>? GetSegments(TextBlock target) =>
        target.GetValue(SegmentsProperty);

    static QuestStepInlines() =>
        SegmentsProperty.Changed.AddClassHandler<TextBlock>(OnSegmentsChanged);

    private static void OnSegmentsChanged(TextBlock target, AvaloniaPropertyChangedEventArgs e)
    {
        var inlines = new InlineCollection();
        if (e.NewValue is IReadOnlyList<QuestStepSegmentViewModel> segments)
            foreach (QuestStepSegmentViewModel seg in segments)
            {
                if (!seg.IsLink) { inlines.Add(new Run(seg.Text)); continue; }

                // Walk links read cyan (MapLinkText); send-command links read green
                // (CmdLinkText) so a "walks me there" token is visually distinct from
                // a "types this at the game" one. Both share the flat MapLink chrome.
                var linkText = new TextBlock { Text = seg.Text };
                linkText.Classes.Add(seg.Kind == QuestStepLinkKind.Send ? "CmdLinkText" : "MapLinkText");
                inlines.Add(new InlineUIContainer(new Button
                {
                    Classes = { "MapLink" },
                    Command = seg.Command,
                    Content = linkText,
                }));
            }
        target.Inlines = inlines;
    }
}

using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using FujinTerm.Services;

namespace FujinTerm.Views;

// Attached behaviour that renders a conversation message into a TextBlock's inline
// runs with clickable web links: plain spans stay text Runs (so the line still
// wraps and the non-link text stays selectable) and http/https URLs become
// underlined hot-text that opens in the OS default browser via ShellLaunch.OpenUrl.
// Bind MessageProperty in XAML instead of Text; the handler rebuilds the inlines
// whenever the text changes. Mirrors the CharacterWorkshop QuestStepInlines pattern.
public static class ConversationMessageInlines
{
    public static readonly AttachedProperty<string?> MessageProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>(
            "Message", typeof(ConversationMessageInlines));

    public static void SetMessage(TextBlock target, string? value) =>
        target.SetValue(MessageProperty, value);

    public static string? GetMessage(TextBlock target) =>
        target.GetValue(MessageProperty);

    // http(s):// run of non-space characters. Trailing sentence punctuation is
    // trimmed off the match below so "see https://x.org." doesn't eat the period.
    private static readonly Regex s_url = new(
        @"https?://\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Trailing characters that are almost always sentence punctuation wrapping the
    // link rather than part of it. Peeled off the end and rendered as plain text.
    private const string TrailingTrim = ".,;:!?)]}>\"'";

    static ConversationMessageInlines() =>
        MessageProperty.Changed.AddClassHandler<TextBlock>(OnMessageChanged);

    private static void OnMessageChanged(TextBlock target, AvaloniaPropertyChangedEventArgs e)
    {
        var inlines = new InlineCollection();
        foreach ((string text, bool isLink) in Segment(e.NewValue as string ?? string.Empty))
            inlines.Add(isLink ? LinkInline(text) : new Run(text));
        target.Inlines = inlines;
    }

    // Split a message into ordered (text, isLink) segments: http/https URLs become
    // link segments, everything else (including punctuation peeled off a link's
    // tail) stays plain text. Pure + testable; the view handler just maps segments
    // to Runs / link inlines.
    internal static IReadOnlyList<(string Text, bool IsLink)> Segment(string text)
    {
        var segments = new List<(string, bool)>();
        if (string.IsNullOrEmpty(text)) return segments;

        int pos = 0;
        foreach (Match match in s_url.Matches(text))
        {
            if (match.Index > pos)
                segments.Add((text.Substring(pos, match.Index - pos), false));

            string url = match.Value;
            int keep = url.Length;
            while (keep > 0 && TrailingTrim.IndexOf(url[keep - 1]) >= 0) keep--;

            if (keep > 0) segments.Add((url.Substring(0, keep), true));
            if (keep < url.Length) segments.Add((url.Substring(keep), false));

            pos = match.Index + match.Length;
        }

        if (pos < text.Length)
            segments.Add((text.Substring(pos), false));

        return segments;
    }

    private static InlineUIContainer LinkInline(string url)
    {
        // Font is left to inherit from the host SelectableTextBlock so the link
        // tracks the user's message font size / family; only colour + underline
        // come from the ChatLinkText style.
        var button = new Button
        {
            Classes = { "ChatLink" },
            Content = new TextBlock { Text = url, Classes = { "ChatLinkText" } },
            VerticalAlignment = VerticalAlignment.Center,
            [ToolTip.TipProperty] = url,
        };
        button.Click += (_, _) => ShellLaunch.OpenUrl(url);
        return new InlineUIContainer(button);
    }
}

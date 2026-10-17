using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace MudPlay.Controls;

// The look of a search-box hit inside text, shared by the Help body and the open
// Settings section so a match reads the same in both windows: solid amber with dark
// text on it. A hit inside a coloured run (code, a field label) would otherwise sit
// coloured-on-amber and be hard to read; the run keeps its weight, slant and font.
// The softer amber that marks a whole matching topic, section or setting is the
// SearchMatch class in Themes/Controls.axaml.
public static class SearchHighlight
{
    private static readonly IBrush HitBackground = new SolidColorBrush(Color.Parse("#E0B040"));
    private static readonly IBrush HitForeground = new SolidColorBrush(Color.Parse("#141414"));

    public static void MarkHit(Run run)
    {
        run.Background = HitBackground;
        run.Foreground = HitForeground;
    }
}

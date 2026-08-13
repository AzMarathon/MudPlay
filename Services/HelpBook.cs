using System.Text;
using Avalonia.Platform;

namespace MudPlay.Services;

// Parses the bundled Help markdown (Assets/Help/guide.md) into a HelpTopic tree
// for the Help window: a leading '# ' title becomes an overview topic, each '## '
// heading a top-level section, and each '### ' its nested sub-topic. The text
// between a heading and the next becomes that topic's markdown body (rendered by
// HelpMarkdownInlines). Standalone horizontal rules ('---') are dropped as
// section separators that carry no content (markdown tables' '|---|' rows are
// kept — they aren't all-dashes).
//
// Parse is pure (string in, topics out) so it's testable without the Avalonia
// asset system; LoadBundled reads the embedded guide and never throws — a broken
// help asset must not crash the window open.
public static class HelpBook
{
    private const string GuideUri = "avares://MudPlay/Assets/Help/guide.md";

    public static IReadOnlyList<HelpTopic> LoadBundled()
    {
        try
        {
            using Stream s = AssetLoader.Open(new Uri(GuideUri));
            using StreamReader r = new(s);
            return Parse(r.ReadToEnd());
        }
        catch (Exception ex)
        {
            // Missing / unreadable asset → an empty book (the window shows no
            // topics rather than failing to open), but logged so a report of
            // "Help is empty" has a program-log trail instead of none. Use the
            // null-safe accessor: this runs at design time too (the previewer's
            // parameterless HelpWindowViewModel ctor), where AppServices isn't
            // up — LoadBundled must still never throw.
            AppServices.CurrentOrNull?.Log.Warn("Help", $"Failed to load bundled guide: {ex.Message}");
            return Array.Empty<HelpTopic>();
        }
    }

    public static IReadOnlyList<HelpTopic> Parse(string markdown)
    {
        List<Builder> roots = new();
        // Open ancestors, deepest last: a heading nests under the nearest open
        // heading of a shallower level, so '# ' → '## ' → '### ' forms a proper
        // tree of arbitrary depth (the guide groups every section under one '#').
        Stack<(int Level, Builder Node)> open = new();

        foreach (string line in (markdown ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            int level = HeadingLevel(line);
            if (level > 0)
            {
                Builder node = new(line[(level + 1)..].Trim());
                while (open.Count > 0 && open.Peek().Level >= level) open.Pop();
                if (open.Count == 0) roots.Add(node);
                else open.Peek().Node.Children.Add(node);
                open.Push((level, node));
            }
            else if (IsHorizontalRule(line))
            {
                // markdown '---' separator — visual only, no content.
            }
            else if (open.Count > 0)
            {
                open.Peek().Node.Body.Add(line);
            }
        }

        return roots.Select(b => b.Build()).ToList();
    }

    // Number of leading '#' when the line is an ATX heading ("## X"), else 0.
    private static int HeadingLevel(string line)
    {
        int h = 0;
        while (h < line.Length && line[h] == '#') h++;
        return h > 0 && h < line.Length && line[h] == ' ' ? h : 0;
    }

    private static bool IsHorizontalRule(string line)
    {
        string t = line.Trim();
        return t.Length >= 3 && t.All(c => c == '-');
    }

    private sealed class Builder
    {
        public string Title { get; }
        public List<string> Body { get; } = new();
        public List<Builder> Children { get; } = new();
        public Builder(string title) => Title = title;

        public HelpTopic Build() =>
            new(Title, JoinBody(Body), Children.Select(c => c.Build()).ToList());
    }

    // Trim leading / trailing blank lines but keep interior blanks (paragraph
    // breaks) and bullet structure intact.
    private static string JoinBody(List<string> lines)
    {
        int start = 0, end = lines.Count;
        while (start < end && lines[start].Trim().Length == 0) start++;
        while (end > start && lines[end - 1].Trim().Length == 0) end--;
        StringBuilder sb = new();
        for (int i = start; i < end; i++)
        {
            if (i > start) sb.Append('\n');
            sb.Append(lines[i]);
        }
        return sb.ToString();
    }
}

using System.Text;

namespace MudPlay.Services;

// Inline style of a run within a Help paragraph / bullet / table cell.
public enum HelpInlineStyle { Normal, Bold, Italic, Code }

// One styled run of text produced by HelpMarkup.ParseInline.
public sealed record HelpInline(string Text, HelpInlineStyle Style);

// Pure inline-markdown tokenizer for the Help content renderer: turns a single
// line of markdown into styled runs — **bold**, *italic*, `code`, and
// [text](link) (rendered as its text, since the guide's links are internal
// anchors). Kept separate from the Avalonia renderer so the parsing is testable.
// Unclosed markers are emitted literally rather than swallowing the rest of the
// line.
public static class HelpMarkup
{
    public static IReadOnlyList<HelpInline> ParseInline(string text)
    {
        List<HelpInline> segs = new();
        if (string.IsNullOrEmpty(text)) return segs;

        StringBuilder normal = new();
        void Flush()
        {
            if (normal.Length == 0) return;
            segs.Add(new HelpInline(normal.ToString(), HelpInlineStyle.Normal));
            normal.Clear();
        }

        int i = 0, n = text.Length;
        while (i < n)
        {
            char c = text[i];

            if (c == '`')
            {
                int close = text.IndexOf('`', i + 1);
                if (close > i)
                {
                    Flush();
                    segs.Add(new HelpInline(text[(i + 1)..close], HelpInlineStyle.Code));
                    i = close + 1;
                    continue;
                }
            }
            else if (c == '*' && i + 1 < n && text[i + 1] == '*')
            {
                int close = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (close > i)
                {
                    Flush();
                    segs.Add(new HelpInline(text[(i + 2)..close], HelpInlineStyle.Bold));
                    i = close + 2;
                    continue;
                }
            }
            else if (c == '*')
            {
                int close = text.IndexOf('*', i + 1);
                if (close > i)
                {
                    Flush();
                    segs.Add(new HelpInline(text[(i + 1)..close], HelpInlineStyle.Italic));
                    i = close + 1;
                    continue;
                }
            }
            else if (c == '[')
            {
                int rb = text.IndexOf(']', i + 1);
                if (rb > i && rb + 1 < n && text[rb + 1] == '(')
                {
                    int rp = text.IndexOf(')', rb + 2);
                    if (rp > rb)
                    {
                        normal.Append(text[(i + 1)..rb]);   // keep the link's visible text
                        i = rp + 1;
                        continue;
                    }
                }
            }

            normal.Append(c);
            i++;
        }

        Flush();
        return segs;
    }
}

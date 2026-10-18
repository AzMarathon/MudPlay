using System.Text;

namespace MudPlay.Game.Remote;

// Text bound for another player's screen by way of the game. The wire is 8-bit
// and the boards are CP437: a character outside ASCII either can't be encoded at
// all (a long dash becomes `?`) or arrives as a different glyph. Reply text is
// written in ASCII to begin with; this is the net under it, for the text a reply
// carries that the handler didn't write (a loop's name, a room's, a reason handed
// up from an engine).
public static class WireText
{
    // The same text with every character outside printable ASCII replaced: the
    // typographic ones by their plain spelling, anything else by `?`. Text that is
    // already ASCII comes back as the same string.
    public static string Ascii(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        int first = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (IsPlain(text[i])) continue;
            first = i;
            break;
        }
        if (first < 0) return text;

        StringBuilder sb = new(text.Length + 8);
        sb.Append(text, 0, first);
        for (int i = first; i < text.Length; i++)
        {
            char c = text[i];
            if (IsPlain(c)) sb.Append(c);
            else sb.Append(Spell(c));
        }
        return sb.ToString();
    }

    // A name a reply carries, the user's or one the sender typed: in plain
    // characters, and cut short past NameCap (its start, then "...") so that no
    // reply built round one name outgrows a telepath and no entry of a list
    // outgrows a part. A name is still found by the words cut off.
    private const int NameCap = 40;

    public static string Shortened(string text)
    {
        string plain = Ascii(text.Trim());
        return plain.Length <= NameCap ? plain : plain[..(NameCap - 3)].TrimEnd() + "...";
    }

    private static bool IsPlain(char c) => c is >= ' ' and <= '~';

    private static string Spell(char c) => c switch
    {
        '—' or '–' or '‒' or '‐' or '‑' or '−' => "-",   // dashes, minus
        '‘' or '’' or '‚' or '′' => "'",
        '“' or '”' or '„' or '″' => "\"",
        '…' => "...",
        '·' or '•' => ".",   // middle dot, bullet
        '≥' => ">=",
        '≤' => "<=",
        '×' => "x",
        '→' => "->",
        '←' => "<-",
        ' ' or '\t' => " ",
        _ => "?",
    };
}

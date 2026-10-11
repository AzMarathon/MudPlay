namespace MudPlay.Services;

// The "map/room" references in a cell of game-data text, such as a monster's
// Summoned By ("Group(lair): 1/547, Room 9/12"), read in place.
//
// A set's Monsters table carries a couple of hundred thousand of them, and every
// index built from that field walks them all, some inside the set switch. So the
// scan allocates nothing: no match object per reference and no string per number,
// only the two ints handed back.
internal static class RoomTokens
{
    // The next reference at or after position, which is left just past it. Finds what
    // the pattern (\d+)/(\d+) finds, in the same order: a whole run of digits, a
    // slash, a whole run of digits, none of it shared with the reference before.
    // map or room comes back 0 when its digits are not a number an int holds (too
    // long, or digits of another script, which the pattern matches and int.TryParse
    // refuses); every caller skips a reference whose map or room is not above 0.
    //
    // It goes from slash to slash rather than character by character: the search for
    // the slash is the runtime's own, and only the digits either side are read here.
    public static bool TryNext(ReadOnlySpan<char> text, ref int position, out int map, out int room)
    {
        int from = position;
        while (from < text.Length)
        {
            int found = text[from..].IndexOf('/');
            if (found < 0) break;
            int slash = from + found;
            from = slash + 1;
            // A digit on each side, the one before not already part of the last reference.
            if (slash <= position || !char.IsDigit(text[slash - 1])
                || from >= text.Length || !char.IsDigit(text[from]))
                continue;

            int start = slash - 1;
            while (start > position && char.IsDigit(text[start - 1])) start--;
            int end = from + 1;
            while (end < text.Length && char.IsDigit(text[end])) end++;
            map = Number(text[start..slash]);
            room = Number(text[from..end]);
            position = end;
            return true;
        }
        position = text.Length;
        map = 0;
        room = 0;
        return false;
    }

    // What int.TryParse makes of a run of digits, with 0 where it fails: it reads
    // 0 to 9 only, and nothing past int.MaxValue.
    private static int Number(ReadOnlySpan<char> digits)
    {
        long value = 0;
        foreach (char c in digits)
        {
            if (!char.IsAsciiDigit(c)) return 0;
            value = value * 10 + (c - '0');
            if (value > int.MaxValue) return 0;
        }
        return (int)value;
    }
}

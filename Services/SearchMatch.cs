namespace MudPlay.Services;

// The search-box matching the Help and Settings windows share, so the two boxes
// find the same things: the query is trimmed, a blank query matches nothing, and a
// hit is a case-insensitive occurrence of the whole query — several words are
// searched as one phrase, the way they read on screen, not as separate terms.
public static class SearchMatch
{
    public static bool IsBlank(string? query) => string.IsNullOrWhiteSpace(query);

    public static bool Matches(string? text, string? query) =>
        !IsBlank(query)
        && text is not null
        && text.Contains(query!.Trim(), StringComparison.OrdinalIgnoreCase);

    // Split text around every match, so a view can highlight what the search box
    // matched. A blank query, or one that isn't there, returns the whole text as a
    // single non-match piece.
    public static IReadOnlyList<(string Text, bool Match)> Split(string text, string? query)
    {
        List<(string, bool)> parts = new();
        if (string.IsNullOrEmpty(text)) return parts;
        string q = query?.Trim() ?? string.Empty;
        if (q.Length == 0) { parts.Add((text, false)); return parts; }

        int at = 0;
        while (at < text.Length)
        {
            int hit = text.IndexOf(q, at, StringComparison.OrdinalIgnoreCase);
            if (hit < 0) { parts.Add((text[at..], false)); break; }
            if (hit > at) parts.Add((text[at..hit], false));
            parts.Add((text.Substring(hit, q.Length), true));
            at = hit + q.Length;
        }
        return parts;
    }
}

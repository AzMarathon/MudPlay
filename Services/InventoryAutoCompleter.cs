using MudPlay.Game.Inventory;

namespace MudPlay.Services;

// Tab-completion cursor over the player's carried/worn/key-ring item names, in
// the style of CommandHistoryNavigator: it caches the in-progress completion
// cycle (the stem/tail around the completed word, the ordered candidate list,
// and the current position) so repeated Tab / Shift+Tab presses step through
// matches instead of re-scanning the inventory each time. Each input widget
// (the terminal, the Conversation window) owns its own instance, the same way
// CommandHistoryNavigator is one-per-widget over the one shared CommandHistory.
//
// Caret-aware: completion always targets the word(s) immediately BEFORE the
// caret and preserves whatever text follows it untouched. The terminal's
// LocalInputBuffer has no mid-line cursor (append/backspace-from-the-end
// only), so it always calls in with caretIndex == text.Length, which
// collapses to "complete the trailing words" — but the Conversation window's
// TextBox supports moving the cursor mid-line like any normal text box, and
// completing based on the trailing word regardless of caret position would
// silently edit the wrong word there.
//
// Takes an InventorySnapshot per call rather than holding the InventoryManager
// itself — InventoryManager is replaced wholesale on a character/profile swap
// (see AppServices.Inventory), so caching a reference to it here would risk
// completing against a torn-down character's pack. Callers fetch the live
// snapshot fresh on every keypress instead.
//
// A Tab press does a handful of string splits over a small inventory — there is
// no caching or background work because none is needed, and this deliberately
// stays off the per-keystroke path (only Tab/Shift+Tab call in), so normal
// typing can never see added latency from it.
public sealed class InventoryAutoCompleter
{
    // A produced completion: the full replacement text, and where the caret
    // should land afterward (immediately after the inserted word, before
    // whatever text followed the original caret).
    public readonly record struct Completion(string Text, int CaretIndex);

    // The completion this instance last produced, so a repeat Tab/Shift+Tab
    // press on an UNCHANGED (text, caret) pair is recognised as "keep cycling"
    // rather than a fresh match. Any other edit (typing, backspace, Enter,
    // history recall, moving the caret) changes one or the other, which this
    // class detects on its own — no explicit reset call is needed from the caller.
    private Completion? _lastProduced;
    private string _stem = "";
    private string _tail = "";
    private IReadOnlyList<string> _candidates = System.Array.Empty<string>();
    private int _index;

    // Step to the next match (Tab).
    public Completion? Next(string text, int caretIndex, InventorySnapshot snapshot)
        => Step(text, caretIndex, snapshot, +1);

    // Step to the previous match (Shift+Tab).
    public Completion? Previous(string text, int caretIndex, InventorySnapshot snapshot)
        => Step(text, caretIndex, snapshot, -1);

    private Completion? Step(string text, int caretIndex, InventorySnapshot snapshot, int direction)
    {
        // Callers source the text and the caret from two different objects (the
        // Conversation window passes the view-model's InputText alongside the
        // TextBox's own CaretIndex), so they can momentarily disagree. Clamp
        // rather than letting a caret past the end throw out of LastIndexOf.
        caretIndex = Math.Clamp(caretIndex, 0, text.Length);

        if (_lastProduced is { } last && text == last.Text && caretIndex == last.CaretIndex
            && _candidates.Count > 0)
        {
            _index = ((_index + direction) % _candidates.Count + _candidates.Count) % _candidates.Count;
            return Produce(_candidates[_index]);
        }

        // Nothing to complete when the caret is at the start or right after a
        // space: an empty word would match every item.
        if (caretIndex == 0 || text[caretIndex - 1] == ' ')
        {
            _lastProduced = null;
            return null;
        }

        // Try the longest run of words ending at the caret first, then shorter
        // ones down to the single trailing word. Item names are multi-word, so
        // "padded h" has to be matched as a whole against "padded helm" — looking
        // at "h" alone would never find it, since no item LEADS with "h". The
        // single-word run is the fallback, which is what lets "drop holy" still
        // resolve "holy" once "drop holy" matches nothing.
        for (int start = 0; start < caretIndex; start++)
        {
            bool wordStart = text[start] != ' ' && (start == 0 || text[start - 1] == ' ');
            if (!wordStart) continue;

            IReadOnlyList<string> candidates = MatchingCompletions(text[start..caretIndex], snapshot);
            if (candidates.Count == 0) continue;

            _stem = text[..start];
            _tail = text[caretIndex..];
            _candidates = candidates;
            _index = 0;
            return Produce(candidates[0]);
        }

        _lastProduced = null;
        return null;
    }

    private Completion Produce(string candidate)
    {
        Completion c = new(_stem + candidate + _tail, _stem.Length + candidate.Length);
        _lastProduced = c;
        return c;
    }

    // For every carried, worn, and key-ring item, check whether what's typed is
    // a prefix of its name — starting at the name's first meaningful word, i.e.
    // after a leading stack count ("43 black diamond") and a leading "a"/"an"
    // (case-insensitive — the same convention InventorySnapshot.Has uses) — and
    // if so complete to that whole name. The count and article are not part of
    // what you'd type after a verb, and skipping only the article used to leave
    // every stacked item unmatchable: its leading "word" was the number.
    // Matching any word ANYWHERE in the name used to mean typing "e" surfaced
    // "bronze emblem" (via its second word "emblem") ahead of items that
    // actually start with "e" like "emerald-tipped crozier" — surprising and
    // not what a short prefix is trying to narrow down to. Reaching "bronze
    // emblem" takes typing "bro" or "bronze", its real leading word.
    //
    // Completing "drop holy" against "holy medallion" must still produce
    // "drop holy medallion", not "drop holy" — the matched word is already
    // what's typed, so completing to itself would silently leave the line
    // unchanged and look like Tab did nothing. Distinct completions,
    // first-seen order.
    private static IReadOnlyList<string> MatchingCompletions(string typed, InventorySnapshot snapshot)
    {
        List<string> matches = new();

        void Scan(string entry)
        {
            string[] words = CountedCommand.SplitLeadingCount(entry).Name
                .Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return;
            int start = words.Length > 1 && IsArticle(words[0]) ? 1 : 0;
            string completion = string.Join(' ', words, start, words.Length - start);
            if (!completion.StartsWith(typed, System.StringComparison.OrdinalIgnoreCase)) return;
            if (!matches.Contains(completion, System.StringComparer.OrdinalIgnoreCase))
                matches.Add(completion);
        }

        foreach (string item in snapshot.CarriedItems) Scan(item);
        foreach (EquippedItem item in snapshot.EquippedItems) Scan(item.Name);
        if (snapshot.Keys is { } keys)
            foreach (string key in keys) Scan(key);

        return matches;
    }

    private static bool IsArticle(string word)
        => word.Equals("a", System.StringComparison.OrdinalIgnoreCase)
        || word.Equals("an", System.StringComparison.OrdinalIgnoreCase);
}

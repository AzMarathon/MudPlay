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
// Caret-aware: completion always targets the word immediately BEFORE the
// caret and preserves whatever text follows it untouched. The terminal's
// LocalInputBuffer has no mid-line cursor (append/backspace-from-the-end
// only), so it always calls in with caretIndex == text.Length, which
// collapses to "complete the trailing word" — but the Conversation window's
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
        if (_lastProduced is { } last && text == last.Text && caretIndex == last.CaretIndex
            && _candidates.Count > 0)
        {
            _index = ((_index + direction) % _candidates.Count + _candidates.Count) % _candidates.Count;
            return Produce(_candidates[_index]);
        }

        // The word immediately before the caret — search backward from just
        // before it for the nearest space, ignoring anything past the caret.
        int searchEnd = caretIndex - 1;
        int sep = searchEnd >= 0 ? text.LastIndexOf(' ', searchEnd) : -1;
        string prefix = text[(sep + 1)..caretIndex];
        if (prefix.Length == 0)
        {
            _lastProduced = null;
            return null;
        }

        IReadOnlyList<string> candidates = MatchingCompletions(prefix, snapshot);
        if (candidates.Count == 0)
        {
            _lastProduced = null;
            return null;
        }

        _stem = text[..(sep + 1)];
        _tail = text[caretIndex..];
        _candidates = candidates;
        _index = 0;
        return Produce(candidates[0]);
    }

    private Completion Produce(string candidate)
    {
        Completion c = new(_stem + candidate + _tail, _stem.Length + candidate.Length);
        _lastProduced = c;
        return c;
    }

    // For every carried, worn, and key-ring item name, find each word that
    // starts with prefix (case-insensitive — the same convention
    // InventorySnapshot.Has uses) and take that word PLUS every word after it
    // in the same name, not just the matched word alone. Completing "drop
    // holy" against "holy medallion" must produce "drop holy medallion", not
    // "drop holy" — the matched word is already what's typed, so completing
    // to itself would silently leave the line unchanged and look like Tab did
    // nothing (the bug this fixes). Distinct completions, first-seen order.
    private static IReadOnlyList<string> MatchingCompletions(string prefix, InventorySnapshot snapshot)
    {
        List<string> matches = new();

        void Scan(string name)
        {
            string[] words = name.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < words.Length; i++)
            {
                if (!words[i].StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase)) continue;
                string completion = string.Join(' ', words, i, words.Length - i);
                if (!matches.Contains(completion, System.StringComparer.OrdinalIgnoreCase))
                    matches.Add(completion);
            }
        }

        foreach (string item in snapshot.CarriedItems) Scan(item);
        foreach (EquippedItem item in snapshot.EquippedItems) Scan(item.Name);
        if (snapshot.Keys is { } keys)
            foreach (string key in keys) Scan(key);

        return matches;
    }
}

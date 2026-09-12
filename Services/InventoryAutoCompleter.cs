using MudPlay.Game.Inventory;

namespace MudPlay.Services;

// Tab-completion cursor over the player's carried/worn/key-ring item names, in
// the style of CommandHistoryNavigator: it caches the in-progress completion
// cycle (the stem before the completed word, the ordered candidate list, and
// the current position) so repeated Tab / Shift+Tab presses step through
// matches instead of re-scanning the inventory each time.
//
// Takes an InventorySnapshot per call rather than holding the InventoryManager
// itself — InventoryManager is replaced wholesale on a character/profile swap
// (see AppServices.Inventory), so caching a reference to it here would risk
// completing against a torn-down character's pack. The caller (TerminalControl)
// fetches the live snapshot fresh on every keypress instead.
//
// A Tab press does a handful of string splits over a small inventory — there is
// no caching or background work because none is needed, and this deliberately
// stays off the per-keystroke path (only Tab/Shift+Tab call in), so normal
// typing can never see added latency from it.
public sealed class InventoryAutoCompleter
{
    // Master enable, mirroring the "Tab-complete inventory item names" Settings
    // -> General toggle. When off, Next/Previous always return null so Tab
    // keeps its old straight-to-wire behaviour.
    public bool Enabled { get; set; } = true;

    // The line text this completer last produced, so a repeat Tab/Shift+Tab
    // press on an UNCHANGED line is recognised as "keep cycling" rather than a
    // fresh match. Any other edit (typing, backspace, Enter, history recall)
    // changes the line text, which this class detects on its own — no explicit
    // reset call is needed from the caller.
    private string? _lastProduced;
    private string _stem = "";
    private IReadOnlyList<string> _candidates = System.Array.Empty<string>();
    private int _index;

    // Step to the next match (Tab).
    public string? Next(string currentLine, InventorySnapshot snapshot) => Step(currentLine, snapshot, +1);

    // Step to the previous match (Shift+Tab).
    public string? Previous(string currentLine, InventorySnapshot snapshot) => Step(currentLine, snapshot, -1);

    private string? Step(string currentLine, InventorySnapshot snapshot, int direction)
    {
        if (!Enabled) return null;

        if (_lastProduced is not null && currentLine == _lastProduced && _candidates.Count > 0)
        {
            _index = ((_index + direction) % _candidates.Count + _candidates.Count) % _candidates.Count;
            return _lastProduced = _stem + _candidates[_index];
        }

        int sep = currentLine.LastIndexOf(' ');
        string prefix = sep >= 0 ? currentLine[(sep + 1)..] : currentLine;
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

        _stem = sep >= 0 ? currentLine[..(sep + 1)] : "";
        _candidates = candidates;
        _index = 0;
        return _lastProduced = _stem + candidates[0];
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

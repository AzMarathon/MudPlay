using MudPlay.Game.Cash;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Inventory;

// Tracks the items lying on the current room's floor, parsed from the
// "You notice <list> here." survey line. Cash entries are filtered out (coin is
// the CashManager's domain), so the snapshot is item-only — the single source
// both @what (read the list back) and @get-all (send get per item) consume.
//
// The snapshot is per-room. A fresh survey rebuilds it wholesale (a new "You
// notice" supersedes the prior list), and a room change clears it via
// OnRoomChanged — the loot belonged to the room we left, and an empty room emits
// no survey line, so without the clear a stale list would linger. Item wording
// is preserved verbatim ("a rusty dagger"), so get can match on the noun phrase
// after the caller strips the article.
//
// There is no bulk "get all" verb in MajorMUD — @get-all walks this list and
// sends one get per entry. Cash recognition is stricter than the CashManager's
// collect rule (see IsCashEntry): a denomination word used as a material
// adjective ("a silver ring") must stay an item, so only a counted coin or the
// canonical "a <denom> piece" singular is filtered.
public sealed class GroundItemTracker : IDisposable
{
    // The four stable single-word denominations; the fifth (runic) is
    // recognised via _naming because a board can rename the runic word.
    private static readonly HashSet<string> StableDenominations =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "copper", "silver", "gold", "platinum",
        };

    private readonly IDisposable _noticeSub;
    private readonly List<string> _items = new();
    private readonly CurrencyNaming _naming;
    private readonly Func<string, bool>? _isKnownItem;

    private Terminal.LineExtractor? _lines;
    private string? _noticeBuffer;            // multi-line continuation
    private bool _disposed;

    // isKnownItem resolves a survey entry against the active item table (true
    // when it names a real Items.json record). Injected so the cash filter can
    // settle the "2 gold key" ambiguity below; null when no game data is wired
    // (tests), where the count+denomination heuristic stands alone.
    public GroundItemTracker(MessageRouter router, CurrencyNaming naming,
        Func<string, bool>? isKnownItem = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(naming);
        _naming = naming;
        _isKnownItem = isKnownItem;
        _noticeSub = router.Subscribe(KnownPatterns.YouNoticeRoom, OnYouNoticeRoom);
    }

    // Item names on the room floor from the latest survey, cash excluded,
    // wording preserved for a get match. Empty until a "You notice" line lands;
    // cleared on room change.
    public IReadOnlyList<string> Items => _items;

    // Fires after a "You notice" survey rebuilds Items — the floor list is now
    // current. A get-all that found an empty cache can re-survey and grab on this.
    public event Action? SurveyUpdated;

    // Bind the per-session LineExtractor so the tracker can stitch a wrapped
    // "You notice" survey back together — same shape as AutoGetItemsManager /
    // the CashManager.
    public void AttachLineExtractor(Terminal.LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (ReferenceEquals(_lines, lines)) return;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    // Discard the snapshot on an actual room change — the floor loot belonged to
    // the room we just left.
    public void OnRoomChanged()
    {
        _noticeBuffer = null;
        _items.Clear();
    }

    // ----- notice parsing ----------------------------------------------

    // Single-line "You notice <list> here." — the pattern subscription path.
    // Multi-line wraps stitch through OnLine and feed the same rebuild.
    private void OnYouNoticeRoom(MatchResult m)
    {
        if (m.Groups.Count == 0) return;
        RebuildFrom(m.Groups[0]);
    }

    // Multi-line stitch mirrors CashManager / AutoGetItemsManager — a wrapped
    // survey arrives as two emitted lines, so we buffer from the "You notice "
    // row until a row ends with '.'. Single-line surveys are skipped here (the
    // pattern subscription handles them) to avoid double-processing.
    private void OnLine(Terminal.LineExtractor.EmittedLine line)
    {
        if (line.IsPromptLine) return;
        string text = line.Text.TrimEnd();
        if (text.Length == 0) return;

        if (_noticeBuffer is not null)
        {
            _noticeBuffer = _noticeBuffer + " " + text;
            if (text.EndsWith('.'))
            {
                string complete = _noticeBuffer;
                _noticeBuffer = null;
                ProcessMultiLine(complete);
            }
            return;
        }

        if (text.StartsWith("You notice ", StringComparison.Ordinal)
            && !text.EndsWith('.'))
        {
            _noticeBuffer = text;
        }
    }

    private void ProcessMultiLine(string completeLine)
    {
        const string prefix = "You notice ";
        if (!completeLine.StartsWith(prefix, StringComparison.Ordinal)) return;
        string body = completeLine[prefix.Length..].TrimEnd();
        const string suffix = " here.";
        if (body.EndsWith(suffix, StringComparison.Ordinal))
            body = body[..^suffix.Length];
        else if (body.EndsWith('.'))
            body = body[..^1];
        RebuildFrom(body);
    }

    // Rebuild the snapshot from a survey list — split into entries, drop cash,
    // keep item wording verbatim.
    private void RebuildFrom(string list)
    {
        _items.Clear();
        foreach (string entry in SplitEntries(list))
        {
            if (IsCashEntry(entry)) continue;
            _items.Add(entry);
        }
        SurveyUpdated?.Invoke();
    }

    // Split "a, b and c" survey wording into individual entries — commas
    // separate all but the final pair, which uses " and ".
    private static IEnumerable<string> SplitEntries(string list)
    {
        foreach (string comma in list.Split(',', StringSplitOptions.TrimEntries
                                              | StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string piece in comma.Split(" and ",
                         StringSplitOptions.TrimEntries
                         | StringSplitOptions.RemoveEmptyEntries))
            {
                if (piece.Length > 0) yield return piece;
            }
        }
    }

    // Cash entries in the survey are either a counted coin ("50 gold crowns",
    // "56 silver nobles") or the singular "a gold piece". Cash always carries its
    // count + denomination; an item shows just its name when a lone copy is on the
    // floor and gains a leading count only when 2+ are stacked ("5 piece of
    // amber"). So a leading count no longer implies cash — both forms are gated on
    // the word right after the count: cash names a denomination there
    // (copper/silver/gold/platinum/runic), a stacked item names its own noun
    // ("piece", "torch"), so "5 piece of amber" stays an item. The singular
    // "a <denom> ..." form is tighter still — cash only when it ends in the coin
    // noun "piece(s)", leaving material-adjective items ("a silver ring", "a
    // copper key") intact.
    //
    // The count+denomination heuristic can't tell a stacked item whose name
    // *starts* with a denomination word ("2 gold key") from a coin pile ("2 gold
    // crowns") on its own — both read as "N <denom> ...". The item-table
    // tiebreaker settles it (shared with CashManager.TryParseCashEntry, the same
    // heuristic + the same fix): an entry that resolves to a real Items.json
    // record is never cash. Currency isn't in the item table, so a genuine coin
    // pile won't resolve and still reads as cash. Unwired (tests without game
    // data) the heuristic stands alone and the "2 gold key" ambiguity resurfaces.
    private bool IsCashEntry(string entry)
    {
        string[] words = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return false;

        // Authoritative item-table override — a known item is never cash.
        if (_isKnownItem is not null && _isKnownItem(entry)) return false;

        if (int.TryParse(words[0], out _))
            return IsCashWord(words[1]);

        if ((string.Equals(words[0], "a", StringComparison.OrdinalIgnoreCase)
             || string.Equals(words[0], "an", StringComparison.OrdinalIgnoreCase))
            && IsCashWord(words[1]))
        {
            return string.Equals(words[^1], "piece", StringComparison.OrdinalIgnoreCase)
                || string.Equals(words[^1], "pieces", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    // True when word names any cash denomination — one of the stable four or
    // the active board's runic word (stock "runic" included).
    private bool IsCashWord(string word) =>
        StableDenominations.Contains(word) || _naming.IsRunic(word);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _noticeSub.Dispose();
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = null;
    }
}

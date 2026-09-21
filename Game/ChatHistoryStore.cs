using System.Collections.ObjectModel;

namespace MudPlay.Game;

// App-singleton chat / realm-event history. Subscribes to
// ChatRouter.EntryClassified and appends every classified entry into Entries
// for the ConversationWindow to bind to. Wall-clock date rollovers insert a
// synthetic DaySeparator entry so multi-day sessions show a visible break (the
// typical case — the app runs for hours, the user keeps it open across
// midnight).
//
// Lifetime: the instance is app-scoped, but its CONTENTS are re-scoped to the
// loaded character. SessionLogService clears + re-seeds it from the new
// character's talk.log on every profile change, so the Conversation window shows
// only the loaded character's chat — a PVE and a PVP character on the same BBS
// don't share the view. A connect / disconnect / reconnect of the SAME character
// keeps the contents. Also cleared on the window's right-click Clear, and on app exit.
//
// This store is the in-memory view the Conversation window binds to. Durable
// disk persistence lives in Services.SessionLogService, which subscribes to the
// same ChatRouter and rolls a per-character <char>.<bbs>.talk.log.
public sealed class ChatHistoryStore : IDisposable
{
    // Upper bound on retained entries. The store is in-memory and app-lifetime,
    // so an all-day session would otherwise grow the collection without limit;
    // past this many the oldest rows drop off the front. Generous enough that
    // the Conversation window's scrollback never feels truncated in practice.
    private const int MaxEntries = 5_000;

    private readonly ChatRouter _router;
    private readonly ObservableCollection<ChatLogEntry> _entries = new();
    private DateOnly _lastDate;
    private bool _disposed;

    // Read-only view for the Conversation window's binding.
    public ReadOnlyObservableCollection<ChatLogEntry> Entries { get; }

    public ChatHistoryStore(ChatRouter router)
    {
        ArgumentNullException.ThrowIfNull(router);
        _router = router;
        Entries = new ReadOnlyObservableCollection<ChatLogEntry>(_entries);
        _router.EntryClassified += OnEntryClassified;
    }

    private void OnEntryClassified(ChatLogEntry entry)
    {
        DateOnly entryDate = DateOnly.FromDateTime(entry.Timestamp.LocalDateTime);

        // First entry of the session anchors _lastDate without emitting a
        // separator — only rollovers within a live session deserve one.
        if (_lastDate == default)
        {
            _lastDate = entryDate;
        }
        else if (entryDate != _lastDate)
        {
            _entries.Add(new ChatLogEntry(
                entry.Timestamp,
                ChatChannel.DaySeparator,
                Speaker: null,
                Message: entryDate.ToString("yyyy-MM-dd"),
                RawText: string.Empty));
            _lastDate = entryDate;
        }

        _entries.Add(entry);

        // Bounded ring: shed the oldest rows once past the cap. Chat arrives
        // at human speech rates, so the O(n) front-removal is off any hot path.
        while (_entries.Count > MaxEntries)
            _entries.RemoveAt(0);
    }

    // Replay persisted history into the store so the Conversation window shows
    // prior-session chat. Entries are inserted at the front (they predate anything
    // live) in their given chronological order. Callers Clear() before re-seeding
    // on a profile change (SessionLogService does this per character), so seeding
    // into a non-empty store is not expected and would prepend duplicates.
    public void Seed(IReadOnlyList<ChatLogEntry> historical)
    {
        ArgumentNullException.ThrowIfNull(historical);
        if (historical.Count == 0) return;

        bool wasEmpty = _entries.Count == 0;
        for (int i = 0; i < historical.Count; i++)
            _entries.Insert(i, historical[i]);
        while (_entries.Count > MaxEntries)
            _entries.RemoveAt(_entries.Count - 1);

        // Anchor the rollover clock to the newest seeded row so the first live
        // entry only draws a separator when it genuinely crosses into a new day.
        if (wasEmpty)
            _lastDate = DateOnly.FromDateTime(historical[^1].Timestamp.LocalDateTime);
    }

    // Wipe every entry. Called on a profile change (SessionLogService re-scopes the
    // view to the new character), and by the Conversation window's right-click →
    // Clear menu. Raises a Reset so the ConversationViewModel rebuilds empty.
    public void Clear()
    {
        _entries.Clear();
        _lastDate = default;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _router.EntryClassified -= OnEntryClassified;
    }
}

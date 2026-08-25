using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Inventory;
using MudPlay.Game.Remote;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Per-BBS "last seen this item in this room" log for Roomba Mode, persisted to
// Data/BBS/{bbs}/roomba_items.json. Fed by GhSweepManager every time it merges
// a fresh floor survey into a labeled gang-house room (recon or the post-sort
// final recon pass); read by RoombaQueryHandler to answer @roomba <item>.
// BBS-scoped for the same reason GhRoomLabelStore is — one shared gang house
// per board, so a sighting recorded by any character is visible to every other
// character on that BBS. Mirrors RoomBlacklistStore's per-BBS load/persist
// shape (OnBbsPinApplied + a Changed event).
public sealed class GhItemLocationStore
{
    private readonly ItemNameStore _itemNames;
    private readonly LogService? _log;
    private string? _activeBbs;
    // Keyed by the canonical (GhSurveyMerger.Canonical) item name — the same
    // normalization GhSweepManager's room-observation ledger uses, so a
    // recorded sighting and a later @roomba query of any wording of the same
    // item resolve to one entry.
    private readonly Dictionary<string, GhItemSighting> _sightings =
        new(StringComparer.OrdinalIgnoreCase);

    // Fires after every mutation, including a BBS-pin reload.
    public event Action? Changed;

    public GhItemLocationStore(ItemNameStore itemNames, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(itemNames);
        _itemNames = itemNames;
        _log = log;
    }

    // Read-only snapshot of every item currently on record, most recent first.
    public IReadOnlyList<GhItemSighting> Sightings
        => _sightings.Values.OrderByDescending(s => s.SeenAt).ToList();

    // Record every item on `room`'s currently-known floor as last-seen there,
    // now. Called by GhSweepManager right after it merges a fresh survey into
    // its own _observedByRoom ledger for `room` — `items` is that room's full
    // accumulated floor list (count-prefixed entries allowed; the count is
    // stripped, only identity + location matter here). A room re-observed
    // later (recon's next lap, or final recon after sorting) simply overwrites
    // the same entries with a fresher timestamp — no explicit "item moved"
    // tracking needed.
    public void RecordRoom(RoomKey room, IReadOnlyList<string> items)
    {
        if (_activeBbs is null || items.Count == 0) return;

        DateTimeOffset now = DateTimeOffset.Now;
        foreach (string entry in items)
        {
            (int count, _) = CountedCommand.SplitLeadingCount(entry);
            string canonical = GhSurveyMerger.Canonical(entry, _itemNames);
            _sightings[canonical] = new GhItemSighting
            {
                ItemName = canonical,
                Map = room.Map,
                Room = room.Room,
                SeenAt = now,
                Quantity = Math.Max(1, count),
            };
        }
        Persist();
        Changed?.Invoke();
    }

    // Resolve query (a player-typed item name, possibly partial or differently
    // worded) to its last-known sighting. Preference order: (1) canonical
    // item-id match via ItemNameStore, so any wording that resolves to the same
    // game-data item hits regardless of how it was recorded; (2) exact
    // case-insensitive name match; (3) a substring match, but only when it's
    // unique — an ambiguous partial (matches more than one tracked item) reports
    // not-found rather than guessing which one the sender meant.
    public bool TryFindLastSeen(string query, out GhItemSighting sighting)
    {
        sighting = null!;
        if (string.IsNullOrWhiteSpace(query)) return false;

        if (_itemNames.FindByName(query) is int number)
        {
            string canonical = _itemNames.GetName(number) ?? query;
            if (_sightings.TryGetValue(canonical, out GhItemSighting? byId))
            {
                sighting = byId;
                return true;
            }
        }

        if (_sightings.TryGetValue(query, out GhItemSighting? exact))
        {
            sighting = exact;
            return true;
        }

        List<GhItemSighting> partial = _sightings.Values
            .Where(s => s.ItemName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (partial.Count == 1)
        {
            sighting = partial[0];
            return true;
        }
        return false;
    }

    // This client's sightings encoded for the @roomba sync wire — only the ones
    // whose canonical name resolves back to an item record number (the sync
    // format has no name fallback; an unresolvable name can't happen for
    // anything Roomba itself recorded, since GhSurveyMerger.Canonical only
    // returns a game-data name when ItemNameStore can already resolve it back).
    public IReadOnlyList<GhItemSyncRecord> ToSyncRecords()
    {
        List<GhItemSyncRecord> records = new();
        foreach (GhItemSighting s in _sightings.Values)
        {
            if (_itemNames.FindByName(s.ItemName) is not int number) continue;
            records.Add(new GhItemSyncRecord(s.Map, s.Room, number, s.Quantity, s.SeenAt));
        }
        return records;
    }

    // Merge sightings received from another MudPlay client's @roomba sync
    // reply. Newest SeenAt per item wins — unlike a boss kill time there's no
    // meaningful "conflict" for a room-contents sighting, so this never needs a
    // user-facing merge decision, just silent adoption of whichever side saw it
    // more recently. Records naming an item number outside our active game-data
    // set are skipped (nothing to resolve a name from). Returns the count
    // actually applied, for the program log.
    public int MergeSyncRecords(IReadOnlyList<GhItemSyncRecord> records)
    {
        if (_activeBbs is null) return 0;
        int applied = 0;
        foreach (GhItemSyncRecord r in records)
        {
            string? name = _itemNames.GetName(r.ItemNumber);
            if (name is null) continue;
            if (_sightings.TryGetValue(name, out GhItemSighting? existing) && existing.SeenAt >= r.SeenAt) continue;

            _sightings[name] = new GhItemSighting
            {
                ItemName = name,
                Map = r.Map,
                Room = r.Room,
                Quantity = r.Quantity,
                SeenAt = r.SeenAt,
            };
            applied++;
        }
        if (applied > 0) { Persist(); Changed?.Invoke(); }
        return applied;
    }

    // Load the item-sighting log for the active BBS. Called by AppServices on
    // ProfileService.ProfileLoaded / BbsPinApplied with the resolved active BBS
    // name; resets the in-memory store when the pin clears (bbs is null / blank).
    public void OnBbsPinApplied(string? bbs)
    {
        if (string.IsNullOrWhiteSpace(bbs))
        {
            if (_activeBbs is not null)
            {
                _activeBbs = null;
                _sightings.Clear();
                Changed?.Invoke();
            }
            return;
        }

        _activeBbs = bbs;
        _sightings.Clear();
        List<GhItemSighting>? loaded = JsonStore.Load<List<GhItemSighting>>(AppPaths.BbsRoombaItemsFile(bbs));
        if (loaded is not null)
            foreach (GhItemSighting s in loaded) _sightings[s.ItemName] = s;
        Changed?.Invoke();
    }

    private void Persist()
    {
        if (_activeBbs is null) return;
        JsonStore.Save(AppPaths.BbsRoombaItemsFile(_activeBbs), _sightings.Values.ToList());
    }
}

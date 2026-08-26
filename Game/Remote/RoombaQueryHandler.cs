using System;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// Read-only handler for @roomba — reports every gang-house room a named item
// is currently tracked in, per GhItemLocationStore's BBS-tier sighting log,
// gated by the QueryItemLocation permission.
//   @roomba <item name>   — one reply line PER DISTINCT MATCHING ITEM: total
//                           quantity summed across every room currently
//                           holding it, and the room locators — or "no
//                           record". A loose query can match a whole family of
//                           similarly-named items ("severed" / "head" both
//                           match "severed head of goru-nezar" AND "severed
//                           head of darksong"); FindSightings returns every
//                           match rather than refusing on ambiguity, so this
//                           groups by item and reports each (capped, with an
//                           overflow tail) instead of silently saying nothing
//                           just because more than one name matched. A query
//                           that resolves to exactly one item — the common
//                           case — is just one line, same as before.
//   @roomba sync          — replies with this client's entire sighting log,
//                           compactly encoded (see GhItemSyncCodec), for the
//                           requester's RoombaSyncReceiver to merge in. Same
//                           permission as the rest of @roomba — mirrors
//                           BossTimerQueryHandler's `@timer sync`.
// Silent (no reply at all, not even a denial) when GhRoomLabelStore's
// ResponsesEnabled toggle is off — the feature is opt-in per BBS, and staying
// silent while off avoids advertising the command to a gang that hasn't turned
// it on.
public sealed class RoombaQueryHandler : IDisposable
{
    // Cap on distinct-item lines a single @roomba <item> reply sends, so a
    // very loose query (matching a whole family of items) can't flood the
    // channel; the overflow folds into one final "+N more" line.
    private const int MaxItemsShown = 5;

    // Cap on room locators shown within one item's reply line so an item
    // scattered across a large gang house can't blow that line out to an
    // unreadable length; the overflow folds into a "+N more" tail instead of
    // extra lines.
    private const int MaxRoomsShown = 10;

    // Cap on records per @roomba sync response so a very large gang-house
    // sighting log can't flood the channel; per-line character budget for each
    // self-contained blob keeps the wrapped wire line ("bg {@roombadata <blob>}")
    // under the realm's 245-char telepath limit with margin for the wrapper and
    // the send-command prefix. Bigger lines = fewer telepaths per sync, which is
    // what keeps a freshly-swept house from tripping the game's burst flood guard.
    private const int MaxSyncRecords = 500;
    private const int MaxBlobCharsPerLine = 200;

    // The chat token a sync RESPONSE rides on. Registered ignored in AppServices
    // (via this const, not an inline literal) so the remote engine swallows it
    // instead of bouncing "{command invalid}"; RoombaSyncReceiver scrapes it on
    // its own ChatRouter subscription.
    public const string SyncResponseToken = "@roombadata";

    private readonly RemoteCommandManager _engine;
    private readonly GhItemLocationStore _locations;
    private readonly GhRoomLabelStore _labels;
    private readonly LogService? _log;
    private bool _disposed;

    public RoombaQueryHandler(RemoteCommandManager engine, GhItemLocationStore locations, GhRoomLabelStore labels, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(labels);
        _engine = engine;
        _locations = locations;
        _labels = labels;
        _log = log;

        if (!RemoteCommandCatalog.TryGetCategory("@roomba", out PlayerRemoteControls category))
            throw new InvalidOperationException(
                "RemoteCommandCatalog missing entry for '@roomba'. Add it to the Map before registering.");
        _engine.RegisterHandler("@roomba", category, OnRoomba);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.UnregisterHandler("@roomba");
    }

    private void OnRoomba(RemoteCommandContext ctx)
    {
        if (!_labels.ResponsesEnabled) return;

        if (ctx.Args.Count > 0 && ctx.Args[0].Equals("sync", StringComparison.OrdinalIgnoreCase))
        {
            OnSyncRequest(ctx);
            return;
        }

        string query = string.Join(' ', ctx.Args).Trim();
        if (query.Length == 0) { ctx.Reply("usage: @roomba <item name>"); return; }

        IReadOnlyList<GhItemSighting> sightings = _locations.FindSightings(query);
        if (sightings.Count == 0)
        {
            ctx.Reply($"no record of \"{query}\"");
            return;
        }

        List<IGrouping<string, GhItemSighting>> byItem = sightings
            .GroupBy(s => s.ItemName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (IGrouping<string, GhItemSighting> group in byItem.Take(MaxItemsShown))
            ctx.Reply(FormatTotal(group.ToList()));

        int extraItems = byItem.Count - MaxItemsShown;
        if (extraItems > 0) ctx.Reply($"{extraItems} more matching item(s) — refine your search");
    }

    // One line: total quantity across every matching room, the item's
    // canonical name, and the room locators (map/room only — a room NAME per
    // entry is what made the old per-room reply so long). Every sighting in
    // `sightings` names the same item (FindSightings resolves to one item
    // per query), so summing quantity across them is always meaningful.
    private static string FormatTotal(IReadOnlyList<GhItemSighting> sightings)
    {
        int total = sightings.Sum(s => s.Quantity);
        string name = sightings[0].ItemName;
        List<string> locators = sightings
            .OrderBy(s => s.Map).ThenBy(s => s.Room)
            .Select(s => new RoomKey(s.Map, s.Room).ToString())
            .ToList();

        string rooms = string.Join(", ", locators.Take(MaxRoomsShown));
        int extra = locators.Count - MaxRoomsShown;
        if (extra > 0) rooms += $", +{extra} more";

        return $"total: {total}x {name} - seen in {rooms}";
    }

    // Reply to `@roomba sync` with this client's entire sighting log, encoded
    // compactly into self-contained `@roombadata <blob>` lines (see
    // GhItemSyncCodec — room-grouped, one sweep-time per room, whole rooms packed
    // per line). Each line decodes on its own, so one the game drops costs only
    // its rooms, not the whole reply. No correlation token — every reply carries
    // the responder's name, so a shared gang/local channel yields one set per
    // responder, same as @timer sync.
    private void OnSyncRequest(RemoteCommandContext ctx)
    {
        var records = _locations.ToSyncRecords();
        if (records.Count > MaxSyncRecords)
            records = records.Take(MaxSyncRecords).ToList();

        IReadOnlyList<string> lines = GhItemSyncCodec.EncodeLines(records, MaxBlobCharsPerLine);
        foreach (string line in lines)
            ctx.Reply($"{SyncResponseToken} {line}");

        _log?.Info("RoombaSync",
            $"answered @roomba sync for {ctx.Sender} on {ctx.Channel} — {records.Count} sighting(s) in {lines.Count} line(s)");
    }
}

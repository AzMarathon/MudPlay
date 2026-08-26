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
//   @roomba sync          — replies with this client's entire sighting log AND
//                           its labeled gang-house rooms, compactly encoded (see
//                           GhItemSyncCodec), for the requester's
//                           RoombaSyncReceiver to merge in. Same permission as
//                           the rest of @roomba — mirrors BossTimerQueryHandler's
//                           `@timer sync`.
// The sole gate is the per-player "Query Roomba" (QueryItemLocation) permission:
// RemoteCommandManager only routes @roomba here for a sender who holds it, so
// there is no separate opt-in toggle. Ungranted senders are denied upstream.
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

    // Per-line character budget for each self-contained blob — keeps the wrapped
    // wire line ("bg {@roombadata <blob>}") under the realm's 245-char telepath
    // limit with margin for the wrapper and the send-command prefix. Bigger lines
    // = fewer telepaths per sync. The whole logged sighting set is sent (no record
    // cap): the sync's job is to hand over everything the responder has.
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
    private readonly RoombaSyncSender _sender;
    private bool _disposed;

    public RoombaQueryHandler(RemoteCommandManager engine, GhItemLocationStore locations, GhRoomLabelStore labels,
        LogService? log = null, Action<TimeSpan, Action>? paceScheduler = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(labels);
        _engine = engine;
        _locations = locations;
        _labels = labels;
        _log = log;
        _sender = new RoombaSyncSender(paceScheduler, log);

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

    // Poke from the rate-limit-line watcher (wired in AppServices): the game just
    // dropped a command because we typed too fast, so back off and resend the last
    // sync line. No-op unless a sync is actively draining.
    public void NoteRateLimitClobber() => _sender.NoteClobber();

    private void OnRoomba(RemoteCommandContext ctx)
    {
        // No separate opt-in: the engine only routes @roomba here when the sender
        // holds the per-player "Query Roomba" (QueryItemLocation) permission, so
        // that grant IS the gate. Never-seen / ungranted senders are already
        // denied upstream by RemoteCommandManager.

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
        // Hand over BOTH the labeled gang-house rooms and the item sightings, so a
        // fresh receiver gets the whole gang house (rooms populate their Roomba tab,
        // items back @roomba / the Master List). Labels first — they're few, and the
        // rooms should appear before their contents stream in.
        IReadOnlyList<GhRoomLabel> labels = _labels.Labels.ToList();
        IReadOnlyList<GhItemSyncRecord> records = _locations.ToSyncRecords();
        IReadOnlyList<string> labelLines = GhItemSyncCodec.EncodeLabelLines(labels, MaxBlobCharsPerLine);
        IReadOnlyList<string> itemLines = GhItemSyncCodec.EncodeLines(records, MaxBlobCharsPerLine);

        // Paced out one telepath at a time (RoombaSyncSender) so a big log can't
        // flood the channel or trip the game's burst rate limit — the sync is a
        // background courtesy to the requester, not something that should stall
        // this client's own combat/heal/movement sends.
        List<string> wire = new(labelLines.Count + itemLines.Count);
        foreach (string line in labelLines) wire.Add($"{SyncResponseToken} {line}");
        foreach (string line in itemLines) wire.Add($"{SyncResponseToken} {line}");
        _sender.Enqueue(ctx.Reply, wire);

        _log?.Info("RoombaSync",
            $"answered @roomba sync for {ctx.Sender} on {ctx.Channel} — {labels.Count} label(s) + {records.Count} sighting(s) in {wire.Count} line(s), pacing out");
    }
}

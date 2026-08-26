using System;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;

namespace MudPlay.Game.Remote;

// Read-only handler for @roomba — reports every gang-house room a named item
// is currently tracked in, per GhItemLocationStore's BBS-tier sighting log,
// gated by the QueryItemLocation permission.
//   @roomba <item name>   — ONE consolidated reply line: total quantity summed
//                           across every room currently holding a match, and
//                           the room locators — or "no record". Originally
//                           replied with one chat line per room, which flooded
//                           the channel for anything stocked in several rooms
//                           (report 20260825-172400); a single line is both
//                           quieter and reads more like an actual answer.
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
    // Cap on room locators shown in a single @roomba <item> reply line so an
    // item scattered across a large gang house can't blow the line out to an
    // unreadable length; the overflow folds into a "+N more" tail instead of
    // extra lines (the whole point of this format is ONE line per query).
    private const int MaxRoomsShown = 10;

    // Cap on records per @roomba sync response so a very large gang-house
    // sighting log can't flood the channel; conservative per-line character
    // budget for the compressed blob keeps the wrapped wire line
    // ("bg {@roombadata i/n <blob>}") well under the game's chat-line limit.
    private const int MaxSyncRecords = 500;
    private const int MaxBlobCharsPerLine = 120;

    // The chat token a sync RESPONSE rides on. Registered ignored in AppServices
    // (via this const, not an inline literal) so the remote engine swallows it
    // instead of bouncing "{command invalid}"; RoombaSyncReceiver scrapes it on
    // its own ChatRouter subscription.
    public const string SyncResponseToken = "@roombadata";

    private readonly RemoteCommandManager _engine;
    private readonly GhItemLocationStore _locations;
    private readonly GhRoomLabelStore _labels;
    private bool _disposed;

    public RoombaQueryHandler(RemoteCommandManager engine, GhItemLocationStore locations, GhRoomLabelStore labels)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(labels);
        _engine = engine;
        _locations = locations;
        _labels = labels;

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

        ctx.Reply(FormatTotal(sightings));
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
    // compactly and chunked to fit the wire: `@roombadata <i>/<n> <blob>`. No
    // correlation token — every reply line carries the responder's name, so a
    // shared gang/local channel yields one set per responder, same as
    // @timer sync.
    private void OnSyncRequest(RemoteCommandContext ctx)
    {
        var records = _locations.ToSyncRecords();
        if (records.Count > MaxSyncRecords)
            records = records.Take(MaxSyncRecords).ToList();

        string payload = GhItemSyncCodec.Encode(records);
        IReadOnlyList<string> chunks = GhItemSyncCodec.Chunk(payload, MaxBlobCharsPerLine);
        for (int i = 0; i < chunks.Count; i++)
            ctx.Reply($"{SyncResponseToken} {i + 1}/{chunks.Count} {chunks[i]}");
    }
}

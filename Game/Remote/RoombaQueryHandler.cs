using System;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;

namespace MudPlay.Game.Remote;

// Read-only handler for @roomba — reports the last gang-house room a named
// item was seen in, per GhItemLocationStore's BBS-tier sighting log, gated by
// the QueryItemLocation permission.
//   @roomba <item name>   — the item's last known room, quantity, and how
//                           long ago, or "no record".
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
    private readonly RoomGraphManager _roomGraph;
    private bool _disposed;

    public RoombaQueryHandler(
        RemoteCommandManager engine, GhItemLocationStore locations, GhRoomLabelStore labels, RoomGraphManager roomGraph)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(roomGraph);
        _engine = engine;
        _locations = locations;
        _labels = labels;
        _roomGraph = roomGraph;

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

        if (!_locations.TryFindLastSeen(query, out GhItemSighting sighting))
        {
            ctx.Reply($"no record of \"{query}\"");
            return;
        }

        RoomKey room = new(sighting.Map, sighting.Room);
        string? name = _roomGraph.GetRoom(room)?.Name;
        string where = name is { Length: > 0 } ? $"{name} ({room})" : room.ToString();
        string qty = sighting.Quantity > 1 ? $"{sighting.Quantity}x " : string.Empty;
        ctx.Reply($"{qty}{sighting.ItemName} last seen in {where}, {FormatAge(DateTimeOffset.Now - sighting.SeenAt)} ago");
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

    // Coarse, single-unit age ("3h", "2d") — a @roomba answer only needs to
    // convey roughly how stale a sighting is, not exact elapsed time.
    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalDays >= 1) return $"{(int)age.TotalDays}d";
        if (age.TotalHours >= 1) return $"{(int)age.TotalHours}h";
        if (age.TotalMinutes >= 1) return $"{(int)age.TotalMinutes}m";
        return "<1m";
    }
}

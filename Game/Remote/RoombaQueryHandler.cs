using System;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;

namespace MudPlay.Game.Remote;

// Read-only handler for @roomba — reports the last gang-house room a named
// item was seen in, per GhItemLocationStore's BBS-tier sighting log, gated by
// the QueryItemLocation permission.
//   @roomba <item name>   — the item's last known room, or "no record".
// Silent (no reply at all, not even a denial) when GhRoomLabelStore's
// ResponsesEnabled toggle is off — the feature is opt-in per BBS, and staying
// silent while off avoids advertising the command to a gang that hasn't turned
// it on.
public sealed class RoombaQueryHandler : IDisposable
{
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
        ctx.Reply($"{sighting.ItemName} last seen in {where}, {FormatAge(DateTimeOffset.Now - sighting.SeenAt)} ago");
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

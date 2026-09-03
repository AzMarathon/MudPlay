using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.ViewModels.GameData.Edit;

namespace MudPlay.ViewModels.Navigation;

// Builds the "Details…" step list for the route the nav engine is CURRENTLY
// executing, in the same shape the route picker's Show-steps panel uses. Input is
// the active route's room-key polyline (source-first) — the very sequence the map
// draws as the route line, so the same builder serves a point-to-point walk, a
// loop circuit, and an Auto-Lair approach. The polyline is turned into the
// expanded WalkStep sequence exactly as the picker does (DirectionsAlong →
// RemoteActionPathExpander.Expand → RouteStepList.Build); each resulting row is
// then annotated with its room's lair monsters via the injected lookup.
//
// Pure aside from the two injected lookups (item names, per-room lair links), so
// it's unit-testable without a live game-data cache or the record-opener command.
public static class CurrentRouteDetails
{
    public static IReadOnlyList<RouteDetailRow> Build(
        RoomGraphManager graph,
        BfsMapper? bfs,
        IRoomFilter? filter,
        IReadOnlyList<RoomKey> route,
        Func<int, string?> itemName,
        Func<RoomKey, IReadOnlyList<RoomDetailLink>> roomMonsterLinks,
        Action<RoomKey> onRoomClick,
        Func<RoomKey, RouteRoomHazard?> roomHazard)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(itemName);
        ArgumentNullException.ThrowIfNull(roomMonsterLinks);
        ArgumentNullException.ThrowIfNull(onRoomClick);
        ArgumentNullException.ThrowIfNull(roomHazard);

        // A route needs at least one hop (two rooms) to have any steps.
        if (route is not { Count: > 1 }) return Array.Empty<RouteDetailRow>();

        RoomKey source = route[0];
        IReadOnlyList<Direction> dirs = RouteStepList.DirectionsAlong(graph, route);
        if (dirs.Count == 0) return Array.Empty<RouteDetailRow>();

        IReadOnlyList<WalkStep> steps = RemoteActionPathExpander.Expand(graph, source, dirs, bfs, filter);

        // No gated acquire rows for the current route — the run's gates were already
        // resolved at plan time, and the walker's expanded steps already carry the
        // door/winch/lever crossings. So pass no gate stops (obtain lookup unused).
        IReadOnlyList<RouteStepRow> rows = RouteStepList.Build(
            source, steps, Array.Empty<RouteGateStop>(),
            key => graph.GetRoom(key)?.DisplayName,
            itemName,
            static _ => null);

        var details = new List<RouteDetailRow>(rows.Count);
        foreach (RouteStepRow row in rows)
        {
            RoomKey rk = row.Room;
            details.Add(new RouteDetailRow(
                row, roomMonsterLinks(rk), new RelayCommand(() => onRoomClick(rk)), roomHazard(rk)));
        }
        return details;
    }
}

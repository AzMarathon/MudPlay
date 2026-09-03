using MudPlay.ViewModels.GameData.Edit;

namespace MudPlay.ViewModels.Navigation;

// One row of the Current-route Details view: the route-picker's numbered
// "N> map/room < command" step, plus that room's notable monsters (placed
// fixtures + lair spawners) as clickable record links. Wrapping RouteStepRow
// rather than extending it keeps the pure step builder UI-free — the monster
// links, which need live game data + a record-opener command, are attached here
// in the VM layer.
public sealed class RouteDetailRow
{
    public RouteStepRow Step { get; }

    // The placed + lair monsters standing in this row's room, each opening its
    // Game Data record on click. Empty for a room with none (the common case).
    public IReadOnlyList<RoomDetailLink> Monsters { get; }

    public bool HasMonsters => Monsters.Count > 0;
    public string Line => Step.Line;
    public bool IsAcquire => Step.IsAcquire;

    public RouteDetailRow(RouteStepRow step, IReadOnlyList<RoomDetailLink> monsters)
    {
        Step = step;
        Monsters = monsters ?? Array.Empty<RoomDetailLink>();
    }
}

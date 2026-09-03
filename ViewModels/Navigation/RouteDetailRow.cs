using MudPlay.ViewModels.GameData.Edit;

namespace MudPlay.ViewModels.Navigation;

// One row of the Current-route Details view: the route-picker's numbered
// "N> map/room < command" step, plus (when the row's room hosts a lair) that
// room's lair monsters as clickable record links. Wrapping RouteStepRow rather
// than extending it keeps the pure step builder UI-free — the monster links, which
// need live game data + a record-opener command, are attached here in the VM layer.
public sealed class RouteDetailRow
{
    public RouteStepRow Step { get; }

    // The lair monsters standing in this row's room, each opening its Game Data
    // record on click. Empty for a room with no lair (the common case).
    public IReadOnlyList<RoomDetailLink> LairMonsters { get; }

    public bool HasLair => LairMonsters.Count > 0;
    public string Line => Step.Line;
    public bool IsAcquire => Step.IsAcquire;

    public RouteDetailRow(RouteStepRow step, IReadOnlyList<RoomDetailLink> lairMonsters)
    {
        Step = step;
        LairMonsters = lairMonsters ?? Array.Empty<RoomDetailLink>();
    }
}

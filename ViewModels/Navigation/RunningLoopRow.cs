using MudPlay.Game.Map;

namespace MudPlay.ViewModels.Navigation;

// One room in the running-loop rail — the live-editable green counterpart to the
// builder's LoopBuilderRow. Carries the same per-waypoint fields the running-loop
// editor tunes in place (command / delay / do-not-rest / do-not-attack), plus
// IsCurrentRoom so the rail can highlight the room the loop is currently in.
// Add / remove / reorder aren't offered while running, so — unlike LoopBuilderRow —
// this row has no move/remove affordance.
public sealed record RunningLoopRow(
    int Index, RoomKey Key, string Name, string? Command, int DelayMs,
    bool DoNotRest, bool DoNotAttack, bool IsCurrentRoom)
{
    public bool HasCommand => !string.IsNullOrWhiteSpace(Command);
    public bool HasDoNotRest => DoNotRest;
    public bool HasDoNotAttack => DoNotAttack;
}

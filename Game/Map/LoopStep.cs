using System.Text.Json.Serialization;

namespace MudPlay.Game.Map;

// One unit of work in a saved navigation loop. Mirrors WalkStep shape so
// the loop runner can share the walker's execution path, but adds a
// DelayMs field for "wait X ms before moving on" pauses the user can
// attach to custom commands (dep 100, ask barmaid pie, etc.).
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(MoveLoopStep),    typeDiscriminator: "move")]
[JsonDerivedType(typeof(CommandLoopStep), typeDiscriminator: "command")]
[JsonDerivedType(typeof(LapWaitLoopStep), typeDiscriminator: "lapwait")]
public abstract record LoopStep
{
    // Display label for the Navigation right rail / loop editor list.
    [JsonIgnore]
    public abstract string Display { get; }
}

// A planar movement step. Same execution semantics as MoveStep.
public sealed record MoveLoopStep(Direction Direction) : LoopStep
{
    // The crossing of a lever exit whose actions the lap types on its own detour
    // just before (MoveStep.SkipSpecialDispatch has the reasoning): crossed as a
    // plain cardinal. Expansion-only, never saved.
    [JsonIgnore]
    public bool SkipSpecialDispatch { get; init; }

    public override string Display => Direction switch
    {
        Map.Direction.N  => "north",
        Map.Direction.S  => "south",
        Map.Direction.E  => "east",
        Map.Direction.W  => "west",
        Map.Direction.NE => "northeast",
        Map.Direction.NW => "northwest",
        Map.Direction.SE => "southeast",
        Map.Direction.SW => "southwest",
        Map.Direction.U  => "up",
        Map.Direction.D  => "down",
        _ => "?",
    };
}

// A free-text in-room command (e.g. dep 100, ask barmaid pie). DelayMs is
// the runner's wait, in real time, between sending the command and proceeding
// to the next step; 0 means "advance once the game has answered the command".
public sealed record CommandLoopStep(string Command, long DelayMs = 0) : LoopStep
{
    // A lever exit's action on the detour the expansion lays in ahead of the exit,
    // not a waypoint's command. Expansion-only, never saved.
    [JsonIgnore]
    public bool IsExitAction { get; init; }

    // That action is a winch pull, re-pulled until the winch turns (CommandStep.IsWinchPull).
    [JsonIgnore]
    public bool IsWinchPull { get; init; }

    // The door or gate that action opens (CommandStep.DoorOrGate).
    [JsonIgnore]
    public (RoomKey Room, Direction Dir)? DoorOrGate { get; init; }

    public override string Display => DelayMs > 0
        ? $"{Command} (wait {LoopDelay.Describe(DelayMs)})"
        : Command;
}

// A waypoint's "wait here until the lap has run LapTimeMs" (LoopWaypoint.LapWaitMs):
// the runner holds in the room until the current lap's clock reaches it, the way a
// command's delay holds, and goes straight on when the lap is already past it.
public sealed record LapWaitLoopStep(long LapTimeMs) : LoopStep
{
    public override string Display => $"wait until lap time {LoopDelay.Describe(LapTimeMs)}";
}

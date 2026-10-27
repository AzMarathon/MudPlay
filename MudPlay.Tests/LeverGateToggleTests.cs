using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// A lever-opened door or gate: the engine flips it on every action, so a pull on
// one standing open locks it (GAME_MECHANICS "Multi-action exits and levers",
// Stock 1.11p's action handler; the user, 2026-10-26: Paradigm the same). The
// client pulls one only when it isn't known to be open: a same-room lever when the
// room shows its gate open, read off the display the move arrives on, and a lever
// in another room while a pull of ours opened its gate less than 5 minutes ago.
public sealed class LeverGateToggleTests : IDisposable
{
    private readonly string _root;

    public LeverGateToggleTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-lever-gate-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private static readonly RoomKey Yard = new(4, 4), Gatehouse = new(4, 1), Hall = new(4, 2), LeverRoom = new(4, 3);

    // A courtyard gate whose lever is on the courtyard's own wall: `(Door …)` with
    // an action cell of its own room, as Newhaven's 1/1375 S is.
    private const string CourtyardJson = """
        [
          { "Map Number": 4, "Room Number": 4, "Name": "Yard", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "4/1", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 4, "Room Number": 1, "Name": "Courtyard", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "4/2 (Door [301 picklocks/strength])", "S": "4/4", "E": "Action#1 [on the N exit of this room]: pull lever", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 4, "Room Number": 2, "Name": "Hall", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "4/1", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    // A gatehouse gate whose lever is in a room off the yard before it, as the
    // Newhaven inner gate's levers are in its guardrooms.
    private const string GatehouseJson = """
        [
          { "Map Number": 4, "Room Number": 4, "Name": "Yard", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "4/1", "S": "0", "E": "4/3", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 4, "Room Number": 1, "Name": "Gatehouse", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "4/2 (Door [301 picklocks/strength])", "S": "4/4", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 4, "Room Number": 2, "Name": "Hall", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "4/1", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 4, "Room Number": 3, "Name": "Guardroom", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "4/4", "NE": "Action#1 [on the N exit of room 4/1]: pull lever",
            "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private static Loop YardToHall() => new("gate", new[] { Yard, Hall });

    private LeverWorld Courtyard() => new(_root, CourtyardJson, new[] { (Gatehouse, "pull lever", Gatehouse, Direction.N) });

    private LeverWorld GatehouseWorld(bool withWalker = false) =>
        new(_root, GatehouseJson, new[] { (LeverRoom, "pull lever", Gatehouse, Direction.N) }, withWalker: withWalker);

    // The step through the gate goes out from inside the arrival into the courtyard,
    // before that display's exits used to be read: the gate it showed open read as
    // shut, and its lever was pulled, locking it.
    [Fact]
    public void SameRoomLever_GateShownOpenOnArrival_IsCrossedWithoutPullingIt()
    {
        LeverWorld world = Courtyard();
        world.Open.Add((Gatehouse, Direction.N));
        world.StandIn(Yard);

        Assert.True(world.Runner.Start(YardToHall()));
        world.Play(() => world.At.Equals(Hall));

        Assert.Equal(0, world.Count("pull lever"));
        Assert.Contains((Gatehouse, "n"), world.Moves);
        Assert.Empty(world.Bonks);
    }

    [Fact]
    public void SameRoomLever_GateShownShutOnArrival_IsPulledOnceThenCrossed()
    {
        LeverWorld world = Courtyard();
        world.StandIn(Yard);

        Assert.True(world.Runner.Start(YardToHall()));
        world.Play(() => world.At.Equals(Hall));

        Assert.Equal(1, world.Count("pull lever"));
        Assert.Contains((Gatehouse, "n"), world.Moves);
        Assert.Empty(world.Bonks);
    }

    // Lap 1 pulls the guardroom lever and sees the gate open; lap 2, inside the
    // 5 minutes, leaves the lever alone; once those are up and the gate has locked
    // again, lap 3 pulls it once more.
    [Fact]
    public void RemoteLever_OpenedByOurPull_IsNotPulledAgainInsideTheWindow_ButIsAfter()
    {
        LeverWorld world = GatehouseWorld();
        world.StandIn(Yard);
        int ThroughTheGate() => world.Moves.Count(m => m.From.Equals(Gatehouse) && m.Sent == "n");

        Assert.True(world.Runner.Start(YardToHall()));
        world.Play(() => ThroughTheGate() == 2 && world.At.Equals(Hall));
        Assert.Equal(1, world.Count("pull lever"));
        Assert.Empty(world.Bonks);

        // In the hall on lap 2, the 5 minutes run out and the engine locks the gate.
        world.Clock.Advance(LeverGateMemory.OpenWindow);
        world.Open.Remove((Gatehouse, Direction.N));
        world.Play(() => ThroughTheGate() == 3 && world.At.Equals(Hall));

        Assert.Equal(2, world.Count("pull lever"));
        Assert.Empty(world.Bonks);
        Assert.Equal(3, ThroughTheGate());
    }

    // The walker keeps the same rule: the second walk through the gate within the
    // window doesn't pull the lever its first walk opened the gate with.
    [Fact]
    public void Walker_SecondWalkThroughAGateItsPullOpened_DoesNotPullItAgain()
    {
        LeverWorld world = GatehouseWorld(withWalker: true);
        world.StandIn(Yard);

        Assert.True(world.Walker!.WalkTo(Hall));
        world.Play(() => world.At.Equals(Hall) && world.Walker.State == WalkState.Idle);
        Assert.True(world.Walker.WalkTo(Yard));
        world.Play(() => world.At.Equals(Yard) && world.Walker.State == WalkState.Idle);
        Assert.True(world.Walker.WalkTo(Hall));
        world.Play(() => world.At.Equals(Hall) && world.Walker.State == WalkState.Idle);

        Assert.Equal(1, world.Count("pull lever"));
        Assert.Equal(2, world.Moves.Count(m => m.From.Equals(Gatehouse) && m.Sent == "n"));
        Assert.Empty(world.Bonks);
    }
}

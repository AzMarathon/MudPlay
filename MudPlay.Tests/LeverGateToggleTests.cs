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
        world.Clock.Advance(TimeSpan.FromSeconds(300));
        world.Open.Remove((Gatehouse, Direction.N));
        world.Play(() => ThroughTheGate() == 3 && world.At.Equals(Hall));

        Assert.Equal(2, world.Count("pull lever"));
        Assert.Empty(world.Bonks);
        Assert.Equal(3, ThroughTheGate());
    }

    // The gate shown open as we arrive in its room, and shut by the time we step
    // (`The gate is closed!`): the walker takes the crossing, pulls the lever again
    // and goes through, and the lap carries on with no recovery.
    [Fact]
    public void PrimedCrossingRefusedAtTheGate_IsWalkedByTheWalker_LeverAndAll()
    {
        LeverWorld world = GatehouseWorld(withWalker: true);
        world.StandIn(Yard);
        bool shut = false;
        world.BeforeAnswer = sent =>
        {
            if (shut || sent != "n" || !world.At.Equals(Gatehouse)) return;
            shut = true;
            world.Open.Remove((Gatehouse, Direction.N));
        };

        Assert.True(world.Runner.Start(YardToHall()));
        world.Play(() => world.At.Equals(Hall));

        Assert.Equal(new[] { "4/1 N" }, world.Bonks);
        Assert.Equal(2, world.Count("pull lever"));
        Assert.DoesNotContain(world.Events, e => e.Kind == LoopEventKind.Failed);
        Assert.DoesNotContain(world.Events, e => e.Detail.StartsWith("recovering", StringComparison.Ordinal));
        Assert.Equal(Hall, world.At);
    }

    // A gate seen open that no pull of ours opened counts the same (the user's
    // ruling for doors, 2026-10-18): a walk through it within the window leaves
    // its lever alone.
    [Fact]
    public void GateSeenOpen_SomeoneElsesPull_IsNotPulledShut()
    {
        LeverWorld world = GatehouseWorld(withWalker: true);
        world.Open.Add((Gatehouse, Direction.N));
        world.StandIn(Gatehouse);
        world.Show(Gatehouse);

        Assert.True(world.Walker!.WalkTo(Yard));
        world.Play(() => world.At.Equals(Yard) && world.Walker.State == WalkState.Idle);
        Assert.True(world.Walker.WalkTo(Hall));
        world.Play(() => world.At.Equals(Hall) && world.Walker.State == WalkState.Idle);

        Assert.Equal(0, world.Count("pull lever"));
        Assert.Empty(world.Bonks);
    }

    // The gate held open from lap 1's pull shuts before the run gets back to it,
    // and the gatehouse shows it shut as the run arrives: the shown shut outranks
    // the hold, the crossing goes to the walker, which pulls the lever, and nobody
    // walks into the shut gate.
    [Fact]
    public void HeldGateShutBeforeArrival_IsPulledAgain_WithNoBonk()
    {
        LeverWorld world = GatehouseWorld(withWalker: true);
        world.StandIn(Yard);
        int intoTheGatehouse = 0;
        world.BeforeAnswer = sent =>
        {
            if (sent != "n" || !world.At.Equals(Yard) || ++intoTheGatehouse != 2) return;
            world.Open.Remove((Gatehouse, Direction.N));
        };

        Assert.True(world.Runner.Start(YardToHall()));
        world.Play(() => world.Moves.Count(m => m.From.Equals(Gatehouse) && m.Sent == "n") == 2 && world.At.Equals(Hall));

        Assert.Empty(world.Bonks);
        Assert.Equal(2, world.Count("pull lever"));
        Assert.DoesNotContain(world.Events, e => e.Kind == LoopEventKind.Failed);
        Assert.Equal(Hall, world.At);
    }

    // A gate seen open is held 300 s from the sighting. A walk that would leave its
    // lever detour out counts the walk there and the last minute: with 100 s left
    // it does, with 50 s left it walks the detour, and at the lever, the gate
    // still held, doesn't pull it (a pull would lock it).
    [Theory]
    [InlineData(200, false)]
    [InlineData(250, true)]
    public void WalkPlan_LeavesTheDetourOutOnlyWhileTheHoldOutlastsTheWalk_AndNeverPullsAHeldGate(int secondsLater, bool detour)
    {
        LeverWorld world = GatehouseWorld(withWalker: true);
        world.Open.Add((Gatehouse, Direction.N));
        world.StandIn(Gatehouse);
        world.Show(Gatehouse);
        Assert.True(world.Walker!.WalkTo(Yard));
        world.Play(() => world.At.Equals(Yard) && world.Walker.State == WalkState.Idle);

        world.Clock.Advance(TimeSpan.FromSeconds(secondsLater));
        Assert.True(world.Walker.WalkTo(Hall));
        world.Play(() => world.At.Equals(Hall) && world.Walker.State == WalkState.Idle);

        Assert.Equal(detour, world.Moves.Contains((Yard, "e")));
        Assert.Equal(0, world.Count("pull lever"));
        Assert.Equal(Hall, world.At);
        Assert.Empty(world.Bonks);
    }

    // A hall whose way back to the gatehouse is a door too, so a pull that flips the
    // gate flips that side with it.
    private const string TwoSidedJson = """
        [
          { "Map Number": 4, "Room Number": 4, "Name": "Yard", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "4/1", "S": "0", "E": "4/3", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 4, "Room Number": 1, "Name": "Gatehouse", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "4/2 (Door [301 picklocks/strength])", "S": "4/4", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 4, "Room Number": 2, "Name": "Hall", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "4/1 (Door [301 picklocks/strength])", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 4, "Room Number": 3, "Name": "Guardroom", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "4/4", "NE": "Action#1 [on the N exit of room 4/1]: pull lever",
            "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private static readonly (RoomKey Room, Direction Dir) Gate = (Gatehouse, Direction.N);

    // Our pull opened the gate 120 s before its room was shown open: it is held from
    // the pull (until 300 s after it, counted on to 240 s), and its far side with
    // it. A gate seen open with no pull of ours is held from the sighting.
    [Fact]
    public void GateOpenedByOurPull_IsHeldFromThePull_FarSideToo_ASightingFromTheSighting()
    {
        LeverWorld pulled = new(_root, TwoSidedJson, new[] { (LeverRoom, "pull lever", Gatehouse, Direction.N) });
        Assert.True(pulled.Held.MayPullLever(Gate, "pull lever", "test"));
        pulled.Clock.Advance(TimeSpan.FromSeconds(120));
        pulled.Open.Add(Gate);
        pulled.StandIn(Gatehouse);
        pulled.Show(Gatehouse);

        pulled.Clock.Advance(TimeSpan.FromSeconds(119));   // 239 s after the pull
        Assert.True(pulled.Held.IsHeldOpen(Gatehouse, Direction.N, 0));
        Assert.True(pulled.Held.IsHeldOpen(Hall, Direction.S, 0));
        pulled.Clock.Advance(TimeSpan.FromSeconds(2));     // 241 s
        Assert.False(pulled.Held.IsHeldOpen(Gatehouse, Direction.N, 0));
        Assert.False(pulled.Held.IsHeldOpen(Hall, Direction.S, 0));
        Assert.False(pulled.Held.MayPullLever(Gate, "pull lever", "test"));   // still open, though

        LeverWorld seen = new(_root, TwoSidedJson, new[] { (LeverRoom, "pull lever", Gatehouse, Direction.N) });
        seen.Clock.Advance(TimeSpan.FromSeconds(120));
        seen.Open.Add(Gate);
        seen.StandIn(Gatehouse);
        seen.Show(Gatehouse);
        seen.Clock.Advance(TimeSpan.FromSeconds(121));
        Assert.True(seen.Held.IsHeldOpen(Gatehouse, Direction.N, 0));
        Assert.False(seen.Held.IsHeldOpen(Hall, Direction.S, 0));
    }

    // A pull whose gate wasn't seen within its window doesn't date a later
    // sighting: that gate is held from the sighting, as any door.
    [Fact]
    public void PullNotSeenWithinItsWindow_ALaterSightingIsHeldFromTheSighting()
    {
        LeverWorld world = new(_root, TwoSidedJson, new[] { (LeverRoom, "pull lever", Gatehouse, Direction.N) });
        Assert.True(world.Held.MayPullLever(Gate, "pull lever", "test"));
        world.Clock.Advance(TimeSpan.FromSeconds(400));
        world.Open.Add(Gate);
        world.StandIn(Gatehouse);
        world.Show(Gatehouse);

        world.Clock.Advance(TimeSpan.FromSeconds(200));
        Assert.True(world.Held.IsHeldOpen(Gatehouse, Direction.N, 0));
        Assert.False(world.Held.IsHeldOpen(Hall, Direction.S, 0));
        Assert.DoesNotContain(world.Held.Describe(), d => d.Contains("lever pulled", StringComparison.Ordinal));
    }

    // A display that confirms a room is read into the held doors even when an engine
    // moved on from inside its transition, leaving the tracker Pending by the end of
    // the read.
    [Fact]
    public void RoomShown_IsRaisedForTheRoomConfirmed_WhenAnEngineMovedOnInsideItsTransition()
    {
        LeverWorld world = GatehouseWorld();
        world.StandIn(Yard);
        List<RoomKey> shown = new();
        world.Tracker.RoomShown += (room, _) => shown.Add(room);
        world.Tracker.StateChanged += t =>
        {
            if (t.Displayed && t.NewRoom?.Key == Gatehouse && t.NewConfidence == RoomConfidence.Confirmed)
                world.Tracker.NoteMoveSent(Direction.N);
        };
        world.Open.Add(Gate);

        world.Tracker.NoteMoveSent(Direction.N);
        world.Server.Line("[HP=565]:n");
        world.Show(Gatehouse);

        Assert.Equal(RoomConfidence.Pending, world.Tracker.State.Confidence);
        Assert.Equal(new[] { Gatehouse }, shown);
        Assert.True(world.Held.IsHeldOpen(Gatehouse, Direction.N, 0));
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

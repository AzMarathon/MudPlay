using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// A loop through a lever exit whose actions are typed in other rooms. The Paradigm
// treetops above the Lost City (data-Anomaly-v0.61.48-checked) chain five such
// exits: 16/551 N (to the vine up to the Narrow Ledge, 16/640) opens to `tear bark`
// in 16/569, behind 16/568 NW, which opens to `push knot` in 16/567, and so on down
// to `turn knot` in 16/547. A walk-to there runs the go-act-return detour; a loop
// expanded the legs as plain moves and stopped at the exit with "multi-action exit
// requires actions in another room" (report paradigm-20261026-220231). The loop now
// takes the walker's expansion of each leg.
public sealed class LoopRemoteActionExitTests : IDisposable
{
    private readonly string _root;

    public LoopRemoteActionExitTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-loop-lever-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // The rooms 16/543-16/569 and 16/640 as the set has them, the two exits out of
    // the fixture (16/543 D, 16/640 NE) left out.
    private const string TreetopsJson = """
        [
          {"Map Number": 16, "Room Number": 543, "Name": "Rooftop", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "16/544", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0"},
          {"Map Number": 16, "Room Number": 544, "Name": "Rooftop", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "0", "S": "16/543", "E": "0", "W": "0", "NE": "16/545 (Text: climb branch, go branch)", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0"},
          {"Map Number": 16, "Room Number": 545, "Name": "Treetops", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "0", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "16/546", "SE": "16/562", "SW": "16/544 (Text: climb branch, go branch)", "U": "0", "D": "0"},
          {"Map Number": 16, "Room Number": 546, "Name": "Treetops", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "16/548", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "16/545", "SW": "0", "U": "0", "D": "0"},
          {"Map Number": 16, "Room Number": 547, "Name": "Treetops", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "0", "S": "0", "E": "16/568", "W": "0", "NE": "Action#1 [on the N exit of room 16/560]: turn knot, twist knot", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0"},
          {"Map Number": 16, "Room Number": 548, "Name": "Treetops", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "0", "S": "16/546", "E": "0", "W": "0", "NE": "16/549", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0"},
          {"Map Number": 16, "Room Number": 549, "Name": "Treetops", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "0", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "16/550", "SW": "16/548", "U": "0", "D": "0"},
          {"Map Number": 16, "Room Number": 550, "Name": "Treetops", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "16/551", "S": "0", "E": "0", "W": "0", "NE": "16/553", "NW": "16/549", "SE": "0", "SW": "0", "U": "0", "D": "0"},
          {"Map Number": 16, "Room Number": 551, "Name": "Treetops", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "16/552 (Hidden/Needs 1 Actions, any order)", "S": "16/550", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0"},
          {"Map Number": 16, "Room Number": 552, "Name": "Treetops", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "0", "S": "16/551", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "16/640 (Text: climb vine)", "D": "0"},
          {"Map Number": 16, "Room Number": 553, "Name": "Treetops", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "0", "S": "0", "E": "16/554", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "16/550", "U": "0", "D": "0"},
          {"Map Number": 16, "Room Number": 554, "Name": "Treetops", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "0", "S": "0", "E": "0", "W": "16/553", "NE": "0", "NW": "0", "SE": "16/555", "SW": "0", "U": "0", "D": "0"},
          {"Map Number": 16, "Room Number": 555, "Name": "Treetops", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "0", "S": "16/557", "E": "0", "W": "16/556 (Hidden/Needs 1 Actions, any order)", "NE": "0", "NW": "16/554", "SE": "0", "SW": "16/566", "U": "0", "D": "0"},
          {"Map Number": 16, "Room Number": 556, "Name": "Treetops", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "0", "S": "0", "E": "16/555", "W": "0", "NE": "Action#1 [on the N exit of room 16/565]: pull vine", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0"},
          {"Map Number": 16, "Room Number": 557, "Name": "Treetops", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "16/555", "S": "16/558", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0"},
          {"Map Number": 16, "Room Number": 558, "Name": "Treetops", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "16/557", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "16/559", "U": "0", "D": "0"},
          {"Map Number": 16, "Room Number": 559, "Name": "Treetops", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "16/564", "S": "0", "E": "0", "W": "16/560", "NE": "16/558", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0"},
          {"Map Number": 16, "Room Number": 560, "Name": "Treetops", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "16/561 (Hidden/Needs 1 Actions, any order)", "S": "0", "E": "16/559", "W": "16/562", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0"},
          {"Map Number": 16, "Room Number": 561, "Name": "Treetops", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "0", "S": "16/560", "E": "0", "W": "0", "NE": "Action#1 [on the W exit of room 16/555]: move branch", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0"},
          {"Map Number": 16, "Room Number": 562, "Name": "Treetops", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "16/563", "S": "0", "E": "16/560", "W": "0", "NE": "0", "NW": "16/545", "SE": "0", "SW": "0", "U": "0", "D": "0"},
          {"Map Number": 16, "Room Number": 563, "Name": "Treetops", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "0", "S": "16/562", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0"},
          {"Map Number": 16, "Room Number": 564, "Name": "Treetops", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "0", "S": "16/559", "E": "0", "W": "0", "NE": "0", "NW": "16/565", "SE": "0", "SW": "0", "U": "0", "D": "0"},
          {"Map Number": 16, "Room Number": 565, "Name": "Treetops", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "16/567 (Hidden/Needs 1 Actions, any order)", "S": "0", "E": "16/566", "W": "16/568", "NE": "0", "NW": "0", "SE": "16/564", "SW": "0", "U": "0", "D": "0"},
          {"Map Number": 16, "Room Number": 566, "Name": "Treetops", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "0", "S": "0", "E": "0", "W": "16/565", "NE": "16/555", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0"},
          {"Map Number": 16, "Room Number": 567, "Name": "Treetops", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "0", "S": "16/565", "E": "0", "W": "0", "NE": "Action#1 [on the NW exit of room 16/568]: push knot", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0"},
          {"Map Number": 16, "Room Number": 568, "Name": "Treetops", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "0", "S": "0", "E": "16/565", "W": "16/547", "NE": "0", "NW": "16/569 (Hidden/Needs 1 Actions, any order)", "SE": "0", "SW": "0", "U": "0", "D": "0"},
          {"Map Number": 16, "Room Number": 569, "Name": "Treetops", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "0", "S": "0", "E": "0", "W": "0", "NE": "Action#1 [on the N exit of room 16/551]: tear bark", "NW": "0", "SE": "16/568", "SW": "0", "U": "0", "D": "0"},
          {"Map Number": 16, "Room Number": 640, "Name": "Narrow Ledge", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "N": "0", "S": "0", "E": "0", "W": "0", "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "16/552 (Text: climb vine)"}
        ]
        """;

    private static readonly RoomKey Ledge = new(16, 640);
    private static readonly RoomKey Rooftop = new(16, 543);
    private static readonly RoomKey VineGate = new(16, 551);

    // The five levers in the order the chain needs them, each with the room it is
    // typed in and the exit it opens.
    private static readonly (RoomKey Room, string Command, RoomKey Opens, Direction Way)[] Levers =
    {
        (new(16, 547), "turn knot", new(16, 560), Direction.N),
        (new(16, 561), "move branch", new(16, 555), Direction.W),
        (new(16, 556), "pull vine", new(16, 565), Direction.N),
        (new(16, 567), "push knot", new(16, 568), Direction.NW),
        (new(16, 569), "tear bark", VineGate, Direction.N),
    };

    private static Loop LedgeToRooftop() => new("Loop 21-59", new[] { Ledge, Rooftop });

    private RoomGraphManager Graph(string json)
    {
        string set = Path.Combine(_root, "alpha");
        Directory.CreateDirectory(set);
        File.WriteAllText(Path.Combine(set, "Rooms.json"), json);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        return graph;
    }

    // The room each lever action of a lap is typed in, walking the lap's steps from
    // its first room over the map.
    private static List<(RoomKey Room, string Command)> ActionsAlong(
        RoomGraphManager graph, RoomKey start, IEnumerable<LoopStep> steps, out RoomKey end, out List<string> unprimed)
    {
        List<(RoomKey, string)> typed = new();
        unprimed = new();
        RoomKey at = start;
        foreach (LoopStep step in steps)
        {
            switch (step)
            {
                case CommandLoopStep command:
                    Assert.True(command.IsExitAction);
                    typed.Add((at, command.Command));
                    break;
                case MoveLoopStep move:
                    RoomExit exit = graph.GetRoom(at)!.Exits[move.Direction];
                    if (exit.Hint == RoomExitHint.MultiActionHidden && !move.SkipSpecialDispatch)
                        unprimed.Add($"{at} {move.Direction}");
                    at = exit.Target;
                    break;
            }
        }
        end = at;
        return typed;
    }

    [Fact]
    public void Expansion_TypesTheWholeLeverChainInItsRooms_BeforeCrossingTheVineGate()
    {
        RoomGraphManager graph = Graph(TreetopsJson);
        BfsMapper bfs = new(graph);

        (IReadOnlyList<LoopStep> steps, IReadOnlyList<(RoomKey From, RoomKey To)> unreachable) =
            LoopExpander.Expand(LedgeToRooftop().Waypoints, bfs, graph);

        Assert.Empty(unreachable);
        List<(RoomKey Room, string Command)> typed =
            ActionsAlong(graph, Ledge, steps, out RoomKey end, out List<string> unprimed);
        Assert.Equal(Levers.Select(l => (l.Room, l.Command)), typed);
        // Every lever exit the lap crosses, the vine gate and the four inside the
        // chain, is crossed after its action, as a plain move.
        Assert.Empty(unprimed);
        Assert.Equal(Ledge, end);
    }

    // The room stood in before each step of a lap walked from start.
    private static List<RoomKey> RoomsBeforeEachStep(RoomGraphManager graph, RoomKey start, IReadOnlyList<LoopStep> steps)
    {
        List<RoomKey> before = new(steps.Count);
        RoomKey at = start;
        foreach (LoopStep step in steps)
        {
            before.Add(at);
            if (step is MoveLoopStep move) at = graph.GetRoom(at)!.Exits[move.Direction].Target;
        }
        return before;
    }

    // The vine gate's detour, as the plan marks it: it leaves the route at 16/545
    // (where the walk-to of the report left it too), comes back there with every
    // lever typed, and the crossing is the primed step out of 16/551.
    [Fact]
    public void Plan_MarksTheVineGateDetour_FromLeavingTheRouteToItsCrossing()
    {
        RoomGraphManager graph = Graph(TreetopsJson);
        BfsMapper bfs = new(graph);

        LoopExpansion plan = LoopExpander.Plan(LedgeToRooftop().Waypoints, bfs, graph);

        LeverDetour vine = Assert.Single(plan.Detours);
        Assert.Equal((VineGate, Direction.N, false), (vine.GateRoom, vine.GateDir, vine.OneWay));
        List<RoomKey> before = RoomsBeforeEachStep(graph, Ledge, plan.Steps);
        Assert.Equal(new RoomKey(16, 545), before[vine.Start]);
        Assert.Equal(new RoomKey(16, 545), before[vine.End]);
        Assert.Equal(VineGate, before[vine.Cross]);
        Assert.True(plan.Steps[vine.Cross] is MoveLoopStep { Direction: Direction.N, SkipSpecialDispatch: true });
        int[] actions = Enumerable.Range(0, plan.Steps.Count).Where(i => plan.Steps[i] is CommandLoopStep).ToArray();
        Assert.Equal(5, actions.Length);
        Assert.All(actions, i => Assert.InRange(i, vine.Start, vine.End - 1));
    }

    // A lever room no route may enter leaves its gate shut: the leg through it is
    // reported unreachable, and the loop isn't started, naming the exit and the
    // lever room.
    [Fact]
    public void LeverRoomAvoided_TheLegIsUnreachable_AndTheLoopIsRefusedNamingTheExitAndLeverRoom()
    {
        RoomGraphManager graph = Graph(TreetopsJson);
        BfsMapper bfs = new(graph);
        AvoidRooms avoid = new(new RoomKey(16, 569));

        LoopExpansion plan = LoopExpander.Plan(LedgeToRooftop().Waypoints, bfs, graph, avoid);
        Assert.Equal(new[] { (Rooftop, Ledge) }, plan.UnreachableSegments);
        UnroutableLeverLeg lever = Assert.Single(plan.UnroutableLevers);
        Assert.Equal((VineGate, Direction.N, new RoomKey(16, 569)), (lever.GateRoom, lever.GateDir, lever.To));

        LeverWorld world = World(filter: avoid);
        world.StandIn(Ledge);
        Assert.False(world.Runner.Start(LedgeToRooftop()));
        LoopEvent failed = Assert.Single(world.Events, e => e.Kind == LoopEventKind.Failed);
        Assert.Contains("the exit north of 16/551", failed.Detail);
        Assert.Contains("opened from 16/569", failed.Detail);
        Assert.Empty(world.Sent);
    }

    private sealed class AvoidRooms(params RoomKey[] rooms) : IRoomFilter
    {
        public bool IsAvoided(RoomKey key) => rooms.Contains(key);
    }

    private static readonly (RoomKey Room, string Command, RoomKey Gate, Direction Way)[] LeverCells =
        Levers.Select(l => (l.Room, l.Command, l.Opens, l.Way)).ToArray();

    private LeverWorld World(IRoomFilter? filter = null, bool withWalker = false) =>
        new(_root, TreetopsJson, LeverCells, filter, withWalker);

    private static bool CrossedTheVine(LeverWorld world) =>
        world.Moves.Any(m => m.From.Equals(VineGate) && m.Sent == "n");

    // The loop of the report, from the Narrow Ledge down the vine and round to the
    // rooftop and back, twice: each lap walks the lever chain, crosses the vine gate
    // once `tear bark` has opened it, and climbs back to the ledge.
    [Fact]
    public void TwoLaps_ThroughTheVineGate_EachPullsTheChainThenCrossesIt()
    {
        LeverWorld world = World();
        world.StandIn(Ledge);

        Assert.True(world.Runner.Start(LedgeToRooftop()));
        world.Play(() => world.Laps >= 2);

        Assert.Empty(world.Bonks);
        Assert.DoesNotContain(world.Events, e => e.Kind == LoopEventKind.Failed);
        Assert.Equal(2, world.Laps);
        Assert.Equal(Levers.Select(l => (l.Room, l.Command)).Concat(Levers.Select(l => (l.Room, l.Command))), world.Typed);
        Assert.Equal(2, world.Moves.Count(m => m.From.Equals(VineGate) && m.Sent == "n"));
        Assert.Equal(new[] { "turn knot", "move branch", "pull vine", "push knot", "tear bark" }, world.Runner.ExitActionCommands);
    }

    // Started in 16/565, a room the lap only reaches partway through the vine gate's
    // detour, the run doesn't join there and cross exits nothing opened: it walks
    // to a room outside the detour and works the whole chain when the lap comes to
    // it. (The two inner gates stand open from a run before.)
    [Fact]
    public void StartedInARoomInsideTheDetour_JoinsElsewhere_AndWorksTheWholeChain()
    {
        LeverWorld world = World(withWalker: true);
        world.StandIn(new RoomKey(16, 565));
        world.Open.UnionWith(new[] { (new RoomKey(16, 560), Direction.N), (new RoomKey(16, 555), Direction.W) });

        Assert.True(world.Runner.Start(LedgeToRooftop()));
        Assert.Equal(LoopState.Approaching, world.Runner.State);
        world.Play(() => CrossedTheVine(world) && world.At.Equals(Ledge));

        Assert.Empty(world.Bonks);
        Assert.DoesNotContain(world.Events, e => e.Kind == LoopEventKind.Failed);
        Assert.True(CrossedTheVine(world));
        Assert.Equal(Levers.Select(l => (l.Room, l.Command)), world.Typed.TakeLast(5));
    }

    // The lap's `tear bark` didn't take, so the vine gate is still shut when the run
    // gets there: the primed crossing is refused, and instead of a recovery that
    // would join the lap past the levers, the walker takes that one crossing,
    // working the chain again from 16/551, and the lap carries on up the vine.
    [Fact]
    public void RefusedPrimedCrossing_IsWalkedByTheWalker_AndTheLapCarriesOn()
    {
        LeverWorld world = World(withWalker: true);
        world.StandIn(Ledge);
        bool fizzled = false;
        world.Fizzle = sent => !fizzled && sent == "tear bark" && (fizzled = true);

        Assert.True(world.Runner.Start(LedgeToRooftop()));
        world.Play(() => CrossedTheVine(world) && world.At.Equals(Ledge));

        Assert.Equal(new[] { "16/551 N" }, world.Bonks);
        Assert.DoesNotContain(world.Events, e => e.Kind == LoopEventKind.Failed);
        Assert.DoesNotContain(world.Events, e => e.Detail.StartsWith("recovering", StringComparison.Ordinal));
        Assert.Equal(1, world.Moves.Count(m => m.From.Equals(Rooftop)));
        Assert.Equal(2, world.Sent.Count(s => s == "tear bark"));
        Assert.Equal(1, world.Count("tear bark"));
        Assert.True(CrossedTheVine(world));
        Assert.Equal(Ledge, world.At);
        Assert.Equal(LoopState.Running, world.Runner.State);
    }

    // A flee out of 16/569 before the game took `tear bark`: the walk back returns
    // to the lever room and the action goes out again there, rather than being
    // taken as done, so the gate is open when the lap reaches it.
    [Fact]
    public void FleeFromTheLeverRoomBeforeItsActionWasTaken_SendsTheActionAgainOnReturn()
    {
        LeverWorld world = World(withWalker: true);
        world.StandIn(Ledge);
        Assert.True(world.Runner.Start(LedgeToRooftop()));
        world.Play(() => world.Sent.Contains("tear bark"));
        Assert.Equal(new RoomKey(16, 569), world.At);
        world.DropUnanswered();

        world.Runner.PauseForFlee("low HP");
        world.Runner.SendBacktrackMove(Direction.SE);
        world.Play();
        Assert.Equal(new RoomKey(16, 568), world.At);
        world.Runner.ResumeAfterFlee(new RoomKey(16, 568));
        world.Play(() => CrossedTheVine(world) && world.At.Equals(Ledge));

        Assert.Equal(1, world.Count("tear bark"));
        Assert.Empty(world.Bonks);
        Assert.True(CrossedTheVine(world));
        Assert.DoesNotContain(world.Events, e => e.Kind == LoopEventKind.Failed);
    }

    // A shrine passage whose lever is in an alcove off the passage's own room: the
    // lap leaves for the lever from that room, so on the next lap, with the room
    // showing the passage open, the detour is skipped.
    private const string ShrineJson = """
        [
          { "Map Number": 2, "Room Number": 2, "Name": "Shrine Door", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "2/3 (Hidden/Needs 1 Actions, any order)", "S": "0", "E": "2/4", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 2, "Room Number": 3, "Name": "Sanctum", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "2/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 2, "Room Number": 4, "Name": "Alcove", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "2/2",
            "NE": "Action#1 [on the N exit of room 2/2]: press stone", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    [Fact]
    public void ExitShownOpenWhereTheDetourStarts_TheDetourIsSkipped()
    {
        RoomKey door = new(2, 2), sanctum = new(2, 3), alcove = new(2, 4);
        LeverWorld world = new(_root, ShrineJson, new[] { (alcove, "press stone", door, Direction.N) });
        world.StandIn(door);

        Assert.True(world.Runner.Start(new Loop("shrine", new[] { door, sanctum })));
        world.Play(() => world.Laps >= 2);

        Assert.Equal(2, world.Laps);
        Assert.Equal(1, world.Count("press stone"));
        Assert.Equal(1, world.Moves.Count(m => m.From.Equals(door) && m.Sent == "e"));
        Assert.Equal(2, world.Moves.Count(m => m.From.Equals(door) && m.Sent == "n"));
        Assert.Empty(world.Bonks);
    }

    // A winch in another room is pulled until it turns, as the walker pulls one,
    // before the loop walks back and through the gate it raises.
    [Fact]
    public void Lap_ThroughAGateWoundFromAnotherRoom_PullsTheWinchUntilItTurns()
    {
        const string json = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "Gatehouse", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "1/2 (Hidden/Needs 1 Actions, any order)", "S": "0", "E": "1/3", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 2, "Name": "Courtyard", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "1/1", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 3, "Name": "Winch Room", "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
                "N": "0", "S": "0", "E": "0", "W": "1/1",
                "NE": "Action#1 [on the N exit of room 1/1]: pull winch", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;
        RoomGraphManager graph = Graph(json);
        RoomTracker tracker = new(graph);
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ServerFeed server = new(router);
        using RoomDisplayParser display = new(server.Lines, tracker);
        ManualTimers winchTimers = new();
        using WinchManager winch = new(router, _ => false, winchTimers.Arm);
        LoopRunner runner = new(tracker, new MovementCoordinator(), promptScanner: new WirePromptScanner(), graph: graph,
            bfs: new BfsMapper(graph), postToUi: a => a(), schedule: new ManualTimers().Arm);
        List<string> sent = new();
        runner.SetWireSender(b => sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        winch.SetWireSender(b => sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        runner.SetWinchEnqueuer(winch.Enqueue);
        void Show(string name, string exits)
        {
            server.Line($"\u001b[1;36m{name}\u001b[0m");
            server.Line($"Obvious exits: {exits}.");
        }

        tracker.SetLocated(new RoomKey(1, 1));
        Assert.True(runner.Start(new Loop("gate", new[] { new RoomKey(1, 1), new RoomKey(1, 2) })));
        Assert.Equal(new[] { "e" }, sent);
        Show("Winch Room", "west");
        Assert.Equal(new[] { "e", "pull winch" }, sent);

        server.Line("You heave mightily on the winch, but it does not budge.");
        winchTimers.RunAll();
        Assert.Equal(new[] { "e", "pull winch", "pull winch" }, sent);

        server.Line("You heave mightily on the winch, and it begins to turn!");
        Assert.Equal(new[] { "e", "pull winch", "pull winch", "w" }, sent);
        Show("Gatehouse", "north, east");
        Assert.Equal(new[] { "e", "pull winch", "pull winch", "w", "n" }, sent);
        Show("Courtyard", "south");
        Assert.Equal(new[] { "e", "pull winch", "pull winch", "w", "n", "s" }, sent);
        Assert.Equal(LoopState.Running, runner.State);
    }
}

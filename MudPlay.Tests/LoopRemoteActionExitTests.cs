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

    // The loop's leg is the walker's walk: the same detour, the same steps.
    [Fact]
    public void Expansion_OfTheLegThroughTheGate_IsTheWalkersOwn()
    {
        RoomGraphManager graph = Graph(TreetopsJson);
        BfsMapper bfs = new(graph);

        IReadOnlyList<WalkStep> walk = RemoteActionPathExpander.Expand(
            graph, Rooftop, bfs.FindPath(Rooftop, Ledge)!, bfs);
        (IReadOnlyList<LoopStep> steps, _) =
            LoopExpander.Expand(new[] { new LoopWaypoint(Rooftop), new LoopWaypoint(Ledge) }, bfs, graph);

        string Of(object step) => step switch
        {
            MoveStep m => $"{m.Direction}{(m.SkipSpecialDispatch ? " primed" : "")}",
            MoveLoopStep m => $"{m.Direction}{(m.SkipSpecialDispatch ? " primed" : "")}",
            CommandStep c => c.Command,
            CommandLoopStep c => c.Command,
            _ => step.ToString()!,
        };
        Assert.Equal(walk.Select(Of), steps.Take(walk.Count).Select(Of));
    }

    // A lever room no route may enter leaves its gate shut: the leg through it is
    // reported unreachable when the loop is built or started, not found mid-lap.
    [Fact]
    public void Expansion_WithTheLeverRoomAvoided_ReportsTheLegThroughTheGate()
    {
        RoomGraphManager graph = Graph(TreetopsJson);
        BfsMapper bfs = new(graph);

        (_, IReadOnlyList<(RoomKey From, RoomKey To)> unreachable) =
            LoopExpander.Expand(LedgeToRooftop().Waypoints, bfs, graph, new AvoidRooms(new RoomKey(16, 569)));

        Assert.Equal(new[] { (Rooftop, Ledge) }, unreachable);
    }

    private sealed class AvoidRooms(params RoomKey[] rooms) : IRoomFilter
    {
        public bool IsAvoided(RoomKey key) => rooms.Contains(key);
    }

    // The game side of a run: the rooms shown as the game shows them, the lever exits
    // shut until their action is typed, each command answered by its prompt.
    private sealed class Treetops(RoomGraphManager graph, RoomTracker tracker, LoopRunner runner, ServerFeed server,
        WirePromptScanner prompts)
    {
        public LoopRunner Runner { get; } = runner;
        public RoomTracker Tracker { get; } = tracker;
        public List<string> Sent { get; } = new();
        public List<LoopEvent> Events { get; } = new();
        public List<(RoomKey Room, string Command)> Typed { get; } = new();
        public List<(RoomKey From, string Sent)> Moves { get; } = new();
        public List<string> Bonks { get; } = new();
        public RoomKey At { get; set; }
        private readonly HashSet<(RoomKey, Direction)> _open = new();
        private int _answered;

        public void Show(RoomKey key)
        {
            Room room = graph.GetRoom(key)!;
            IEnumerable<string> exits = room.Exits
                .Where(e => e.Key.IsCardinal() && e.Value.Hint != RoomExitHint.Text
                            && (e.Value.Hint != RoomExitHint.MultiActionHidden || _open.Contains((key, e.Key))))
                .Select(e => e.Key.ToLongName());
            server.Line($"\u001b[1;36m{room.Name}\u001b[0m");
            server.Line($"Obvious exits: {string.Join(", ", exits)}.");
        }

        // Answers what the client sent, one line at a time, until the lap is round
        // or the run stops.
        public void Play(int limit = 400)
        {
            while (_answered < Sent.Count && limit-- > 0
                   && Runner.State == LoopState.Running && !Events.Any(e => e.Kind == LoopEventKind.RepeatStarted))
            {
                string sent = Sent[_answered++];
                // The game echoes what it takes on the prompt row it was typed at.
                server.Line("[HP=565]:" + sent);
                Room room = graph.GetRoom(At)!;
                RoomExit? crossed = null;
                if (DirectionExtensions.TryFromToken(sent, out Direction d) && room.Exits.TryGetValue(d, out RoomExit cardinal))
                {
                    crossed = cardinal;
                    if (cardinal.Hint == RoomExitHint.MultiActionHidden && !_open.Contains((At, d)))
                    {
                        Bonks.Add($"{At} {d}");
                        return;
                    }
                }
                else if (room.Exits.Values.FirstOrDefault(e => e.Hint == RoomExitHint.Text && e.TextCommands!.Contains(sent))
                         is { Target.Map: > 0 } text)
                    crossed = text;

                if (crossed is { } exit)
                {
                    Moves.Add((At, sent));
                    At = exit.Target;
                    Show(At);
                    continue;
                }
                Typed.Add((At, sent));
                foreach ((RoomKey lever, string command, RoomKey opens, Direction way) in Levers)
                    if (lever.Equals(At) && command == sent) _open.Add((opens, way));
                prompts.Append(Encoding.Latin1.GetBytes("[HP=565]: "));
            }
        }
    }

    private Treetops NewRun()
    {
        RoomGraphManager graph = Graph(TreetopsJson);
        RoomTracker tracker = new(graph);
        WirePromptScanner prompts = new();
        LoopRunner runner = new(tracker, new MovementCoordinator(), promptScanner: prompts, graph: graph,
            bfs: new BfsMapper(graph), postToUi: a => a(), schedule: new ManualTimers().Arm);
        ServerFeed server = new();
        _ = new RoomDisplayParser(server.Lines, tracker);
        // Its echo tells a move into one Treetops from a re-show of the last.
        _ = new InboundMoveEchoScanner(server.Lines, tracker);
        Treetops run = new(graph, tracker, runner, server, prompts);
        runner.SetWireSender(b => run.Sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        runner.Event += run.Events.Add;
        return run;
    }

    // The loop of the report, from the Narrow Ledge down the vine and round to the
    // rooftop and back: the lap walks the lever chain, crosses the vine gate once
    // `tear bark` has opened it, and climbs back to the ledge, where the next lap starts.
    [Fact]
    public void Lap_ThroughTheVineGate_PullsTheChainThenCrossesIt_AndComesRound()
    {
        Treetops run = NewRun();
        run.Tracker.SetLocated(Ledge);
        run.At = Ledge;

        Assert.True(run.Runner.Start(LedgeToRooftop()));
        run.Play();

        Assert.Empty(run.Bonks);
        Assert.DoesNotContain(run.Events, e => e.Kind == LoopEventKind.Failed);
        Assert.Equal(Levers.Select(l => (l.Room, l.Command)), run.Typed);
        int bark = run.Sent.IndexOf("tear bark");
        int cross = run.Moves.FindIndex(m => m.From.Equals(VineGate) && m.Sent == "n");
        Assert.True(cross >= 0, "the vine gate was never crossed");
        Assert.True(run.Sent.IndexOf("n", bark) > bark);
        Assert.Equal((new RoomKey(16, 552), "climb vine"), run.Moves[^1]);
        Assert.Equal(Ledge, run.At);
        Assert.Contains(run.Events, e => e.Kind == LoopEventKind.RepeatStarted);
        Assert.Equal(new[] { "turn knot", "move branch", "pull vine", "push knot", "tear bark" }, run.Runner.ExitActionCommands);
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

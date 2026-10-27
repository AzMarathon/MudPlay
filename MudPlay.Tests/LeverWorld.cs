using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Tests;

// Test infrastructure: the game side of a walk or loop through lever exits. The
// rooms are shown as the game shows them, through a ServerFeed read by the
// client's RoomDisplayParser, InboundMoveEchoScanner and MovementRefusalDetector;
// every line the client sends is echoed on the prompt row it was typed at. A
// lever exit is shut until a lever of it is typed: a hidden one is then listed,
// a door or gate shows `open gate <dir>` (`closed gate <dir>` while shut), and an
// action on a gate standing open shuts it again, as the engine's toggle does
// (GAME_MECHANICS "Multi-action exits and levers"). A move into a shut lever exit
// is refused, and each command is answered by a prompt. The held-open doors
// (DoorsHeldOpen) are wired to the tracker and the refusals as the client wires them.
internal sealed class LeverWorld
{
    private readonly Dictionary<(RoomKey Room, string Command), (RoomKey Room, Direction Dir)> _levers = new();
    private int _answered;

    public LeverWorld(string root, string roomsJson,
        IEnumerable<(RoomKey Room, string Command, RoomKey Gate, Direction Way)> levers,
        IRoomFilter? filter = null, bool withWalker = false)
    {
        string set = Path.Combine(root, "alpha");
        Directory.CreateDirectory(set);
        File.WriteAllText(Path.Combine(set, "Rooms.json"), roomsJson);
        GameDataCache cache = new(root);
        cache.SwitchSet("alpha");
        Graph = new RoomGraphManager(cache);
        Graph.OnActiveSetChanged("alpha");
        Tracker = new RoomTracker(Graph);
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        Server = new ServerFeed(router);
        _ = new RoomDisplayParser(Server.Lines, Tracker);
        // Its echo tells a move into one Treetops from a re-show of the last.
        _ = new InboundMoveEchoScanner(Server.Lines, Tracker);
        MovementRefusalDetector refusals = new(Server.Lines, Tracker);
        Prompts = new WirePromptScanner();
        BfsMapper bfs = new(Graph);
        MovementCoordinator coordinator = new();
        Coordinator = coordinator;
        Walker = withWalker ? new AutoWalkManager(Graph, bfs, Tracker, coordinator, filter, promptScanner: Prompts) : null;
        Runner = new LoopRunner(Tracker, coordinator, promptScanner: Prompts, graph: Graph, bfs: bfs, walker: Walker,
            filter: filter, postToUi: a => a(), now: () => Clock.Now, schedule: Timers.Arm);
        // Wired as AppServices wires it.
        Held = new DoorsHeldOpen(
            (room, dir) => Graph.GetRoom(room) is { } r && r.Exits.TryGetValue(dir, out RoomExit e) ? e : null, () => Clock.Now);
        Held.ReleaseOn(refusals);
        // Two seconds a move, so a hold's last stretch is weighed against the walk.
        Held.TravelTime = moves => TimeSpan.FromSeconds(2 * moves);
        Tracker.RoomShown += Held.NoteRoomShown;
        Runner.SetDoorsHeldOpen(Held);
        Runner.SetWireSender(b => Sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        Runner.Event += Events.Add;
        if (Walker is not null)
        {
            Walker.SetDoorsHeldOpen(Held);
            Walker.SetWireSender(b => Sent.Add(Encoding.Latin1.GetString(b).TrimEnd('\r')));
        }
        foreach ((RoomKey room, string command, RoomKey gate, Direction way) in levers)
            _levers[(room, command)] = (gate, way);
    }

    public RoomGraphManager Graph { get; }
    public RoomTracker Tracker { get; }
    public LoopRunner Runner { get; }
    public AutoWalkManager? Walker { get; }
    public MovementCoordinator Coordinator { get; }
    public DoorsHeldOpen Held { get; }
    public ServerFeed Server { get; }
    public WirePromptScanner Prompts { get; }
    public ManualClock Clock { get; } = new();
    public ManualTimers Timers { get; } = new();
    public List<string> Sent { get; } = new();
    public List<LoopEvent> Events { get; } = new();
    public List<(RoomKey Room, string Command)> Typed { get; } = new();
    public List<(RoomKey From, string Sent)> Moves { get; } = new();
    public List<string> Bonks { get; } = new();
    public HashSet<(RoomKey Room, Direction Dir)> Open { get; } = new();
    public RoomKey At { get; set; }

    // A command the game answers without its action taking effect, asked as each
    // line is answered.
    public Func<string, bool>? Fizzle { get; set; }

    // Told each line before the game answers it, standing where it was sent from:
    // a test shuts a gate under the client's feet this way.
    public Action<string>? BeforeAnswer { get; set; }

    // Everything sent and not answered yet never reached the game.
    public void DropUnanswered() => _answered = Sent.Count;

    public int Laps => Events.Count(e => e.Kind == LoopEventKind.RepeatStarted);

    public int Count(string command) => Typed.Count(t => t.Command == command);

    public void StandIn(RoomKey room)
    {
        Tracker.SetLocated(room);
        At = room;
    }

    public void Show(RoomKey key)
    {
        Room room = Graph.GetRoom(key)!;
        List<string> exits = new();
        foreach ((Direction dir, RoomExit exit) in room.Exits)
        {
            if (!dir.IsCardinal() || exit.Hint == RoomExitHint.Text) continue;
            bool lever = exit.Hint == RoomExitHint.MultiActionHidden;
            bool open = Open.Contains((key, dir));
            if (lever && exit.MultiAction?.DoorOrGate is not null)
                exits.Add($"{(open ? "open" : "closed")} gate {dir.ToLongName()}");
            else if (!lever || open)
                exits.Add(dir.ToLongName());
        }
        Server.Line($"\u001b[1;36m{room.Name}\u001b[0m");
        Server.Line($"Obvious exits: {string.Join(", ", exits)}.");
    }

    // Answers what the client sent, one line at a time, until done says so or
    // nothing is left to answer.
    public void Play(Func<bool>? done = null, int limit = 600)
    {
        while (_answered < Sent.Count && limit-- > 0 && done?.Invoke() != true)
        {
            string sent = Sent[_answered++];
            BeforeAnswer?.Invoke(sent);
            Server.Line("[HP=565]:" + sent);
            if (Fizzle?.Invoke(sent) == true)
            {
                Prompts.Append(Encoding.Latin1.GetBytes("[HP=565]: "));
                continue;
            }
            Room room = Graph.GetRoom(At)!;
            RoomExit? crossed = null;
            if (DirectionExtensions.TryFromToken(sent, out Direction d) && room.Exits.TryGetValue(d, out RoomExit cardinal))
            {
                if (cardinal.Hint == RoomExitHint.MultiActionHidden && !Open.Contains((At, d)))
                {
                    Bonks.Add($"{At} {d}");
                    Server.Line(cardinal.MultiAction?.DoorOrGate is not null ? "The gate is closed!" : "There is no exit in that direction!");
                    continue;
                }
                crossed = cardinal;
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
            if (_levers.TryGetValue((At, sent), out (RoomKey Room, Direction Dir) gate))
            {
                bool door = Graph.GetRoom(gate.Room)!.Exits[gate.Dir].MultiAction?.DoorOrGate is not null;
                if (door && Open.Contains(gate)) Open.Remove(gate);
                else Open.Add(gate);
            }
            Prompts.Append(Encoding.Latin1.GetBytes("[HP=565]: "));
        }
    }
}

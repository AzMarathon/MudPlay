using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// The Dark-Elf Castle gatehouse wheel (report paradigm-20261026-150710): 8/560's N
// gate to the Moatside is a 1000-picklocks door the room's command works, `turn
// wheel` (CMD 4100, `remoteaction 560 0 0 0`), and the set also writes that command
// out as the D slot's action cell for the N exit. The walker, the tracker and the
// recovery gate run on TrackerWireRig, the game answering in the report's lines.
public sealed class WheelGateWalkTests
{
    private const string RoomsJson = """
        [
          { "Map Number": 8, "Room Number": 559, "Name": "Dark-Elf City, Moatside",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "CMD": 0,
            "N": "0", "S": "8/560 (Door [1000 picklocks/strength])", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 8, "Room Number": 560, "Name": "Dark-Elf Castle, Gatehouse",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "CMD": 4100,
            "N": "8/559 (Door [1000 picklocks/strength])", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0",
            "D": "Action [on the N exit of this room]: turn wheel, push bar, move wheel" }
        ]
        """;

    private const string TbInfoJson = """
        [ { "Number": 4100, "LinkTo": 0,
            "Action": "turn wheel:message 1400:message 3211:remoteaction 560 0 0 0\npush bar:message 1400:message 3211:remoteaction 560 0 0 0\nmove wheel:message 1400:message 3211:remoteaction 560 0 0 0\n",
            "Called From": "Room 8/560" } ]
        """;

    private const string WheelLine = "You turn the great wheel with a mighty effort, and the drawbridge opens!";
    private const string GateClosed = "The gate is Closed!";

    private static readonly RoomKey Moatside = new(8, 559);
    private static readonly RoomKey Gatehouse = new(8, 560);

    // The gate as the game keeps it. A test's turn says what a turn of the wheel does
    // to it: in the report every pair of turns left it shut.
    private sealed class Gate
    {
        public bool Open { get; set; }
    }

    private static TrackerWireRig NewRig(Gate gate, Func<bool, bool> turn, List<WalkEvent> events)
    {
        TrackerWireRig rig = new(RoomsJson, prompt: "[HP=556]:", tbinfoJson: TbInfoJson) { Position = Gatehouse };
        rig.GameAnswer = command =>
        {
            if (rig.Position == Gatehouse && command == "turn wheel")
            {
                gate.Open = turn(gate.Open);
                return WheelLine;
            }
            return rig.Position == Gatehouse && command == "n" && !gate.Open ? GateClosed : null;
        };
        rig.ExitWords = (room, dir) =>
            (room, dir) == (Gatehouse, Direction.N) ? (gate.Open ? "open gate north" : "closed gate north")
            : (room, dir) == (Moatside, Direction.S) ? (gate.Open ? "open gate south" : "closed gate south")
            : null;
        rig.Walker.Event += events.Add;
        rig.Tracker.SetLocated(Gatehouse);
        rig.ShowRoom();
        return rig;
    }

    [Fact]
    public void TheWheel_IsTurnedOnce_AndTheWalkGoesThroughTheGate()
    {
        Gate gate = new();
        List<WalkEvent> events = new();
        using TrackerWireRig rig = NewRig(gate, open => !open, events);

        Assert.True(rig.Walker.WalkTo(Moatside));
        rig.Pump();

        // Twice, the second turn shut the gate the first had opened, and `n` met
        // "The gate is Closed!" on every try.
        Assert.Equal(new[] { "turn wheel", "n" }, rig.Sent);
        Assert.Equal(Moatside, rig.Position);
        Assert.Equal(Moatside, rig.Believed);
        Assert.Contains(events, e => e.Kind == WalkEventKind.Finished);
    }

    [Fact]
    public void AGateTheWheelNeverOpens_StopsTheWalk_NamingTheWay_OneTurnPerTry()
    {
        Gate gate = new();
        List<WalkEvent> events = new();
        using TrackerWireRig rig = NewRig(gate, _ => false, events);

        Assert.True(rig.Walker.WalkTo(Moatside));
        rig.Pump();

        Assert.Equal(WalkState.Idle, rig.Walker.State);
        Assert.Equal(Gatehouse, rig.Position);
        WalkEvent failed = Assert.Single(events, e => e.Kind == WalkEventKind.Failed);
        Assert.Contains("N from 8/560", failed.Detail);
        Assert.Contains("refused on every try and every re-plan", failed.Detail);

        // Each try is one turn and one move: the first, its retry, and the retry of
        // each of the two re-plans the walk is allowed.
        int turns = rig.Sent.Count(c => c == "turn wheel");
        Assert.Equal(rig.Sent.Count(c => c == "n"), turns);
        Assert.Equal(6, turns);
        for (int i = 0; i < rig.Sent.Count; i += 2)
            Assert.Equal(new[] { "turn wheel", "n" }, rig.Sent.Skip(i).Take(2));
    }
}

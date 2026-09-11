using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;
using Xunit.Abstractions;

namespace MudPlay.Tests;

// Reproduction for the "LostWays Lockers" fragility (issue #478): looping the
// 4x5 grid of identically-named "Soldier's Quarters" (map 8, around 1703) in
// lostwaysrealm2 reliably drives RoomTracker to Lost.
//
// The grid (all 20 rooms share the name; only the exit-set distinguishes them,
// and even that collapses — the 6 interior rooms are all 4-way N/S/E/W):
//
//         col0   col1   col2   col3
//   row0: 1703 — 1702 — 1701 — 1699
//          |      |      |      |
//   row1: 1704 — 1708 — 1712 — 1716
//          |      |      |      |
//   row2: 1705 — 1709 — 1713 — 1717
//          |      |      |      |
//   row3: 1706 — 1710 — 1714 — 1718
//          |      |      |      |
//   row4: 1707 — 1711 — 1715 — 1719
//
// Root cause: a sent move only flips the tracker to Pending; position advances
// when a room display matches the predicted target (name + subset exits). There
// is no move<->display correlation token, so a stray re-display of the room the
// player is STILL in (a passing mob re-rendering the room, a look, a combat
// redraw) also matches the predicted target in a homogeneous grid. The only
// guard is a 400ms timing floor (RoomTracker.AmbiguousRedisplayFloor). A
// re-display that lands at normal server latency (>400ms) slips past it and
// phantom-advances the tracker one room ahead of reality — invisibly, because
// every room reads the same name — until the predicted room's exits stop
// matching and it bails to Lost.
public sealed class SoldiersQuartersGridLostRepro : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root;

    public SoldiersQuartersGridLostRepro(ITestOutputHelper output)
    {
        _out = output;
        _root = Path.Combine(Path.GetTempPath(), "mudplay-soldiers-grid-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // The real lostwaysrealm2 map-8 grid around 1703, extracted verbatim.
    private const string GridJson = """
        [
        {"Map Number":8,"Room Number":1703,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"0","S":"8/1704","E":"8/1702","W":"0","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1702,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"0","S":"8/1708","E":"8/1701","W":"8/1703","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1701,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"0","S":"8/1712","E":"8/1699","W":"8/1702","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1699,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"0","S":"8/1716","E":"0","W":"8/1701","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1704,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1703","S":"8/1705","E":"8/1708","W":"0","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1708,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1702","S":"8/1709","E":"8/1712","W":"8/1704","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1712,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1701","S":"8/1713","E":"8/1716","W":"8/1708","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1716,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1699","S":"8/1717","E":"0","W":"8/1712","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1705,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1704","S":"8/1706","E":"8/1709","W":"0","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1709,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1708","S":"8/1710","E":"8/1713","W":"8/1705","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1713,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1712","S":"8/1714","E":"8/1717","W":"8/1709","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1717,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1716","S":"8/1718","E":"0","W":"8/1713","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1706,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1705","S":"8/1707","E":"8/1710","W":"0","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1710,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1709","S":"8/1711","E":"8/1714","W":"8/1706","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1714,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1713","S":"8/1715","E":"8/1718","W":"8/1710","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1718,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1717","S":"8/1719","E":"0","W":"8/1714","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1707,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1706","S":"0","E":"8/1711","W":"0","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1711,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1710","S":"0","E":"8/1715","W":"8/1707","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1715,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1714","S":"0","E":"8/1719","W":"8/1711","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"},
        {"Map Number":8,"Room Number":1719,"Name":"Soldier's Quarters","Light":0,"Shop":0,"Lair":"","Delay":0,"N":"8/1718","S":"0","E":"0","W":"8/1715","NE":"0","NW":"0","SE":"0","SW":"0","U":"0","D":"0"}
        ]
        """;

    private const string Name = "Soldier's Quarters";

    private RoomTracker NewTracker()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GridJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        return new RoomTracker(graph);
    }

    // Real grid adjacency (room -> direction -> neighbour), so the simulator can
    // move the "true" position exactly as the game would.
    private static readonly Dictionary<int, Dictionary<Direction, int>> Adj = BuildAdjacency();

    private static Dictionary<int, Dictionary<Direction, int>> BuildAdjacency()
    {
        // Parsed straight from the grid above.
        int[,] rows =
        {
            {1703,1702,1701,1699},
            {1704,1708,1712,1716},
            {1705,1709,1713,1717},
            {1706,1710,1714,1718},
            {1707,1711,1715,1719},
        };
        var adj = new Dictionary<int, Dictionary<Direction, int>>();
        for (int r = 0; r < 5; r++)
            for (int c = 0; c < 4; c++)
            {
                var m = new Dictionary<Direction, int>();
                if (r > 0) m[Direction.N] = rows[r - 1, c];
                if (r < 4) m[Direction.S] = rows[r + 1, c];
                if (c < 3) m[Direction.E] = rows[r, c + 1];
                if (c > 0) m[Direction.W] = rows[r, c - 1];
                adj[rows[r, c]] = m;
            }
        return adj;
    }

    private static HashSet<Direction> ExitsOf(int room) => new(Adj[room].Keys);

    private static RoomObservation Obs(int room) => new(Name, ExitsOf(room));

    // ---------------------------------------------------------------------
    // Test A — the isolated root defect: a stray re-display of the room the
    // player is STILL in, arriving after the 400ms floor, phantom-advances the
    // tracker to the predicted target. (Under the floor it correctly stays put.)
    // ---------------------------------------------------------------------
    [Fact]
    public void SlowSourceRedisplay_PhantomAdvancesTrackerWithoutMoving()
    {
        RoomTracker tracker = NewTracker();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Standing, Confirmed, in interior room 1708 (4-way N/S/E/W).
        tracker.SetLocated(new RoomKey(8, 1708), t0);
        tracker.NoteRoomObserved(Obs(1708), t0);
        Assert.Equal(new RoomKey(8, 1708), tracker.State.CurrentRoom?.Key);

        // Send "s" (predicted target 1709, also interior 4-way). Pending.
        tracker.NoteMoveSent(Direction.S, t0.AddSeconds(1));

        // A passing mob re-renders room 1708 — the room we're STILL in — 700ms
        // later, well past the 400ms floor. Same name, same N/S/E/W exits as the
        // predicted target 1709, so it matches the prediction.
        tracker.NoteRoomObserved(Obs(1708), t0.AddSeconds(1).AddMilliseconds(700));

        // BUG: tracker now believes it arrived at 1709, though the character
        // never left 1708.
        _out.WriteLine($"after slow source re-display: tracker believes {tracker.State.CurrentRoom?.Key}, confidence {tracker.State.Confidence}");
        Assert.Equal(new RoomKey(8, 1709), tracker.State.CurrentRoom?.Key);
    }

    [Fact]
    public void FastSourceRedisplay_CorrectlyStaysPut()
    {
        RoomTracker tracker = NewTracker();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        tracker.SetLocated(new RoomKey(8, 1708), t0);
        tracker.NoteRoomObserved(Obs(1708), t0);
        tracker.NoteMoveSent(Direction.S, t0.AddSeconds(1));

        // Same stray re-display, but 100ms < the 400ms floor — the guard catches it.
        tracker.NoteRoomObserved(Obs(1708), t0.AddSeconds(1).AddMilliseconds(100));

        Assert.Equal(new RoomKey(8, 1708), tracker.State.CurrentRoom?.Key);
    }

    // ---------------------------------------------------------------------
    // Test B — the full failure. A lone stray re-display self-heals (commands
    // still equal real moves), so permanent drift needs a NET miscount: a move
    // the client counts but the character never makes. Roomba's fast command
    // bursts routinely trip the game's typing-rate limiter ("You are typing too
    // quickly - command ignored"), which drops the command silently — the client
    // counted it, the character didn't move. Combined with a stray re-display to
    // phantom-confirm the dropped move, the tracker is permanently one room ahead,
    // and in a homogeneous grid it stays invisibly wrong until a turn in the path
    // contradicts its prediction and it bails to Lost.
    //
    // The snake: ssssennnnessssennnnwww (boustrophedon over all 20 rooms). The
    // client SENDS every move; the character only MAKES the ones the game didn't
    // drop.
    [Fact]
    public void SnakeLoop_DroppedMovePlusStrayRedisplay_GoesLost()
    {
        RoomTracker tracker = NewTracker();
        var clock = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        int realPos = 1703;
        tracker.SetLocated(new RoomKey(8, realPos), clock);
        tracker.NoteRoomObserved(Obs(realPos), clock);

        Direction[] moves = ParseMoves("ssssennnnessssennnnwww");
        const int dropStep = 6;   // the game drops this command (rate limiter)

        for (int i = 0; i < moves.Length; i++)
        {
            Direction d = moves[i];
            clock = clock.AddSeconds(2);
            tracker.NoteMoveSent(d, clock);   // client always counts the send

            if (i == dropStep)
            {
                // Command dropped by the typing-rate limiter: no real move, no
                // arrival display. A passing mob then re-renders the room we're
                // STILL in, 600ms later (> the 400ms floor) — phantom-confirming
                // the move that never happened.
                tracker.NoteRoomObserved(Obs(realPos), clock.AddMilliseconds(600));
                continue;
            }

            // Ordinary move: the character actually moves (if the exit exists) and
            // the landing room renders a round-trip later.
            if (Adj[realPos].TryGetValue(d, out int next))
            {
                realPos = next;
                tracker.NoteRoomObserved(Obs(realPos), clock.AddMilliseconds(1500));
            }

            if (tracker.State.Confidence == RoomConfidence.Lost) break;
        }

        RoomKey? believed = tracker.State.CurrentRoom?.Key;
        _out.WriteLine($"real position 8/{realPos}, tracker believes {believed}, confidence {tracker.State.Confidence}");

        // One dropped command + one stray re-display is enough to strand the loop:
        // the tracker ends either Lost or confidently wrong about where it is.
        bool desynced = tracker.State.Confidence != RoomConfidence.Confirmed
                        || believed != new RoomKey(8, realPos);
        Assert.True(desynced,
            $"expected the dropped-move + stray re-display to desync the tracker; " +
            $"instead it stayed in sync at 8/{realPos}");
    }

    private static Direction[] ParseMoves(string s) =>
        s.Select(c => c switch
        {
            's' => Direction.S,
            'n' => Direction.N,
            'e' => Direction.E,
            'w' => Direction.W,
            _ => throw new ArgumentException($"bad move '{c}'"),
        }).ToArray();
}

using System;
using System.IO;
using System.Text.RegularExpressions;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Verifies the bridge from LineExtractor's prompt/content split to
// RoomTracker.NoteInboundMoveEcho — the wire-format-dependent bit. The end-to-end
// echo-gate behaviour itself is covered by RoomTrackerTests /
// SoldiersQuartersGridLostRepro with echoes fed directly; here we prove the
// scanner forwards the right line (a prompt-split command echo) and ignores the
// wrong one (a room display following a bare prompt).
public sealed class InboundMoveEchoScannerTests : IDisposable
{
    private readonly string _root;

    public InboundMoveEchoScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-echo-scanner-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // Three identically-named "Hall" rooms in an N/S cycle, each {N,S}: a move
    // confirms by predicted-neighbour (identical name + exits), so its landing is
    // echo-gated — exactly the case the scanner must feed.
    private const string GraphJson = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Hall",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/2", "S": "1/3", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2, "Name": "Hall",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/3", "S": "1/1", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 3, "Name": "Hall",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "1/1", "S": "1/2", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private (RoomTracker Tracker, InboundMoveEchoScanner Scanner) NewScanner(Regex? promptPattern = null)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        RoomTracker tracker = new(graph);
        LineExtractor lines = new(new TerminalEmulator(80, 25));
        InboundMoveEchoScanner scanner = promptPattern is null
            ? new(lines, tracker)
            : new(lines, tracker, () => promptPattern);
        return (tracker, scanner);
    }

    private static LineExtractor.EmittedLine Line(string text, DateTimeOffset at, bool prompt) =>
        new(text, Array.Empty<CellAttributes>(), at, prompt);

    private static RoomObservation Hall() =>
        new("Hall", new HashSet<Direction> { Direction.N, Direction.S });

    // A prompt-split command echo (prompt line + trailing content sharing its
    // timestamp) is forwarded, so the move's same-named-neighbour landing confirms.
    [Fact]
    public void PromptSplitEcho_ConfirmsMove()
    {
        (RoomTracker tracker, InboundMoveEchoScanner scanner) = NewScanner();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        tracker.SetLocated(new RoomKey(1, 1), t0);
        tracker.NoteRoomObserved(Hall(), t0);
        tracker.NoteMoveSent(Direction.N, t0.AddSeconds(1));  // predicted 1/2 (identical {N,S})

        // The split pair: prompt half then "n", both with the same row timestamp.
        DateTimeOffset echoAt = t0.AddSeconds(2);
        scanner.FeedTestLine(Line("[HP=100/MA=50]:", echoAt, prompt: true));
        scanner.FeedTestLine(Line("n", echoAt, prompt: false));

        tracker.NoteRoomObserved(Hall(), echoAt.AddMilliseconds(100));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 2), tracker.State.CurrentRoom!.Key);
    }

    // A room display following a BARE prompt carries a later timestamp (a separate
    // row), so the scanner does NOT forward it as an echo — the tracker records no
    // move-echo from it.
    [Fact]
    public void BarePromptThenRoomLine_NotTreatedAsEcho()
    {
        (RoomTracker tracker, InboundMoveEchoScanner scanner) = NewScanner();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        tracker.SetLocated(new RoomKey(1, 1), t0);
        tracker.NoteRoomObserved(Hall(), t0);
        tracker.NoteMoveSent(Direction.N, t0.AddSeconds(1));

        // Bare prompt, then the room name on a later row (different timestamp).
        scanner.FeedTestLine(Line("[HP=100/MA=50]:", t0.AddSeconds(2), prompt: true));
        scanner.FeedTestLine(Line("Hall", t0.AddSeconds(2).AddMilliseconds(40), prompt: false));

        // Nothing was forwarded as an echo — the room line is not a command echo.
        Assert.Null(tracker.LastInboundMoveEcho);
    }

    // A CUSTOM statline LineExtractor's built-in "[HP=..]:" split doesn't recognise:
    // the whole prompt + typed command arrive as ONE non-prompt line. Given the
    // user's configured statline matcher, the scanner still lifts the trailing
    // command (path B) — so the move's same-named-neighbour landing confirms.
    [Fact]
    public void CustomStatlinePrompt_UnsplitLine_ConfirmsMove()
    {
        // A user-authored prompt shape "<hp/mana>:" the default [HP=..]: split misses.
        Regex custom = new(@"<\d+/\d+>:", RegexOptions.CultureInvariant);
        (RoomTracker tracker, InboundMoveEchoScanner scanner) = NewScanner(custom);
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        tracker.SetLocated(new RoomKey(1, 1), t0);
        tracker.NoteRoomObserved(Hall(), t0);
        tracker.NoteMoveSent(Direction.N, t0.AddSeconds(1));  // predicted 1/2 (identical {N,S})

        // The whole row as one non-prompt line: prompt + the echoed "n".
        DateTimeOffset echoAt = t0.AddSeconds(2);
        scanner.FeedTestLine(Line("<100/50>:n", echoAt, prompt: false));

        tracker.NoteRoomObserved(Hall(), echoAt.AddMilliseconds(100));

        Assert.Equal(RoomConfidence.Confirmed, tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 2), tracker.State.CurrentRoom!.Key);
    }
}

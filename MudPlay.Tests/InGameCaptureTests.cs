using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MudPlay.Game;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// A bug report copies the terminal only from the time spent in the game, so the
// board's login screen (the account name as typed) can't ride along in one. The
// terminal and the backscroll still hold it; the filter is on what a report takes.
public sealed class InGameCaptureTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private static void WriteRow(TerminalScreen screen, int y, string text, DateTimeOffset stamp)
    {
        screen.FeedTimestamp = stamp;
        for (int x = 0; x < text.Length && x < screen.Cols; x++)
            screen.Put(x, y, new Cell(text[x], default));
    }

    private static string[] Text(IReadOnlyList<TranscriptSnapshot.Line> lines) =>
        lines.Select(l => l.Text).Where(t => t.Length > 0).ToArray();

    [Fact]
    public void Window_CoversARowByWhenItWasWritten()
    {
        CaptureWindow window = new(on: false);
        Assert.False(window.Covers(T0));

        window.Set(true, T0.AddSeconds(10));
        window.Set(false, T0.AddSeconds(20));

        Assert.False(window.Covers(T0.AddSeconds(9)));
        Assert.True(window.Covers(T0.AddSeconds(10)));    // the row that carried the prompt
        Assert.True(window.Covers(T0.AddSeconds(19)));
        Assert.False(window.Covers(T0.AddSeconds(20)));
        Assert.False(window.IsOn);
    }

    // The login screen is still on the terminal when the game is entered, and it
    // scrolls into the backscroll afterwards. Neither copy reaches a report.
    [Fact]
    public void Tail_LeavesOutRowsWrittenBeforeTheGame_OnScreenOrScrolledOff()
    {
        TerminalEmulator emulator = new(80, 5);
        TerminalScreen screen = emulator.Screen;
        CaptureWindow inGame = new(on: false);

        WriteRow(screen, 0, "Enter your user-ID: bobthegreat", T0);
        WriteRow(screen, 1, "Password: ******", T0.AddSeconds(1));
        inGame.Set(true, T0.AddSeconds(5));
        WriteRow(screen, 2, "Town Square", T0.AddSeconds(5));
        WriteRow(screen, 3, "[HP=100/MA=50]:", T0.AddSeconds(5));

        // Still on screen: the terminal shows it all, the report's view doesn't.
        Assert.Equal(4, Text(TranscriptSnapshot.Tail(emulator, 0)).Length);
        Assert.Equal(new[] { "Town Square", "[HP=100/MA=50]:" }, Text(TranscriptSnapshot.Tail(emulator, 0, only: inGame)));

        // Scrolled off: the backscroll keeps the login rows, the report's view doesn't.
        screen.ClearAll(default);
        WriteRow(screen, 0, "You see nothing special.", T0.AddSeconds(8));
        Assert.Contains("Enter your user-ID: bobthegreat", Text(TranscriptSnapshot.Tail(emulator, 0)));
        Assert.Equal(
            new[] { "Town Square", "[HP=100/MA=50]:", "You see nothing special." },
            Text(TranscriptSnapshot.Tail(emulator, 0, only: inGame)));
    }

    [Fact]
    public void Tail_LeavesOutWhatIsWrittenAfterLeavingForTheMenus()
    {
        TerminalEmulator emulator = new(80, 5);
        TerminalScreen screen = emulator.Screen;
        CaptureWindow inGame = new(on: false);
        inGame.Set(true, T0);

        WriteRow(screen, 0, "Your character has been saved.", T0.AddSeconds(1));
        inGame.Set(false, T0.AddSeconds(2));
        WriteRow(screen, 1, "[A] . Account settings", T0.AddSeconds(3));

        Assert.Equal(new[] { "Your character has been saved." }, Text(TranscriptSnapshot.Tail(emulator, 0, only: inGame)));
    }

    private static byte[] Bytes(string s) => Encoding.Latin1.GetBytes(s);

    [Fact]
    public void Gate_OpensOnAGamePrompt_AndClosesOnExitMenuOrDrop()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        WirePromptScanner scanner = new();
        WireBuffer wire = new();
        using InGameCapture gate = new(router, scanner, wire);
        void Line(string text) => router.Dispatch(
            new LineExtractor.EmittedLine(text, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));

        // The board's login: not in the game, and no wire a report may take.
        wire.Append(Bytes("Enter your user-ID: bobthegreat\r\n"));
        Assert.False(gate.InGame);
        Assert.Null(gate.WireMark);

        scanner.Append(Bytes("[HP=100/MA=50]:"));
        Assert.True(gate.InGame);
        Assert.Equal(wire.TotalBytes, gate.WireMark);

        Line("Your character has been saved.  Please leave any comments in E-mail to Sysop.");
        Assert.False(gate.InGame);
        Assert.Null(gate.WireMark);

        scanner.Append(Bytes("[HP=100/MA=50]:"));
        Assert.True(gate.InGame);
        Line("[E] . Enter the Realm");
        Assert.False(gate.InGame);

        scanner.Append(Bytes("[HP=100/MA=50]:"));
        gate.NotifyDisconnected();
        Assert.False(gate.InGame);
    }
}

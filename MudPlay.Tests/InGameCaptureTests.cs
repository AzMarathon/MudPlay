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

    private sealed class Rig : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public WirePromptScanner Scanner { get; } = new();
        public WireBuffer Wire { get; } = new();
        public InGameCapture Gate { get; }
        public List<bool> MenuChanges { get; } = new();

        public Rig()
        {
            DefaultPatterns.Seed(Router);
            Gate = new InGameCapture(Router, Scanner, Wire);
            Gate.AtBoardMenuChanged += MenuChanges.Add;
        }

        public void Prompt() => Scanner.Append(Bytes("[HP=100/MA=50]:"));
        public void Line(string text) => Router.Dispatch(
            new LineExtractor.EmittedLine(text, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));
        public void Dispose() => Gate.Dispose();
    }

    [Fact]
    public void Gate_OpensOnAGamePrompt_AndClosesOnExitMenuOrDrop()
    {
        using Rig rig = new();
        InGameCapture gate = rig.Gate;

        // The board's login: not in the game, and no wire a report may take.
        rig.Wire.Append(Bytes("Enter your user-ID: bobthegreat\r\n"));
        Assert.False(gate.InGame);
        Assert.Null(gate.WireMark);

        rig.Prompt();
        Assert.True(gate.InGame);
        Assert.Equal(rig.Wire.TotalBytes, gate.WireMark);

        rig.Line("Your character has been saved.  Please leave any comments in E-mail to Sysop.");
        Assert.False(gate.InGame);
        Assert.Null(gate.WireMark);

        rig.Prompt();
        Assert.True(gate.InGame);
        rig.Line("[E] . Enter the Realm");
        Assert.False(gate.InGame);

        rig.Prompt();
        gate.NotifyDisconnected();
        Assert.False(gate.InGame);
    }

    // Report paradigm-20261008-125639: after `exit` the party poll, a buff, `sn` and
    // `set statline full` were all sent into the board's menu as selections.
    [Fact]
    public void AtBoardMenu_OnlyAfterLeavingTheGame_UntilTheNextGamePrompt()
    {
        using Rig rig = new();
        InGameCapture gate = rig.Gate;

        // A fresh login shows the same menu, and the entry automation has to answer it.
        rig.Line("[E] . Enter the Realm");
        Assert.False(gate.AtBoardMenu);

        rig.Prompt();
        rig.Line("[E] . Enter the Realm");
        Assert.True(gate.AtBoardMenu);

        // The menu redraws on every selection; it is still one stay.
        rig.Line("[E] . Enter the Realm");
        Assert.Equal(new[] { true }, rig.MenuChanges);

        rig.Prompt();
        Assert.False(gate.AtBoardMenu);
        Assert.Equal(new[] { true, false }, rig.MenuChanges);
    }

    [Fact]
    public void AtBoardMenu_EndsWithTheLink()
    {
        using Rig rig = new();
        rig.Prompt();
        rig.Line("[E] . Enter the Realm");
        Assert.True(rig.Gate.AtBoardMenu);

        rig.Gate.NotifyDisconnected();

        Assert.False(rig.Gate.AtBoardMenu);
    }

    // The menu arrives in one chunk with the lines before it; a report stops at the
    // start of that chunk so the menu drawn with it isn't copied.
    [Fact]
    public void LeavingForTheMenus_EndsTheStretchWhereThatChunkBegan()
    {
        using Rig rig = new();
        DateTimeOffset chunk = DateTimeOffset.Now.AddSeconds(-1);
        rig.Gate.FeedTime = () => chunk;
        rig.Prompt();

        rig.Line("[E] . Enter the Realm");

        Assert.False(rig.Gate.Window.Covers(chunk));
    }

    // While the engine gate holds for the menu nothing automatic reaches the wire;
    // a typed selection isn't an engine send and is untouched.
    [Fact]
    public void BoardMenuHold_DropsEngineSends_UntilTheGameIsEnteredAgain()
    {
        using Rig rig = new();
        EngineSendGate engines = new();
        rig.Gate.AtBoardMenuChanged += at => { if (at) engines.Hold("menu"); else engines.Release("menu"); };
        List<string> wire = new();
        Action<byte[]> send = engines.WrapEngineSender(b => wire.Add(Encoding.Latin1.GetString(b)));

        rig.Prompt();
        send(Bytes("par\r"));
        rig.Line("[E] . Enter the Realm");
        send(Bytes("par\r"));
        send(Bytes("set statline full\r"));
        rig.Prompt();
        send(Bytes("sn\r"));

        Assert.Equal(new[] { "par\r", "sn\r" }, wire);
    }
}

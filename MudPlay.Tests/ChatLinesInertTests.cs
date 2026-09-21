using MudPlay.Game;
using MudPlay.Game.Conditions;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Player-typed chat must never drive game state. Report paradigm-20260921-053754: another
// player's `Broadcast from Placido "You are flat on your back!"` (a relay of his own game
// output) matched the knockdown records' applied text, latched MovementPrevented, paused the
// loop and made the client re-cast cure paralysis every round with nothing to cure.
public sealed class ChatLinesInertTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 5, 35, 22, TimeSpan.Zero);

    // ----- ChatLineDetector ---------------------------------------------------

    [Theory]
    [InlineData("Broadcast from Placido \"You are flat on your back!\"")]
    [InlineData("Broadcast from Placido \"Also here: big crag elemental, nasty crag elemental")]   // no closing quote — fail closed
    [InlineData("Forged gossips: You are flat on your back!")]
    [InlineData("Forged auctions: WTS opal ring")]
    [InlineData("Forged telepaths: You are blind.")]
    [InlineData("Forged gangpaths: You feel lucky!")]
    [InlineData("Forged yells \"You are flat on your back!\"")]
    [InlineData("You yell \"hello\"")]
    [InlineData("Forged says \"You are flat on your back!\"")]
    [InlineData("Forged says (to you) \"hello\"")]
    [InlineData("You say \"hello\"")]
    [InlineData("  Broadcast from Placido \"leading whitespace\"")]
    [InlineData("[HP=407/MA=542]: (Meditating) Broadcast from Placido \"prompt not split off\"")]
    [InlineData("BROADCAST FROM Placido \"case\"")]
    public void IsChat_PlayerTypedChatShapes_True(string text)
        => Assert.True(ChatLineDetector.IsChat(text));

    [Theory]
    [InlineData("You are flat on your back!")]
    [InlineData("You cast cure paralysis on Ermias!")]
    [InlineData("Ocean Floor, Deep Water")]
    [InlineData("Obvious exits: north, south, east, west")]
    [InlineData("--- Telepath sent to Goblin ---")]                  // server confirmation, no typed body
    [InlineData("--- Message Directed to Goblin ---")]
    [InlineData("Server PvP Message: Forged just killed Goblin!")]
    [InlineData("Ire just entered the Realm.")]
    [InlineData("Placido says nothing and looks at you.")]           // not the quoted-speech shape
    [InlineData("The squid retreats to the depths of the water.")]
    [InlineData("[HP=407/MA=542]: (Meditating) curp")]
    [InlineData("")]
    public void IsChat_ServerOutput_False(string text)
        => Assert.False(ChatLineDetector.IsChat(text));

    // ----- LineExtractor lanes ------------------------------------------------

    [Fact]
    public void LineExtractor_ChatGoesToChatLane_NotLineEmitted()
    {
        (List<string> server, List<string> chat) = FeedThroughEmulator(
            "Broadcast from Placido \"You are flat on your back!\"\r\n" +
            "You are flat on your back!\r\n");

        Assert.Equal(new[] { "You are flat on your back!" }, server);
        Assert.Equal(new[] { "Broadcast from Placido \"You are flat on your back!\"" }, chat);
    }

    // The exact bytes from the report: prompt repaint, cursor-left + erase-line, then the
    // broadcast on the same row.
    [Fact]
    public void LineExtractor_ReportWireBytes_BroadcastNeverReachesLineEmitted()
    {
        (List<string> server, List<string> chat) = FeedThroughEmulator(
            "\u001b[79D\u001b[K\u001b[0;36m[HP=\u001b[1;36m407\u001b[0;36m/MA=\u001b[1;36m542\u001b[0;36m]: (Meditating) \u001b[0m" +
            "\u001b[79D\u001b[K\u001b[1;33mBroadcast from Placido \"You are flat on your back!\"\r\n");

        Assert.Empty(server);
        Assert.Single(chat);
    }

    [Fact]
    public void LineExtractor_PromptSharedRowWithChat_SplitsAndRoutesEachHalf()
    {
        (List<string> server, List<string> chat) = FeedThroughEmulator(
            "[HP=407/MA=542]: (Meditating) Broadcast from Placido \"You are flat on your back!\"\r\n");

        Assert.Empty(server);   // the prompt half is filtered out of the helper's list
        Assert.Single(chat);
        Assert.StartsWith("Broadcast from Placido", chat[0].TrimStart());
    }

    [Fact]
    public void LineExtractor_LongWrappedBroadcast_StaysOneChatLine()
    {
        string body = new string('x', 200);
        (List<string> server, List<string> chat) = FeedThroughEmulator(
            "Broadcast from Placido \"" + body + "\"\r\n");

        Assert.Empty(server);
        Assert.Equal("Broadcast from Placido \"" + body + "\"", Assert.Single(chat));
    }

    // ----- ConditionTracker end-to-end ---------------------------------------

    [Fact]
    public void Broadcast_QuotingKnockdownText_DoesNotLatchMovementPrevented()
    {
        using ConditionHarness h = new();

        h.Feed("Broadcast from Placido \"You are flat on your back!\"\r\n");

        Assert.False(h.Tracker.IsMovementPrevented);
    }

    [Theory]
    [InlineData("Forged gossips: You are flat on your back!")]
    [InlineData("Forged auctions: You are flat on your back!")]
    [InlineData("Forged telepaths: You are flat on your back!")]
    [InlineData("Forged gangpaths: You are flat on your back!")]
    [InlineData("Forged yells \"You are flat on your back!\"")]
    [InlineData("Forged says \"You are flat on your back!\"")]
    public void EveryChatChannel_QuotingKnockdownText_DoesNotLatchMovementPrevented(string chatLine)
    {
        using ConditionHarness h = new();

        h.Feed(chatLine + "\r\n");

        Assert.False(h.Tracker.IsMovementPrevented);
    }

    // Control: the same text as real server output still latches — the guard is about the
    // source of the line, not the wording.
    [Fact]
    public void RealServerLine_KnockdownText_StillLatchesMovementPrevented()
    {
        using ConditionHarness h = new();

        h.Feed("You are flat on your back!\r\n");

        Assert.True(h.Tracker.IsMovementPrevented);
    }

    // ----- MessageRouter ------------------------------------------------------

    [Fact]
    public void Router_ChatLine_ReachesOnlyConversationPatterns()
    {
        MessageRouter router = new();
        int game = 0, conversation = 0, raw = 0;
        router.LineDispatched += _ => raw++;
        router.Register(new RegexPattern("test.condition", @"flat on your back"), _ => game++);
        router.Register(new RegexPattern(KnownPatterns.ConversationBroadcast, @"^Broadcast from (\w+) ""(.+)"""), _ => conversation++);

        router.Dispatch(ChatLine("Broadcast from Placido \"You are flat on your back!\""));

        Assert.Equal(0, game);
        Assert.Equal(0, raw);
        Assert.Equal(1, conversation);
    }

    [Fact]
    public void Router_ServerLine_ReachesGamePatternsAndRawObservers()
    {
        MessageRouter router = new();
        int game = 0, raw = 0;
        router.LineDispatched += _ => raw++;
        router.Register(new RegexPattern("test.condition", @"flat on your back"), _ => game++);

        router.Dispatch(ServerLine("You are flat on your back!"));

        Assert.Equal(1, game);
        Assert.Equal(1, raw);
    }

    // Chat display / remote commands ride the conversation patterns — they must still work
    // for a chat line the extractor flagged.
    [Fact]
    public void ChatRouter_StillClassifiesFlaggedChatLines()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        List<ChatLogEntry> entries = new();
        chat.EntryClassified += entries.Add;

        router.Dispatch(ChatLine("Broadcast from Placido \"hello there\""));
        router.Dispatch(ChatLine("Forged gossips: anyone selling?"));
        router.Dispatch(ChatLine("Forged telepaths: pst"));

        Assert.Equal(
            new[] { ChatChannel.Broadcast, ChatChannel.Gossip, ChatChannel.TelepathIncoming },
            entries.Select(e => e.Channel).ToArray());
    }

    // ----- helpers ------------------------------------------------------------

    private static LineExtractor.EmittedLine ChatLine(string text) =>
        new(text, new CellAttributes[text.Length], Now, IsPromptLine: false, IsChat: true);

    private static LineExtractor.EmittedLine ServerLine(string text) =>
        new(text, new CellAttributes[text.Length], Now, IsPromptLine: false);

    // Real 80-column emulator + LineExtractor; collects non-prompt text from each lane.
    private static (List<string> Server, List<string> Chat) FeedThroughEmulator(string text)
    {
        TerminalEmulator emulator = new(80, 25);
        LineExtractor extractor = new(emulator);
        List<string> server = new(), chat = new();
        extractor.LineEmitted     += l => { if (!l.IsPromptLine) server.Add(l.Text); };
        extractor.ChatLineEmitted += l => { if (!l.IsPromptLine) chat.Add(l.Text); };
        emulator.Feed(System.Text.Encoding.Latin1.GetBytes(text));
        return (server, chat);
    }

    // ConditionTracker wired to a real emulator + extractor, holding the knockdown records the
    // Paradigm data set carries (a shared applied line, ended by "You get back on your feet.").
    private sealed class ConditionHarness : IDisposable
    {
        private readonly TerminalEmulator _emulator = new(80, 25);
        public ConditionTracker Tracker { get; }

        public ConditionHarness()
        {
            MessageStore messages = new();
            messages.Messages.Add(Knockdown("knockdown"));
            messages.Messages.Add(Knockdown("spear slam knockdown"));
            Tracker = new ConditionTracker(messages, new LogService());
            Tracker.AttachLineExtractor(new LineExtractor(_emulator));
        }

        public void Feed(string text) => _emulator.Feed(System.Text.Encoding.Latin1.GetBytes(text));

        public void Dispose() => Tracker.Dispose();

        private static MessageRecord Knockdown(string name)
        {
            const string applied = "You are flat on your back";
            const string ends = "You get back on your feet";
            return new MessageRecord(
                Id: MessageRecord.ComputeId(name, "", "", "", applied, ends),
                Name: name,
                Flags: MessageFlags.MovementPrevented,
                RawFlagsHex: (ushort)MessageFlags.MovementPrevented,
                CasterMessage: string.Empty,
                TargetMessage: string.Empty,
                WitnessMessage: string.Empty,
                AppliedMessage: applied,
                AppliedEndsWith: ends);
        }
    }
}

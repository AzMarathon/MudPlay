using MudPlay.Game;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

public sealed class ChatRouterTests
{
    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    /// <summary>
    /// Build a router with the default catalog seeded and a ChatRouter
    /// wired to it; collect every EntryClassified event into a list for
    /// assertions.
    /// </summary>
    private static (MessageRouter router, ChatRouter chat, List<ChatLogEntry> entries) Setup(
        Func<bool>? isParadigmRealm = null, LogService? log = null, Func<DateTimeOffset>? clock = null)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router, isParadigmRealm, log, clock);
        List<ChatLogEntry> entries = new();
        chat.EntryClassified += entries.Add;
        return (router, chat, entries);
    }

    [Fact]
    public void Gossip_ClassifiesWithSpeakerAndMessage()
    {
        var (router, _, entries) = Setup();
        router.Dispatch(Line("Forged gossips: hello world"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.Gossip, e.Channel);
        Assert.Equal("Forged",           e.Speaker);
        Assert.Equal("hello world",      e.Message);
    }

    [Fact]
    public void TelepathIn_ClassifiesAsTelepathIncoming()
    {
        var (router, _, entries) = Setup();
        router.Dispatch(Line("Forged telepaths: pst!"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.TelepathIncoming, e.Channel);
        Assert.Equal("Forged", e.Speaker);
        Assert.Equal("pst!",   e.Message);
    }

    [Fact]
    public void TelepathOut_ClassifiesAsOutgoingWithRecipientAsSpeaker()
    {
        var (router, _, entries) = Setup();
        router.Dispatch(Line("--- Telepath sent to Goblin ---"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.TelepathOutgoing, e.Channel);
        Assert.Equal("Goblin", e.Speaker);
        Assert.Equal(string.Empty, e.Message);
    }

    [Fact]
    public void TelepathOut_TypedLineOnScreen_AttributesMessage()
    {
        var (router, _, entries) = Setup();
        // The typed /-line renders on the terminal and is sniffed off the screen.
        router.Dispatch(Line("/Goblin hello there"));
        router.Dispatch(Line("--- Telepath sent to Goblin ---"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.TelepathOutgoing, e.Channel);
        Assert.Equal("Goblin",      e.Speaker);
        Assert.Equal("hello there",  e.Message);
    }

    [Fact]
    public void TelepathOut_EngineOutboundBurst_AttributesMessage()
    {
        var (router, chat, entries) = Setup();
        // An engine broadcast (@wealth toll probe) never touches the screen —
        // the raw "/<recipient> <atCommand>\r" burst must attribute the entry.
        chat.ObserveOutbound(System.Text.Encoding.Latin1.GetBytes("/Raijin @wealth\r"));
        router.Dispatch(Line("--- Telepath sent to Raijin ---"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.TelepathOutgoing, e.Channel);
        Assert.Equal("Raijin",  e.Speaker);
        Assert.Equal("@wealth", e.Message);
    }

    [Fact]
    public void TelepathOut_EngineOutboundBurst_MultipleSendsPairFifoWithConfirmations()
    {
        var (router, chat, entries) = Setup();
        // A @roomba reply answers with one telepath per item — all four sends
        // fire before any confirmation arrives, then the confirmations arrive
        // as their own burst. Each must still get its own message, in order.
        chat.ObserveOutbound(System.Text.Encoding.Latin1.GetBytes("/Farmer greatsword x5\r"));
        chat.ObserveOutbound(System.Text.Encoding.Latin1.GetBytes("/Farmer sash x2\r"));
        chat.ObserveOutbound(System.Text.Encoding.Latin1.GetBytes("/Farmer vial x33\r"));
        chat.ObserveOutbound(System.Text.Encoding.Latin1.GetBytes("/Farmer robes x1\r"));
        router.Dispatch(Line("--- Telepath sent to Farmer ---"));
        router.Dispatch(Line("--- Telepath sent to Farmer ---"));
        router.Dispatch(Line("--- Telepath sent to Farmer ---"));
        router.Dispatch(Line("--- Telepath sent to Farmer ---"));

        Assert.Equal(4, entries.Count);
        Assert.Equal("greatsword x5", entries[0].Message);
        Assert.Equal("sash x2",       entries[1].Message);
        Assert.Equal("vial x33",      entries[2].Message);
        Assert.Equal("robes x1",      entries[3].Message);
    }

    [Fact]
    public void TelepathOut_DuplicateOnScreenEchoOfSameSend_DoesNotDoubleQueue()
    {
        var (router, chat, entries) = Setup();
        // LineExtractor's prompt-split can re-emit an engine-fired reply's own
        // text as an ordinary content line when it lands on the same row as the
        // still-displayed prompt — so the exact same text can reach
        // TryCaptureTelepath twice for one physical send: once via
        // ObserveOutbound (the raw bytes), once via the on-screen echo dispatch.
        // Without a dedup guard this leaves one stale entry in the queue per
        // send, permanently drifting every later pairing for the rest of the
        // session (the reported bug: a bloodstone-brooch answer surfacing on an
        // unrelated later telepath to a different person).
        chat.ObserveOutbound(System.Text.Encoding.Latin1.GetBytes("/Farmer {no record of \"a\"}\r"));
        router.Dispatch(Line("/Farmer {no record of \"a\"}")); // duplicate on-screen echo of the same send
        router.Dispatch(Line("--- Telepath sent to Farmer ---"));

        chat.ObserveOutbound(System.Text.Encoding.Latin1.GetBytes("/Kit {no record of \"b\"}\r"));
        router.Dispatch(Line("/Kit {no record of \"b\"}"));
        router.Dispatch(Line("--- Telepath sent to Kit ---"));

        Assert.Equal(2, entries.Count);
        Assert.Equal("Farmer", entries[0].Speaker);
        Assert.Contains("\"a\"", entries[0].Message);
        Assert.Equal("Kit", entries[1].Speaker);
        Assert.Contains("\"b\"", entries[1].Message);
    }

    [Fact]
    public void TelepathOut_StaleUnconfirmedSend_DiscardedInsteadOfStealingLaterConfirmation()
    {
        // Report paradigm-20260915-055754: a @roomba reply to "martial sleeve"
        // was sent at 20:55 but its own "--- Telepath sent ---" confirmation
        // never arrived/was never recognized — the entry sat in the queue for
        // hours until the NEXT morning's unrelated "dark blue orb" query's
        // confirmation wrongly dequeued it, displaying a nonsense answer to a
        // question nobody asked. The stale entry must be discarded once it's
        // long past any plausible real round-trip, not sit forever waiting to
        // steal whichever confirmation happens along next.
        DateTimeOffset now = new(2026, 9, 14, 20, 55, 43, TimeSpan.Zero);
        var (router, chat, entries) = Setup(clock: () => now);

        chat.ObserveOutbound(System.Text.Encoding.Latin1.GetBytes(
            "/Farmer {no record of \"martial sleeve\"}\r"));
        // Its own confirmation never arrives — Farmer's session moves on to
        // other things for hours, no "--- Telepath sent to Farmer ---" line.

        now = now.AddHours(8).AddMinutes(39);   // 2026-09-15 05:34:44 — next morning
        chat.ObserveOutbound(System.Text.Encoding.Latin1.GetBytes(
            "/Farmer {4 more matching item(s) - refine your search}\r"));
        router.Dispatch(Line("--- Telepath sent to Farmer ---"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal("Farmer", e.Speaker);
        // The FRESH reply, not the 8.5-hour-stale "martial sleeve" one.
        Assert.Contains("4 more matching item", e.Message);
        Assert.DoesNotContain("martial", e.Message);
    }

    [Fact]
    public void TelepathOut_StaleEntryDiscard_LogsWarning()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        LogService log = new();
        List<LogEntry> lines = new();
        log.EntryAdded += lines.Add;
        var (router, chat, _) = Setup(log: log, clock: () => now);

        chat.ObserveOutbound(System.Text.Encoding.Latin1.GetBytes("/Farmer {no record of \"martial\"}\r"));
        now = now.AddSeconds(30);   // well past PendingCaptureStaleAfter
        chat.ObserveOutbound(System.Text.Encoding.Latin1.GetBytes("/Farmer {no record of \"dark blue orb\"}\r"));
        router.Dispatch(Line("--- Telepath sent to Farmer ---"));

        Assert.Contains(lines, l => l.Severity == LogSeverity.Warn
            && l.Source == "ChatRouter"
            && l.Message.Contains("martial", StringComparison.Ordinal));
    }

    // ----- whose confirmation a "Telepath sent" line is ----------------------

    private static byte[] Wire(string line) => System.Text.Encoding.Latin1.GetBytes(line + "\r");

    // The entries raised, each with whether it was the user's own telepath at the
    // moment it was raised.
    private static List<(ChatLogEntry Entry, bool Users)> Watch(ChatRouter chat)
    {
        List<(ChatLogEntry, bool)> seen = new();
        chat.EntryClassified += e => seen.Add((e, chat.OutgoingTelepathWasUsers));
        return seen;
    }

    // A confirmation names the player the telepath reached, in full. It is the
    // confirmation of the pending send addressed to that player: the name as sent
    // begins the name confirmed, whatever capitals were typed.
    [Theory]
    [InlineData("/bo @timer sync", "Bob")]
    [InlineData("/BOB @timer sync", "Bob")]
    [InlineData("/Bob @timer sync", "Bob")]
    public void TelepathOut_AShortenedName_IsConfirmedByThePlayerItReached(string sent, string confirmed)
    {
        var (router, chat, _) = Setup();
        var seen = Watch(chat);

        chat.ObserveOutbound(Wire(sent), usersOwn: true);
        router.Dispatch(Line($"--- Telepath sent to {confirmed} ---"));

        (ChatLogEntry e, bool users) = Assert.Single(seen);
        Assert.Equal((confirmed, "@timer sync"), (e.Speaker, e.Message));
        Assert.True(users);
        Assert.False(chat.OutgoingTelepathWasUsers);   // only while the entry is being raised
    }

    // A send whose own reply was not a confirmation (no such player, letters that
    // fit two players, the telepath not sent) is not handed to the confirmation of
    // a later telepath to someone else. That confirmation is its own send's.
    [Theory]
    [InlineData("/qq @timer sync", "/Mallory {ok}", "Mallory", "{ok}")]
    [InlineData("/bo @timer sync", "/Sue hello", "Sue", "hello")]
    [InlineData("/Bob @timer sync", "/Sue @health", "Sue", "@health")]
    public void TelepathOut_AnUnconfirmedSend_IsNotHandedToAnotherPlayersConfirmation(
        string unconfirmed, string next, string confirmed, string message)
    {
        var (router, chat, _) = Setup();
        var seen = Watch(chat);

        chat.ObserveOutbound(Wire(unconfirmed), usersOwn: true);
        chat.ObserveOutbound(Wire(next), usersOwn: true);
        router.Dispatch(Line($"--- Telepath sent to {confirmed} ---"));

        (ChatLogEntry e, _) = Assert.Single(seen);
        Assert.Equal((confirmed, message), (e.Speaker, e.Message));

        // A second confirmation for that player has no send left to be for.
        router.Dispatch(Line($"--- Telepath sent to {confirmed} ---"));
        Assert.Equal(string.Empty, seen[1].Entry.Message);
        Assert.False(seen[1].Users);
    }

    // A confirmation for another player arriving between a send and its own does
    // not take that send: each is paired by the name it carries.
    [Fact]
    public void TelepathOut_AConfirmationForAnotherPlayerInBetween_LeavesTheSendForItsOwn()
    {
        var (router, chat, _) = Setup();
        var seen = Watch(chat);

        chat.ObserveOutbound(Wire("/bo @timer sync"), usersOwn: true);
        chat.ObserveOutbound(Wire("/Sue @health"));
        router.Dispatch(Line("--- Telepath sent to Sue ---"));
        router.Dispatch(Line("--- Telepath sent to Bob ---"));

        Assert.Equal(new[] { ("Sue", "@health", false), ("Bob", "@timer sync", true) },
            seen.Select(s => (s.Entry.Speaker!, s.Entry.Message, s.Users)));
    }

    // A confirmation is first the confirmation of a send addressed to exactly the
    // name it carries. The user typed letters that fit two players and reached
    // nobody; then the client telepaths a player whose name those letters begin
    // (a reply to her own remote command). Her confirmation is the reply's, not
    // the user's request's: she was never asked.
    [Fact]
    public void TelepathOut_AConfirmation_TakesTheSendAddressedToExactlyThatName_BeforeAShortenedOne()
    {
        var (router, chat, _) = Setup();
        var seen = Watch(chat);

        chat.ObserveOutbound(Wire("/b @timer sync"), usersOwn: true);
        chat.ObserveOutbound(Wire("/Betty {command invalid or not allowed}"));
        router.Dispatch(Line("--- Telepath sent to Betty ---"));

        (ChatLogEntry e, bool users) = Assert.Single(seen);
        Assert.Equal(("Betty", "{command invalid or not allowed}"), (e.Speaker, e.Message));
        Assert.False(users);
    }

    // A shortened request that did reach its player, with a telepath of the
    // client's to the same player going out before or after it: the two
    // confirmations take the two sends, the full-name one first, and the request
    // is confirmed as the user's exactly once.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TelepathOut_AShortenedRequestAndAFullNameSendToTheSamePlayer_AreEachConfirmedOnce(bool requestFirst)
    {
        var (router, chat, _) = Setup();
        var seen = Watch(chat);

        if (requestFirst) chat.ObserveOutbound(Wire("/bo @timer sync"), usersOwn: true);
        chat.ObserveOutbound(Wire("/Bob @health"));
        if (!requestFirst) chat.ObserveOutbound(Wire("/bo @timer sync"), usersOwn: true);
        router.Dispatch(Line("--- Telepath sent to Bob ---"));
        router.Dispatch(Line("--- Telepath sent to Bob ---"));

        Assert.Equal(new[] { ("Bob", "@health", false), ("Bob", "@timer sync", true) },
            seen.Select(s => (s.Entry.Speaker!, s.Entry.Message, s.Users)));

        // A third confirmation has no send left to be for.
        router.Dispatch(Line("--- Telepath sent to Bob ---"));
        Assert.False(seen[2].Users);
        Assert.Equal(string.Empty, seen[2].Entry.Message);
    }

    // The notice for a pending send that was never confirmed quotes the send only
    // when it was the client's own line. What the user sent, and a row known only
    // from the screen, are the user's words: the log says one went unconfirmed
    // and keeps none of it, neither the message nor the name it was addressed to.
    [Fact]
    public void TelepathOut_StaleEntryDiscard_KeepsNoneOfTheUsersWords()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        LogService log = new();
        List<LogEntry> lines = new();
        log.EntryAdded += lines.Add;
        var (router, chat, _) = Setup(log: log, clock: () => now);

        chat.ObserveOutbound(Wire("/hunter 2secret"), usersOwn: true);
        router.Dispatch(Line("/swordfish 3secret"));
        chat.ObserveOutbound(Wire("/Farmer {no record of \"martial\"}"));
        now = now.AddSeconds(30);   // well past PendingCaptureStaleAfter
        router.Dispatch(Line("--- Telepath sent to Farmer ---"));

        string[] warned = lines.Where(l => l.Severity == LogSeverity.Warn && l.Source == "ChatRouter")
            .Select(l => l.Message).ToArray();
        Assert.Equal(3, warned.Length);
        Assert.Contains(warned, m => m.Contains("a telepath the user sent (its words are not kept)", StringComparison.Ordinal));
        Assert.Contains(warned, m => m.Contains("a row shown on screen (its words are not kept)", StringComparison.Ordinal));
        Assert.Contains(warned, m => m.Contains("martial", StringComparison.Ordinal));
        Assert.DoesNotContain(warned, m => m.Contains("secret", StringComparison.Ordinal)
            || m.Contains("hunter", StringComparison.Ordinal) || m.Contains("swordfish", StringComparison.Ordinal));
    }

    // Whose send it was goes with it to its confirmation, and comes only from the
    // line as it went out: the user's own (typed at the terminal, or sent by
    // something of theirs), or one the client made by itself or for another player.
    // A telepath known only from the row the board showed has its text, for the
    // Conversation window, and is nobody's.
    [Fact]
    public void TelepathOut_WhoseSendItWas_IsToldWithItsConfirmation_AndNeverFromTheScreen()
    {
        var (router, chat, _) = Setup();
        var seen = Watch(chat);

        chat.ObserveOutbound(Wire("/Bob @timer sync"), usersOwn: true);  // typed, or a window's button
        router.Dispatch(Line("/Bob @timer sync"));                       // and shown on screen
        router.Dispatch(Line("--- Telepath sent to Bob ---"));
        chat.ObserveOutbound(Wire("/Mallory @timer sync"));              // for another player
        router.Dispatch(Line("/Mallory @timer sync"));
        router.Dispatch(Line("--- Telepath sent to Mallory ---"));
        router.Dispatch(Line("/Sue @timer sync"));                       // a row, with no send seen
        router.Dispatch(Line("--- Telepath sent to Sue ---"));

        Assert.Equal(new[] { true, false, false }, seen.Select(s => s.Users));
        Assert.All(seen, s => Assert.Equal("@timer sync", s.Entry.Message));
    }

    // The same words to two players, one after the other, are two sends: each is
    // confirmed with its message.
    [Fact]
    public void TelepathOut_TheSameMessageToTwoPlayers_IsTwoSends()
    {
        var (router, chat, _) = Setup();
        var seen = Watch(chat);

        chat.ObserveOutbound(Wire("/Bob @timer sync"), usersOwn: true);
        chat.ObserveOutbound(Wire("/Sue @timer sync"), usersOwn: true);
        router.Dispatch(Line("--- Telepath sent to Bob ---"));
        router.Dispatch(Line("--- Telepath sent to Sue ---"));

        Assert.Equal(new[] { ("Bob", "@timer sync", true), ("Sue", "@timer sync", true) },
            seen.Select(s => (s.Entry.Speaker!, s.Entry.Message, s.Users)));
    }

    // ----- a screen row that is the copy of a line the client sent ------------

    // A board shows each line the client sends back as a row of its own. `@do`
    // sends two telepaths in one go (the relayed line, then its `{ok}`), so the
    // first line's copy arrives with another send already made after it. The copy
    // is not a telepath the user typed: however the copies and confirmations fall,
    // and however many such lines follow, no send of the user's comes of them.
    [Theory]
    [InlineData("S1 S2 R1 C R2 C")]         // copy, confirmation, copy, confirmation
    [InlineData("S1 S2 C R1 C R2")]         // each copy after its confirmation
    [InlineData("S1 S2 R2 R1 C C")]         // the copies the other way round
    [InlineData("S1 S2 R1 R2 C C S2 R2 C")] // and a further `@do` after it
    [InlineData("S1 R1 C S1 R1 C S2 R2 C")] // the same request relayed twice
    [InlineData("S1 S2 R1 C R2 C S2 R2 C S2 C R2 R1 R1")]
    public void TelepathOut_TheScreenCopyOfAClientSend_IsNeverASendOfTheUsers(string script)
    {
        var (router, chat, _) = Setup();
        var seen = Watch(chat);
        const string request = "/Mallory @timer sync", ok = "/Mallory {ok}";

        foreach (string step in script.Split(' '))
            switch (step)
            {
                case "S1": chat.ObserveOutbound(Wire(request)); break;
                case "S2": chat.ObserveOutbound(Wire(ok)); break;
                case "R1": router.Dispatch(Line(request)); break;
                case "R2": router.Dispatch(Line(ok)); break;
                default: router.Dispatch(Line("--- Telepath sent to Mallory ---")); break;
            }
        // Whatever else she is then sent and confirmed.
        chat.ObserveOutbound(Wire("/Mallory @health"));
        router.Dispatch(Line("--- Telepath sent to Mallory ---"));
        router.Dispatch(Line("--- Telepath sent to Mallory ---"));

        Assert.DoesNotContain(seen, s => s.Users);
        Assert.Equal(script.Split(' ').Count(s => s == "S1"), seen.Count(s => s.Entry.Message == "@timer sync"));
    }

    // A copy does not always come whole: a long line wraps onto a second row, an
    // incoming line cuts it short, or it comes with something after it. A row that
    // begins a line sent lately, or that one begins, is its copy all the same.
    [Theory]
    [InlineData("/Mallory @timer sy")]
    [InlineData("/Mallory @")]
    [InlineData("/Mallory @timer sync  ")]
    [InlineData("/Mallory @timer sync[HP=10/MA=10]:")]
    public void TelepathOut_ACopyThatIsNotExact_IsStillACopy(string row)
    {
        var (router, chat, _) = Setup();
        var seen = Watch(chat);

        chat.ObserveOutbound(Wire("/Mallory @timer sync"));
        chat.ObserveOutbound(Wire("/Mallory {ok}"));
        router.Dispatch(Line(row));
        router.Dispatch(Line("nc"));   // the rest of a wrapped row reads as no telepath
        router.Dispatch(Line("--- Telepath sent to Mallory ---"));
        router.Dispatch(Line("--- Telepath sent to Mallory ---"));
        router.Dispatch(Line("--- Telepath sent to Mallory ---"));

        Assert.Equal(new[] { "@timer sync", "{ok}", "" }, seen.Select(s => s.Entry.Message));
        Assert.DoesNotContain(seen, s => s.Users);
    }

    // A telepath the user typed is told as it goes out, as theirs. With lines of
    // the client's own awaiting their copies and confirmations, it is confirmed as
    // the user's by the player it reached.
    [Fact]
    public void TelepathOut_ATypedTelepath_IsTheUsers_WithClientSendsPending()
    {
        var (router, chat, _) = Setup();
        var seen = Watch(chat);

        chat.ObserveOutbound(Wire("/Sue @health"));
        chat.ObserveOutbound(Wire("/Mallory {ok}"));
        chat.ObserveOutbound(Wire("/bo @timer sync"), usersOwn: true);   // typed
        router.Dispatch(Line("/bo @timer sync"));
        router.Dispatch(Line("/Sue @health"));                           // the client's copies
        router.Dispatch(Line("--- Telepath sent to Sue ---"));
        router.Dispatch(Line("/Mallory {ok}"));
        router.Dispatch(Line("--- Telepath sent to Mallory ---"));
        router.Dispatch(Line("--- Telepath sent to Bob ---"));

        Assert.Equal(new[] { ("Sue", "@health", false), ("Mallory", "{ok}", false), ("Bob", "@timer sync", true) },
            seen.Select(s => (s.Entry.Speaker!, s.Entry.Message, s.Users)));
    }

    // The screen is never the word on whose send a line was. A row is shown for
    // what the client sent for another player the same as for what the user typed,
    // and not always as it went out. Each of these rows fails to match the line
    // that was sent, so it is queued for the Conversation window's sake and paired
    // with a later confirmation; none of them is the user's send.
    //   - an 8-bit character the terminal draws as another one;
    //   - a copy that comes after the send has gone stale;
    //   - a copy cut by an incoming row just before a second slash.
    [Theory]
    [InlineData("eight-bit")]
    [InlineData("late")]
    [InlineData("cut")]
    public void TelepathOut_ARowThatDoesNotMatchWhatWasSent_IsStillNotTheUsers(string door)
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        var (router, chat, _) = Setup(clock: () => now);
        var seen = Watch(chat);
        void Confirmed() => router.Dispatch(Line("--- Telepath sent to Mallory ---"));

        switch (door)
        {
            case "eight-bit":
                chat.ObserveOutbound(Wire("/Mallory @timer sync \u0081"));
                router.Dispatch(Line("/Mallory @timer sync ü"));   // the same byte, as the terminal draws it
                break;
            case "late":
                chat.ObserveOutbound(Wire("/Mallory @timer sync"));
                now = now.AddSeconds(20);
                router.Dispatch(Line("/Mallory @timer sync"));
                break;
            default:
                chat.ObserveOutbound(Wire("/Mallory x /Mallory @timer sync"));
                router.Dispatch(Line("/Mallory x "));
                router.Dispatch(Line("Sue gossips: hello"));
                router.Dispatch(Line("/Mallory @timer sync"));
                break;
        }
        Confirmed();
        // One more line to her, as any answer of the client's would be, and its
        // confirmation: this is the one the mis-read row used to be credited by.
        chat.ObserveOutbound(Wire("/Mallory {ok}"));
        Confirmed();
        Confirmed();

        Assert.NotEmpty(seen);
        Assert.DoesNotContain(seen, s => s.Users);
    }

    // A row in the words of a line just sent is that line's copy and is not queued
    // again; once the line is no longer lately sent the row is queued, for its
    // text, and is still nobody's send.
    [Fact]
    public void TelepathOut_ARowInTheWordsOfALineJustSent_IsNotQueuedAgain_AndIsNeverTheUsers()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        var (router, chat, _) = Setup(clock: () => now);
        var seen = Watch(chat);

        chat.ObserveOutbound(Wire("/Bob @timer sync"));
        router.Dispatch(Line("/Bob @timer sync"));
        router.Dispatch(Line("/Bob @timer sync"));
        router.Dispatch(Line("--- Telepath sent to Bob ---"));
        router.Dispatch(Line("--- Telepath sent to Bob ---"));
        Assert.Equal(new[] { ("@timer sync", false), ("", false) }, seen.Select(s => (s.Entry.Message, s.Users)));

        now = now.AddSeconds(30);
        router.Dispatch(Line("/Bob @timer sync"));
        router.Dispatch(Line("--- Telepath sent to Bob ---"));
        Assert.Equal(("@timer sync", false), (seen[2].Entry.Message, seen[2].Users));
    }

    // The user's own lines of every kind are remembered for a few seconds as they
    // go out, for a subscriber that sees a request come back on a shared channel
    // and has to know whether the user sent it. A line the client sent by itself,
    // and a row on the screen, are not among them.
    [Fact]
    public void UserSentLately_KnowsOnlyTheUsersOwnLines_AndOnlyForAWhile()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        var (router, chat, _) = Setup(clock: () => now);
        bool Sent(string line) => chat.UserSentLately(l => l == line);

        chat.ObserveOutbound(Wire("bg @roomba sync"));
        router.Dispatch(Line("bg @roomba sync"));
        Assert.False(Sent("bg @roomba sync"));

        chat.ObserveOutbound(Wire("bg @roomba sync"), usersOwn: true);
        Assert.True(Sent("bg @roomba sync"));
        Assert.False(Sent("bg @timer sync"));

        now = now.AddSeconds(20);
        Assert.False(Sent("bg @roomba sync"));
    }

    [Fact]
    public void ObserveOutbound_NonTelepathBytes_LeaveMessageEmpty()
    {
        var (router, chat, entries) = Setup();
        // A plain movement burst isn't a /-telepath — it must not seed a message.
        chat.ObserveOutbound(System.Text.Encoding.Latin1.GetBytes("north\r"));
        router.Dispatch(Line("--- Telepath sent to Raijin ---"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(string.Empty, e.Message);
    }

    [Fact]
    public void Yell_SelfHasNoSpeaker()
    {
        var (router, _, entries) = Setup();
        router.Dispatch(Line(@"You yell ""help""")) ;

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.Yell, e.Channel);
        Assert.Null(e.Speaker);
        Assert.Equal("help", e.Message);
    }

    [Fact]
    public void Yell_OtherHasSpeaker()
    {
        var (router, _, entries) = Setup();
        router.Dispatch(Line(@"Forged yells ""ouch"""));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.Yell, e.Channel);
        Assert.Equal("Forged", e.Speaker);
        Assert.Equal("ouch",   e.Message);
    }

    [Fact]
    public void Say_OtherClassifiesAsLocal()
    {
        var (router, _, entries) = Setup();
        router.Dispatch(Line(@"Forged says ""hi there"""));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.Local, e.Channel);
        Assert.Equal("Forged",   e.Speaker);
        Assert.Equal("hi there", e.Message);
        Assert.Null(e.DirectedTo);   // undirected
    }

    [Fact]
    public void Say_Directed_CapturesTarget_EvenAsAThirdParty()
    {
        // A directed say is seen by the target AND third parties in the room; capture
        // the named target so the window shows "Suijin (to Fujin): hi".
        var (router, _, entries) = Setup();
        router.Dispatch(Line(@"Suijin says (to Fujin) ""hi"""));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.Local, e.Channel);
        Assert.Equal("Suijin", e.Speaker);
        Assert.Equal("hi",     e.Message);
        Assert.Equal("Fujin",  e.DirectedTo);
    }

    [Fact]
    public void Say_DirectedToUs_CapturesYouAsTarget()
    {
        var (router, _, entries) = Setup();
        router.Dispatch(Line(@"Raijin says (to you) ""hi"""));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal("Raijin", e.Speaker);
        Assert.Equal("you",    e.DirectedTo);
    }

    [Fact]
    public void DirectedSayOut_TypedLine_AttributesMessageAndTarget()
    {
        // Our own outgoing directed say: the typed ">Target msg" carries the message,
        // the "--- Message Directed to Target ---" confirmation the target. Speaker is
        // null (us) so it renders "You (to Fujin): hi".
        var (router, _, entries) = Setup();
        router.Dispatch(Line(">fujin hi"));
        router.Dispatch(Line("--- Message Directed to Fujin ---"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.Local, e.Channel);
        Assert.Null(e.Speaker);        // us -> "You"
        Assert.Equal("hi",    e.Message);
        Assert.Equal("Fujin", e.DirectedTo);
    }

    [Fact]
    public void DirectedSayOut_EngineOutboundBurst_AttributesMessage()
    {
        // A gate-wrapped @-command reply never renders on-screen, so the message is
        // captured from the outbound bytes instead of the terminal line.
        var (router, chat, entries) = Setup();
        chat.ObserveOutbound(System.Text.Encoding.Latin1.GetBytes(">Raijin {HP 100/100}\r"));
        router.Dispatch(Line("--- Message Directed to Raijin ---"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal("{HP 100/100}", e.Message);
        Assert.Equal("Raijin",       e.DirectedTo);
    }

    [Fact]
    public void DirectedSay_ToYou_ClassifiesAsLocalWithSpeaker()
    {
        // Report 225011: a directed reply ("Tristian says (to you) ""…""")
        // was dropped entirely because the (to you) clause broke the say
        // regex. It must land in the say channel like any other say.
        var (router, _, entries) = Setup();
        router.Dispatch(Line(@"Tristian says (to you) ""{Yes: 1.}"""));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.Local, e.Channel);
        Assert.Equal("Tristian", e.Speaker);
        Assert.Equal("{Yes: 1.}", e.Message);
    }

    [Fact]
    public void DirectedSay_SelfToOther_HasNoSpeaker()
    {
        // Own directed say keeps "You" out of the speaker group (so it's
        // never mistaken for an inbound @-command), same as a plain say.
        var (router, _, entries) = Setup();
        router.Dispatch(Line(@"You say (to Tristian) ""@have rope"""));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.Local, e.Channel);
        Assert.Null(e.Speaker);
        Assert.Equal("@have rope", e.Message);
    }

    [Fact]
    public void PlayerEnters_ClassifiesAsRealmEvent()
    {
        var (router, _, entries) = Setup();
        router.Dispatch(Line("Forged just entered the Realm."));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.RealmEvent, e.Channel);
        Assert.Equal("Forged",               e.Speaker);
        Assert.Equal("entered the Realm",    e.Message);
    }

    [Fact]
    public void BuiltInDisconnect_ClassifiesAsRealmEvent()
    {
        // The built-in pattern requires the trailing period after "!!!".
        var (router, _, entries) = Setup();
        router.Dispatch(Line("Forged just disconnected!!!."));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.RealmEvent, e.Channel);
        Assert.Equal("Forged",               e.Speaker);
        Assert.Equal("disconnected",         e.Message);
    }

    [Fact]
    public void CustomDisconnectLine_NonStandardForm_ClassifiesAsRealmEvent()
    {
        // A board whose logoff line has no trailing period never matches the
        // built-in pattern; the configured custom pattern must classify it so the
        // conversation window logs the disconnect.
        var (router, chat, entries) = Setup();
        chat.DisconnectPatternProvider = () => "{name} just disconnected!!!";
        router.Dispatch(Line("Forged just disconnected!!!"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.RealmEvent, e.Channel);
        Assert.Equal("Forged",               e.Speaker);
        Assert.Equal("disconnected",         e.Message);
    }

    [Fact]
    public void CustomDisconnectLine_MultiWordName_CapturesFullName()
    {
        var (router, chat, entries) = Setup();
        chat.DisconnectPatternProvider = () => "{name} just disconnected!!!";
        router.Dispatch(Line("MudPlay WuzHere just disconnected!!!"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.RealmEvent, e.Channel);
        Assert.Equal("MudPlay WuzHere",        e.Speaker);
        Assert.Equal("disconnected",         e.Message);
    }

    [Fact]
    public void NoCustomPattern_NonStandardDisconnect_Ignored()
    {
        // No custom pattern configured + a line the built-in can't match (no
        // trailing period) → nothing classified.
        var (router, _, entries) = Setup();
        router.Dispatch(Line("Forged just disconnected!!!"));
        Assert.Empty(entries);
    }

    [Fact]
    public void ServerPvp_KillOnParadigmRealm_ClassifiesAsServerWithFullBodyMessage()
    {
        var (router, _, entries) = Setup(isParadigmRealm: () => true);
        router.Dispatch(Line("Server PvP Message: Balgor just killed Fizznod!"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.Server, e.Channel);
        Assert.Null(e.Speaker);
        Assert.Equal("Balgor just killed Fizznod!", e.Message);
    }

    [Fact]
    public void ServerPvp_NonKillBody_ClassifiesGenericallyByPrefix()
    {
        // The "Server PvP Message: " prefix covers PvP events beyond kills; any
        // body after it must classify as Server, not just the "just killed" form.
        var (router, _, entries) = Setup(isParadigmRealm: () => true);
        router.Dispatch(Line("Server PvP Message: Balgor has declared war on Fizznod!"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.Server, e.Channel);
        Assert.Null(e.Speaker);
        Assert.Equal("Balgor has declared war on Fizznod!", e.Message);
    }

    [Fact]
    public void ServerPvp_OnNonParadigmRealm_IsSuppressed()
    {
        var (router, _, entries) = Setup(isParadigmRealm: () => false);
        router.Dispatch(Line("Server PvP Message: Balgor just killed Fizznod!"));

        Assert.Empty(entries);
    }

    [Fact]
    public void ServerPvp_NullGate_ClassifiesAsServer()
    {
        // Default Setup passes no gate (null) — the channel is open so bare-router
        // tests still see the entry.
        var (router, _, entries) = Setup();
        router.Dispatch(Line("Server PvP Message: Balgor just killed Fizznod!"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.Server, e.Channel);
    }

    [Fact]
    public void Dispose_UnsubscribesAllHandlers()
    {
        var (router, chat, entries) = Setup();
        chat.Dispose();
        router.Dispatch(Line("Forged gossips: hi"));
        Assert.Empty(entries);
        Assert.Equal(0, router.SubscriptionCount);
    }

    [Fact]
    public void NonChatLines_AreIgnored()
    {
        var (router, _, entries) = Setup();
        router.Dispatch(Line("Forged slashes Goblin for 17 damage!"));
        router.Dispatch(Line("Obvious exits: north, south"));
        Assert.Empty(entries);
    }
}

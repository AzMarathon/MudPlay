using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// Each of the three client-to-client transfers goes by its own chat channels and
// no others: a loop by telepath; the gang house's log by telepath or gangpath; boss
// timers by say, telepath, gangpath or the broadcast channel. One table says so for
// both ends of each.
public sealed class TransferChannelsTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);

    // ----- the table ------------------------------------------------------

    [Theory]
    [InlineData(Transfer.LoopSend, RemoteChannel.Telepath, true)]
    [InlineData(Transfer.LoopSend, RemoteChannel.Gangpath, false)]
    [InlineData(Transfer.LoopSend, RemoteChannel.Local, false)]
    [InlineData(Transfer.LoopSend, RemoteChannel.Broadcast, false)]
    [InlineData(Transfer.RoombaSync, RemoteChannel.Telepath, true)]
    [InlineData(Transfer.RoombaSync, RemoteChannel.Gangpath, true)]
    [InlineData(Transfer.RoombaSync, RemoteChannel.Local, false)]
    [InlineData(Transfer.RoombaSync, RemoteChannel.Broadcast, false)]
    [InlineData(Transfer.TimerSync, RemoteChannel.Telepath, true)]
    [InlineData(Transfer.TimerSync, RemoteChannel.Gangpath, true)]
    [InlineData(Transfer.TimerSync, RemoteChannel.Local, true)]
    [InlineData(Transfer.TimerSync, RemoteChannel.Broadcast, true)]
    public void EachTransfer_GoesByItsOwnChannels(Transfer transfer, RemoteChannel channel, bool allowed) =>
        Assert.Equal(allowed, TransferChannels.Allows(transfer, channel));

    // What is heard is judged by the channel it was heard on. The echo of our own
    // telepath is not something another player sent, and gossip, yell and the rest
    // carry no transfer at all.
    [Theory]
    [InlineData(ChatChannel.TelepathIncoming, true)]
    [InlineData(ChatChannel.Gangpath, true)]
    [InlineData(ChatChannel.Local, true)]
    [InlineData(ChatChannel.Broadcast, true)]
    [InlineData(ChatChannel.TelepathOutgoing, false)]
    [InlineData(ChatChannel.Gossip, false)]
    [InlineData(ChatChannel.Yell, false)]
    [InlineData(ChatChannel.Social, false)]
    [InlineData(ChatChannel.Server, false)]
    public void WhatIsHeard_IsATransfersOnlyOnAChannelRemoteCommandsTravelBy(ChatChannel heardOn, bool timersTakeIt)
    {
        Assert.Equal(timersTakeIt, TransferChannels.Allows(Transfer.TimerSync, heardOn));
        Assert.Equal(heardOn == ChatChannel.TelepathIncoming, TransferChannels.Allows(Transfer.LoopSend, heardOn));
        Assert.Equal(heardOn is ChatChannel.TelepathIncoming or ChatChannel.Gangpath,
            TransferChannels.Allows(Transfer.RoombaSync, heardOn));
    }

    [Fact]
    public void TheRefusal_SaysWhereTheTransferDoesWork()
    {
        Assert.Equal("@loop send works by telepath only", TransferChannels.Refusal(Transfer.LoopSend));
        Assert.Equal("@roomba sync works by telepath or gangpath only", TransferChannels.Refusal(Transfer.RoombaSync));
        Assert.Equal("@timer sync works by say, telepath, gangpath or broadcast only", TransferChannels.Refusal(Transfer.TimerSync));
    }

    [Theory]
    [InlineData("@loop", "send kings road", Transfer.LoopSend)]
    [InlineData("@LOOP", "SEND", Transfer.LoopSend)]
    [InlineData("@loop", "send yes", Transfer.LoopSend)]
    [InlineData("@roomba", "sync", Transfer.RoombaSync)]
    [InlineData("@timer", "sync", Transfer.TimerSync)]
    public void TheTransferForms_AreKnown(string command, string args, Transfer expected) =>
        Assert.Equal(expected, TransferChannels.Identify(command, args.Split(' ')));

    // The other forms of the three commands are ordinary remote commands.
    [Theory]
    [InlineData("@loop", "kings road")]
    [InlineData("@loop", "last")]
    [InlineData("@roomba", "torch")]
    [InlineData("@roomba", "syncing ring")]
    [InlineData("@timer", "dragon")]
    [InlineData("@health", "sync")]
    public void OtherForms_AreNoTransfer(string command, string args) =>
        Assert.Null(TransferChannels.Identify(command, args.Split(' ')));

    [Fact]
    public void ABareCommand_IsNoTransfer()
    {
        Assert.Null(TransferChannels.Identify("@timer", Array.Empty<string>()));
        Assert.Null(TransferChannels.Identify("@loop", Array.Empty<string>()));
    }

    // ----- our own request, as the asking side sees it go out --------------

    private static ChatLogEntry Line(ChatChannel channel, string? speaker, string msg) =>
        new(Now, channel, speaker, msg, msg);

    private static bool IsUs(string name) => name.Equals("Us", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void OurOwnRequest_IsKnownByItsEcho_WithTheChannelItWentBy()
    {
        // A telepath's echo names the player it went to.
        Assert.Equal((Transfer.LoopSend, RemoteChannel.Telepath),
            TransferChannels.OwnRequest(Line(ChatChannel.TelepathOutgoing, "Friend", "@loop send yes"), IsUs));
        // "You say" has no speaker; a shared channel echoes our own name.
        Assert.Equal((Transfer.TimerSync, RemoteChannel.Local),
            TransferChannels.OwnRequest(Line(ChatChannel.Local, null, "@timer sync"), IsUs));
        Assert.Equal((Transfer.RoombaSync, RemoteChannel.Gangpath),
            TransferChannels.OwnRequest(Line(ChatChannel.Gangpath, "Us", "@roomba sync"), IsUs));
        Assert.Equal((Transfer.TimerSync, RemoteChannel.Broadcast),
            TransferChannels.OwnRequest(Line(ChatChannel.Broadcast, "Us", "@timer sync"), IsUs));
        Assert.Equal((Transfer.LoopSend, RemoteChannel.Gangpath),
            TransferChannels.OwnRequest(Line(ChatChannel.Gangpath, "Us", "@loop send yes"), IsUs));
    }

    [Fact]
    public void AnotherPlayersRequest_OrALineThatAsksForNoTransfer_IsNotOurs()
    {
        Assert.Null(TransferChannels.OwnRequest(Line(ChatChannel.Gangpath, "Friend", "@roomba sync"), IsUs));
        Assert.Null(TransferChannels.OwnRequest(Line(ChatChannel.TelepathIncoming, "Friend", "@timer sync"), IsUs));
        Assert.Null(TransferChannels.OwnRequest(Line(ChatChannel.TelepathOutgoing, "Friend", "@roomba torch"), IsUs));
        Assert.Null(TransferChannels.OwnRequest(Line(ChatChannel.Gossip, "Us", "@timer sync"), IsUs));
        Assert.Null(TransferChannels.OwnRequest(Line(ChatChannel.Local, null, "hello there"), IsUs));
    }

    // ----- the broadcast channel in the command engine ----------------------

    private sealed class Engine
    {
        public RemoteCommandManager Commands { get; }
        public List<string> Ran { get; } = new();
        public LogService Log { get; } = new();

        // The engine's clock: what it says on the broadcast channel is bounded by
        // the minute.
        public DateTimeOffset Clock { get; set; } = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

        // broadcastOpen: the Talk tab's "Disallow @commands from broadcast" unticked.
        // everyCommand: a handler for every command in the catalogue, not only the
        // three that have a transfer form.
        public Engine(bool warn = true, bool masterSwitchOff = false, bool broadcastOpen = true, bool everyCommand = false)
        {
            MessageRouter router = new();
            DefaultPatterns.Seed(router);
            PlayerDatabase players = new();
            players.RecordObservation("Friend", null, null, null, null, null, null, Now);
            players.EditCustomization("Friend", new PlayerCustomization(RemoteControls: (PlayerRemoteControls)~0));
            players.RecordObservation("Stranger", null, null, null, null, null, null, Now);
            Commands = new RemoteCommandManager(new ChatRouter(router), new PartyState(), players, Log)
            {
                WarnOnDenial = warn,
                BlockedByMasterSwitch = _ => masterSwitchOff,
                SelfNameProvider = () => "Us",
                DisableBroadcastChannel = !broadcastOpen,
            };
            Commands.Now = () => Clock;
            // Stand-ins that answer as the real handlers do for a channel they use,
            // and refuse as they do for one they don't.
            Register("@timer", PlayerRemoteControls.QueryBossTimers, Transfer.TimerSync);
            Register("@roomba", PlayerRemoteControls.QueryItemLocation, Transfer.RoombaSync);
            Register("@loop", PlayerRemoteControls.MovePlayer, Transfer.LoopSend);
            Commands.RegisterHandler("@health", PlayerRemoteControls.QueryHealthStatus, ctx => { Ran.Add("@health"); ctx.Reply("HP=1/1"); });
            if (!everyCommand) return;

            // Every other command answers, refuses and then fails, so that whichever
            // of the three a real handler does would show on the wire.
            foreach (string command in RemoteCommandCatalog.Help.Keys)
            {
                if (command is "@timer" or "@roomba" or "@loop" or "@health") continue;
                RemoteCommandCatalog.TryGetCategory(command, out PlayerRemoteControls category);
                Commands.RegisterHandler(command, category, ctx =>
                {
                    Ran.Add(command);
                    ctx.Reply("an answer");
                    ctx.Refuse("a refusal");
                    throw new InvalidOperationException("a handler that fails");
                });
            }
            Commands.RegisterPrefixHandler("@equip-", PlayerRemoteControls.ExecuteCommands, ctx => { Ran.Add("@equip-"); ctx.Reply("an answer"); });
        }

        private void Register(string command, PlayerRemoteControls category, Transfer transfer) =>
            Commands.RegisterHandler(command, category, ctx =>
            {
                Ran.Add($"{command} {string.Join(' ', ctx.Args)}".Trim());
                if (TransferChannels.Identify(command, ctx.Args) is null) { ctx.Reply("ordinary answer"); return; }
                if (TransferChannels.Allows(transfer, ctx.Channel)) ctx.Reply("data");
                else ctx.Refuse(TransferChannels.Refusal(transfer));
            });

        public string[] Wire => Commands.LastSentForTests.Select(b => Encoding.Latin1.GetString(b)).ToArray();
    }

    private static ChatLogEntry Broadcast(string sender, string msg) =>
        new(Now, ChatChannel.Broadcast, sender, msg, $"Broadcast from {sender} \"{msg}\"");

    // Timers may be asked for on the broadcast channel, and are answered there with
    // the channel's own speak verb.
    [Fact]
    public void TimerSyncOnTheBroadcastChannel_IsFollowed_AndAnsweredThere()
    {
        Engine e = new();

        e.Commands.DispatchForTests(Broadcast("Friend", "@timer sync"));

        Assert.Equal(new[] { "@timer sync" }, e.Ran);
        Assert.Equal("br {data}\r", e.Wire.Single());
    }

    // The broadcast channel carries the transfers and nothing else: no other remote
    // command is followed or answered there, as none was before.
    [Theory]
    [InlineData("@timer")]
    [InlineData("@timer dragon")]
    [InlineData("@health")]
    [InlineData("@loop kings road")]
    [InlineData("@roomba torch")]
    [InlineData("&@health")]
    [InlineData("@nosuchcommand sync")]
    public void AnythingElseOnTheBroadcastChannel_IsPassedOver(string line)
    {
        Engine e = new();

        e.Commands.DispatchForTests(Broadcast("Friend", line));

        Assert.Empty(e.Ran);
        Assert.Empty(e.Wire);
    }

    // The two transfers that do not use the broadcast channel are answered there
    // with where they do work, as on any other channel they do not use.
    [Theory]
    [InlineData("@roomba sync", "br {@roomba sync works by telepath or gangpath only}\r")]
    [InlineData("@loop send kings road", "br {@loop send works by telepath only}\r")]
    [InlineData("@loop send yes", "br {@loop send works by telepath only}\r")]
    public void ATransferThatDoesNotUseTheBroadcastChannel_IsRefusedThere(string line, string expected)
    {
        Engine e = new();
        e.Commands.DispatchForTests(Broadcast("Friend", line));
        Assert.Equal(expected, e.Wire.Single());

        // The refusal is a failure answer: the Warn setting silences it.
        Engine quiet = new(warn: false);
        quiet.Commands.DispatchForTests(Broadcast("Friend", line));
        Assert.Empty(quiet.Wire);
    }

    [Fact]
    public void OnTheBroadcastChannel_TheUsualGatesStillStand()
    {
        // Our own line, echoed back to us with our name, is not a request.
        Engine echo = new();
        echo.Commands.DispatchForTests(Broadcast("Us", "@timer sync"));
        Assert.Empty(echo.Ran);

        // A player without the permission gets the denial, by the Warn setting.
        Engine denied = new();
        denied.Commands.DispatchForTests(Broadcast("Stranger", "@timer sync"));
        Assert.Empty(denied.Ran);
        Assert.Equal("br {command invalid or not allowed}\r", denied.Wire.Single());
        Engine deniedQuietly = new(warn: false);
        deniedQuietly.Commands.DispatchForTests(Broadcast("Stranger", "@timer sync"));
        Assert.Empty(deniedQuietly.Wire);

        // With the master switch off nothing is followed or answered.
        Engine off = new(masterSwitchOff: true);
        off.Commands.DispatchForTests(Broadcast("Friend", "@timer sync"));
        Assert.Empty(off.Ran);
        Assert.Empty(off.Wire);

        // "Disallow all remote commands" covers it too.
        Engine disallowed = new();
        disallowed.Commands.MasterDisable = true;
        disallowed.Commands.DispatchForTests(Broadcast("Friend", "@timer sync"));
        Assert.Empty(disallowed.Ran);
    }

    // The channel is public, so it is not listened to until the user opens it: the
    // Talk tab's "Disallow @commands from broadcast" starts ticked.
    [Fact]
    public void TheBroadcastChannel_IsOffUntilTheUserOpensIt()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        Assert.True(new RemoteCommandManager(new ChatRouter(router), new PartyState(), new PlayerDatabase()).DisableBroadcastChannel);

        Engine e = new(broadcastOpen: false);
        e.Commands.DispatchForTests(Broadcast("Friend", "@timer sync"));
        e.Commands.DispatchForTests(Broadcast("Friend", "@roomba sync"));
        e.Commands.DispatchForTests(Broadcast("Stranger", "@timer sync"));

        Assert.Empty(e.Ran);
        Assert.Empty(e.Wire);
    }

    // Everything the engine can put on the broadcast channel, for every command in
    // the catalogue and for words that are no command, in every form a line takes,
    // from a player with every permission and from one with none. With the box
    // ticked: nothing. Unticked: the data of the one transfer that goes by that
    // channel, the "works by … only" refusal of the other two, and the Failure
    // message to a player without the permission for one of the three; the last
    // two by the Warn setting. No other command is read there, so none is answered,
    // refused or relayed, and a handler is never run for one.
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void OnTheBroadcastChannel_OnlyTheThreeTransfersAreEverAnswered(bool open, bool warn)
    {
        string[] words = RemoteCommandCatalog.Help.Keys
            .Concat(new[] { "@nosuchcommand", "@", "@timerdata", "@loopdata", "@loopresult", "@roombadata", "hello", "{@timer" })
            .ToArray();
        string[] tails = { "", " sync", " send kings road", " send yes", " sync reroll", " reroll", " x", " sync}" };
        Engine e = new(warn: warn, broadcastOpen: open, everyCommand: true);
        int lines = 0;

        foreach (string sender in new[] { "Friend", "Stranger" })
        foreach (string word in words)
        foreach (string tail in tails)
        foreach (string line in new[] { word + tail, word + "!" + tail, "&" + word + tail })
        {
            e.Commands.LastSentForTests.Clear();
            e.Ran.Clear();
            // Each line in a minute of its own: what is said there in one minute is
            // bounded, and that bound has its own tests.
            e.Clock += TimeSpan.FromMinutes(2);

            e.Commands.DispatchForTests(Broadcast(sender, line));

            (string[] expected, bool handlerRuns) = ExpectedOnBroadcast(sender, line, open, warn);
            Assert.True(expected.SequenceEqual(e.Wire),
                $"'{line}' from {sender} (open {open}, warn {warn}) sent [{string.Join(" | ", e.Wire)}], expected [{string.Join(" | ", expected)}]");
            Assert.True(handlerRuns == (e.Ran.Count == 1),
                $"'{line}' from {sender} (open {open}, warn {warn}) ran [{string.Join(" | ", e.Ran)}]");
            lines++;
        }
        Assert.True(lines > 2000, $"only {lines} lines were tried");
    }

    // What a line on the broadcast channel puts on the wire, and whether a handler
    // is run for it at all: only for one of the three transfers, from a player who
    // holds its permission.
    private static (string[] Wire, bool HandlerRuns) ExpectedOnBroadcast(string sender, string line, bool open, bool warn)
    {
        string[] none = Array.Empty<string>();
        if (!open || line.StartsWith('&')) return (none, false);
        string[] tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string command = tokens[0].TrimEnd('!');
        string[] args = tokens[1..];
        if (!command.StartsWith('@') || TransferChannels.Identify(command, args) is not { } transfer) return (none, false);
        // A blocked word anywhere in the line is never answered, on any channel.
        if (args.Contains("reroll")) return (none, false);
        if (sender == "Stranger") return (warn ? new[] { "br {command invalid or not allowed}\r" } : none, false);
        if (transfer == Transfer.TimerSync) return (new[] { "br {data}\r" }, true);
        return (warn ? new[] { $"br {{{TransferChannels.Refusal(transfer)}}}\r" } : none, true);
    }

    // The remote commands for Events are ordinary remote commands: on the broadcast
    // channel they are not read, whoever sends them. By telepath they are.
    [Theory]
    [InlineData("@events", "@events")]
    [InlineData("@event 1 on", "@event")]
    [InlineData("@event daily bank off", "@event")]
    [InlineData("@event! 1", "@event")]
    public void TheEventCommandsOnTheBroadcastChannel_AreNotRead(string line, string command)
    {
        Engine e = new(everyCommand: true);

        e.Commands.DispatchForTests(Broadcast("Friend", line));
        e.Commands.DispatchForTests(Broadcast("Stranger", line));

        Assert.Empty(e.Ran);
        Assert.Empty(e.Wire);

        e.Commands.DispatchForTests(new ChatLogEntry(Now, ChatChannel.TelepathIncoming, "Friend", line, "raw"));
        Assert.Equal(new[] { command }, e.Ran);
    }

    // ----- how much is said on the broadcast channel ------------------------

    private static string[] DebugLines(Engine e) =>
        e.Log.Snapshot().Where(l => l.Severity == LogSeverity.Debug).Select(l => l.Message).ToArray();

    // A player with no permission is told so once (the Failure message), as on any
    // channel. Whatever they send in the rest of that minute draws nothing: every
    // line said there is public and counts against the user's own input.
    [Fact]
    public void OnTheBroadcastChannel_AStrangerDrawsOneLineAMinute()
    {
        Engine e = new();

        for (int i = 0; i < 30; i++)
        {
            e.Commands.DispatchForTests(Broadcast("Stranger", "@timer sync"));
            e.Commands.DispatchForTests(Broadcast("Stranger", $"@loop send loop {i}"));
        }

        Assert.Equal("br {command invalid or not allowed}\r", e.Wire.Single());
        Assert.Equal(59, DebugLines(e).Count(l =>
            l.Contains("from Stranger on the broadcast channel: they were answered there less than a minute ago")));

        e.Clock += TimeSpan.FromSeconds(59);
        e.Commands.DispatchForTests(Broadcast("Stranger", "@timer sync"));
        Assert.Single(e.Wire);

        // A minute after the answer, the next line is answered again.
        e.Clock += TimeSpan.FromSeconds(1);
        e.Commands.DispatchForTests(Broadcast("Stranger", "@roomba sync"));
        e.Commands.DispatchForTests(Broadcast("Stranger", "@roomba sync"));
        Assert.Equal(2, e.Wire.Length);
    }

    // The same for a player who holds the permission: one "works by … only" line,
    // and the handler is not run again for the lines that would draw the rest.
    [Fact]
    public void OnTheBroadcastChannel_AGrantedPlayersRepeatedRefusal_IsSaidOnceAMinute()
    {
        Engine e = new();

        for (int i = 0; i < 30; i++)
            e.Commands.DispatchForTests(Broadcast("Friend", "@roomba sync"));

        Assert.Equal("br {@roomba sync works by telepath or gangpath only}\r", e.Wire.Single());
        Assert.Single(e.Ran);

        // The bound is the broadcast channel's alone.
        e.Commands.DispatchForTests(new ChatLogEntry(Now, ChatChannel.Local, "Friend", "@roomba sync", "raw"));
        e.Commands.DispatchForTests(new ChatLogEntry(Now, ChatChannel.Local, "Friend", "@roomba sync", "raw"));
        Assert.Equal(3, e.Wire.Length);
    }

    // With the Warn setting off a refusal is not said, so it uses up nothing: the
    // timers the same player then asks for are sent.
    [Fact]
    public void OnTheBroadcastChannel_ALineThatDrawsNothing_UsesUpNoAnswer()
    {
        Engine e = new(warn: false);

        e.Commands.DispatchForTests(Broadcast("Friend", "@roomba sync"));
        Assert.Empty(e.Wire);

        e.Commands.DispatchForTests(Broadcast("Friend", "@timer sync"));
        Assert.Equal("br {data}\r", e.Wire.Single());
    }

    // Many speakers cannot make the client flood either: no more than
    // BroadcastAnswerBudget.AnswersPerWindow answers a minute to all of them
    // together. Nothing more is remembered than the speakers answered in the last
    // minute.
    [Fact]
    public void OnTheBroadcastChannel_AllSpeakersTogetherDrawAFewAnswersAMinute()
    {
        const int cap = BroadcastAnswerBudget.AnswersPerWindow;
        Engine e = new();

        for (int i = 0; i < 50; i++)
        {
            e.Commands.DispatchForTests(Broadcast($"Passerby{i}", "@timer sync"));
            e.Clock += TimeSpan.FromSeconds(1);
        }

        Assert.Equal(cap, e.Wire.Length);
        Assert.Equal(cap, e.Commands.BroadcastSpeakersRememberedForTests);
        Assert.Contains(DebugLines(e), l => l.Contains($"{cap} answers were given there in the last minute"));

        // A granted player's timers wait their turn like anything else said there.
        e.Commands.DispatchForTests(Broadcast("Friend", "@timer sync"));
        Assert.Empty(e.Ran);

        // A minute after the first of those answers, there is room for one more.
        e.Clock += TimeSpan.FromSeconds(10);
        e.Commands.DispatchForTests(Broadcast("Friend", "@timer sync"));
        Assert.Equal(new[] { "@timer sync" }, e.Ran);
        Assert.Equal("br {data}\r", e.Wire.Last());
        Assert.Equal(cap, e.Commands.BroadcastSpeakersRememberedForTests);
    }

    // The bookkeeping holds a speaker for a minute and no longer, and never more of
    // them than it will answer.
    [Fact]
    public void TheBroadcastBound_ForgetsASpeakerAfterAMinute_AndNeverHoldsMoreThanItAnswers()
    {
        BroadcastAnswerBudget budget = new();
        DateTimeOffset now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

        for (int i = 0; i < 1000; i++)
        {
            string speaker = $"Passerby{i}";
            if (budget.WhyNot(speaker, now) is null) budget.Note(speaker, now);
            Assert.InRange(budget.Remembered, 1, BroadcastAnswerBudget.AnswersPerWindow);
            now += TimeSpan.FromSeconds(7);
        }

        Assert.Null(budget.WhyNot("Passerby0", now + BroadcastAnswerBudget.Window));
        Assert.Equal(0, budget.Remembered);
    }

    // ----- each transfer, each channel, through the engine -----------------

    private static ChatLogEntry On(RemoteChannel channel, string sender, string msg) => channel switch
    {
        RemoteChannel.Telepath => new(Now, ChatChannel.TelepathIncoming, sender, msg, $"{sender} telepaths: {msg}"),
        RemoteChannel.Gangpath => new(Now, ChatChannel.Gangpath, sender, msg, $"{sender} gangpaths: {msg}"),
        RemoteChannel.Local => new(Now, ChatChannel.Local, sender, msg, $"{sender} says \"{msg}\""),
        _ => Broadcast(sender, msg),
    };

    [Theory]
    [InlineData("@loop send kings road", RemoteChannel.Telepath, "/Friend {data}\r")]
    [InlineData("@loop send kings road", RemoteChannel.Gangpath, "bg {@loop send works by telepath only}\r")]
    [InlineData("@loop send kings road", RemoteChannel.Local, ">Friend {@loop send works by telepath only}\r")]
    [InlineData("@roomba sync", RemoteChannel.Telepath, "/Friend {data}\r")]
    [InlineData("@roomba sync", RemoteChannel.Gangpath, "bg {data}\r")]
    [InlineData("@roomba sync", RemoteChannel.Local, ">Friend {@roomba sync works by telepath or gangpath only}\r")]
    [InlineData("@timer sync", RemoteChannel.Telepath, "/Friend {data}\r")]
    [InlineData("@timer sync", RemoteChannel.Gangpath, "bg {data}\r")]
    [InlineData("@timer sync", RemoteChannel.Local, ">Friend {data}\r")]
    [InlineData("@timer sync", RemoteChannel.Broadcast, "br {data}\r")]
    public void EachTransferOnEachChannel_IsAnsweredOrRefusedOnThatChannel(string line, RemoteChannel channel, string expected)
    {
        Engine e = new();

        e.Commands.DispatchForTests(On(channel, "Friend", line));

        Assert.Equal(expected, e.Wire.Single());
    }

    // A channel switched off on the Talk tab stays off whatever a transfer allows:
    // the request is not looked at, so there is no answer and no refusal.
    [Theory]
    [InlineData(RemoteChannel.Telepath, "@loop send kings road")]
    [InlineData(RemoteChannel.Gangpath, "@roomba sync")]
    [InlineData(RemoteChannel.Local, "@timer sync")]
    public void AChannelSwitchedOffOnTheTalkTab_StaysOff(RemoteChannel channel, string line)
    {
        Engine e = new();
        e.Commands.DisableTelepathChannel = channel == RemoteChannel.Telepath;
        e.Commands.DisableGangpathChannel = channel == RemoteChannel.Gangpath;
        e.Commands.DisableLocalChannel = channel == RemoteChannel.Local;

        e.Commands.DispatchForTests(On(channel, "Friend", line));

        Assert.Empty(e.Ran);
        Assert.Empty(e.Wire);
    }

    // Sneaking, nothing is said aloud: the answer to a said request goes by telepath,
    // data and refusal alike. The channel the request came by is still what decides.
    [Theory]
    [InlineData("@timer sync", "/Friend {data}\r")]
    [InlineData("@roomba sync", "/Friend {@roomba sync works by telepath or gangpath only}\r")]
    [InlineData("@loop send kings road", "/Friend {@loop send works by telepath only}\r")]
    public void ASaidRequest_WhileSneaking_IsAnsweredByTelepath(string line, string expected)
    {
        Engine e = new();
        e.Commands.StealthedProvider = () => true;

        e.Commands.DispatchForTests(On(RemoteChannel.Local, "Friend", line));

        Assert.Equal(expected, e.Wire.Single());
    }
}

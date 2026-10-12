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

    // ----- whose request a telepathed one was -------------------------------

    // The requests of ours the chat log raises while a script of sends, screen rows
    // and confirmations plays, each with whether it can be taken as the user's own:
    // what the app goes by before it accepts another client's data for one.
    private static List<(Transfer Transfer, bool UsersOwn)> RequestsSeen(Action<MessageRouter, ChatRouter> play)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        List<(Transfer, bool)> seen = new();
        chat.EntryClassified += e =>
        {
            if (TransferChannels.OwnRequest(e, IsUs) is { } asked)
                seen.Add((asked.Transfer, TransferChannels.IsUsersOwn(e, asked.Channel, chat)));
        };
        play(router, chat);
        return seen;
    }

    private static MudPlay.Terminal.LineExtractor.EmittedLine Row(string text) =>
        new(text, new MudPlay.Terminal.CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    private static byte[] Sent(string line) => Encoding.Latin1.GetBytes(line + "\r");

    // The client telepaths these lines for other players too: a `@do /<her> @roomba
    // sync`, or a relay-back of `@loop send yes`. Taken as the user's, the first
    // would have this client take in her gang-house log and the second accept a
    // loop she offered. Sent by the client for her, with its `{ok}` after it and
    // the board showing both rows back, neither is the user's request.
    [Theory]
    [InlineData("@roomba sync", Transfer.RoombaSync)]
    [InlineData("@loop send yes", Transfer.LoopSend)]
    [InlineData("@timer sync", Transfer.TimerSync)]
    public void ATransferRequestTheClientTelepathedForAnotherPlayer_IsNotTheUsers(string request, Transfer transfer)
    {
        var seen = RequestsSeen((router, chat) =>
        {
            chat.ObserveOutbound(Sent($"/Mallory {request}"));
            chat.ObserveOutbound(Sent("/Mallory {ok}"));
            router.Dispatch(Row($"/Mallory {request}"));
            router.Dispatch(Row("--- Telepath sent to Mallory ---"));
            router.Dispatch(Row("/Mallory {ok}"));
            router.Dispatch(Row("--- Telepath sent to Mallory ---"));
            // A further `@do` of hers.
            chat.ObserveOutbound(Sent("/Mallory {ok}"));
            router.Dispatch(Row("/Mallory {ok}"));
            router.Dispatch(Row("--- Telepath sent to Mallory ---"));
        });

        Assert.Equal(new[] { (transfer, false) }, seen);
    }

    // Each of them is started by the user and by nothing else in the client: typed
    // at the terminal, or sent as a line of the user's by a window, a macro, a
    // trigger, an event or an alias. Either way the line is told as it goes out as
    // the user's own, and it counts, with the board showing it back or not.
    [Theory]
    [InlineData("@roomba sync", Transfer.RoombaSync, true)]
    [InlineData("@roomba sync", Transfer.RoombaSync, false)]
    [InlineData("@loop send yes", Transfer.LoopSend, true)]
    [InlineData("@loop send yes", Transfer.LoopSend, false)]
    public void ATransferRequestTheUserTelepathed_IsTheUsers(string request, Transfer transfer, bool boardShowsItBack)
    {
        var seen = RequestsSeen((router, chat) =>
        {
            chat.ObserveOutbound(Sent($"/mal {request}"), usersOwn: true);
            if (boardShowsItBack) router.Dispatch(Row($"/mal {request}"));
            router.Dispatch(Row("--- Telepath sent to Mallory ---"));
        });

        Assert.Equal(new[] { (transfer, true) }, seen);
    }

    // A row on the screen says a line was shown, never whose send it was. A
    // telepathed request known only from its row (nothing was seen going out for
    // it) is nobody's request.
    [Theory]
    [InlineData("@roomba sync", Transfer.RoombaSync)]
    [InlineData("@loop send yes", Transfer.LoopSend)]
    [InlineData("@timer sync", Transfer.TimerSync)]
    public void ATransferRequestKnownOnlyFromTheScreen_IsNotTheUsers(string request, Transfer transfer)
    {
        var seen = RequestsSeen((router, _) =>
        {
            router.Dispatch(Row($"/Mallory {request}"));
            router.Dispatch(Row("--- Telepath sent to Mallory ---"));
        });

        Assert.Equal(new[] { (transfer, false) }, seen);
    }

    // ----- whose request one on a shared channel was -------------------------

    // Our own line coming back on the gang channel shows that this client said it.
    // The client says such lines for other players too: a `@do bg @roomba sync`
    // would otherwise start two minutes in which every `@roombadata` line from
    // anyone is merged into the item log unreviewed. It is the user's request only
    // if the user sent it, as a line of their own, in the last few seconds.
    [Fact]
    public void ARoombaSyncOnGangpathTheClientSentForAnotherPlayer_IsNotTheUsers()
    {
        DateTimeOffset now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router, clock: () => now);
        ChatLogEntry echo = Line(ChatChannel.Gangpath, "Us", "@roomba sync");
        Assert.Equal((Transfer.RoombaSync, RemoteChannel.Gangpath), TransferChannels.OwnRequest(echo, IsUs));

        // Her `@do bg @roomba sync`, and the `{ok}` the client telepaths her.
        chat.ObserveOutbound(Sent("bg @roomba sync"));
        chat.ObserveOutbound(Sent("/Mallory {ok}"));
        Assert.False(TransferChannels.IsUsersOwn(echo, RemoteChannel.Gangpath, chat));

        // The user's own, typed or sent by something of theirs.
        chat.ObserveOutbound(Sent("bg @roomba sync"), usersOwn: true);
        Assert.True(TransferChannels.IsUsersOwn(echo, RemoteChannel.Gangpath, chat));

        // And only for a few seconds: an echo that comes long after is not it.
        now += TimeSpan.FromSeconds(20);
        Assert.False(TransferChannels.IsUsersOwn(echo, RemoteChannel.Gangpath, chat));
    }

    // The user's line has to carry the request on that channel. The same words by
    // telepath, by say, or on the broadcast channel are not a gang request; other
    // words on the gang channel are not this request. A speak form nobody listed
    // (a board's own say character, a verb spelled another way) is taken for the
    // channel the echo came on, so a request the user did type still counts.
    [Theory]
    [InlineData("bg @roomba sync", true)]
    [InlineData("BG @Roomba Sync", true)]
    [InlineData("gb @roomba sync", true)]
    [InlineData("broadgang @roomba sync", true)]
    [InlineData("/Bob @roomba sync", false)]
    [InlineData(".@roomba sync", false)]
    [InlineData("@roomba sync", false)]
    [InlineData("br @roomba sync", false)]
    [InlineData("'@roomba sync", false)]
    [InlineData("broadcast @roomba sync", false)]
    [InlineData("bg @roomba torch", false)]
    [InlineData("bg @timer sync", false)]
    [InlineData("gsay @roomba sync", true)]
    public void AUsersLine_IsTheRequest_OnlyWhenItCarriesItOnThatChannel(string usersLine, bool isIt)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        chat.ObserveOutbound(Sent(usersLine), usersOwn: true);

        Assert.Equal(isIt, TransferChannels.IsUsersOwn(
            Line(ChatChannel.Gangpath, "Us", "@roomba sync"), RemoteChannel.Gangpath, chat));
    }

    // The same for a timer sync said aloud or on the broadcast channel: there it
    // decides only whether the window opens as a request of the user's.
    [Theory]
    [InlineData(RemoteChannel.Local, ChatChannel.Local, ".@timer sync", true)]
    [InlineData(RemoteChannel.Local, ChatChannel.Local, "@timer sync", true)]
    [InlineData(RemoteChannel.Local, ChatChannel.Local, "bg @timer sync", false)]
    [InlineData(RemoteChannel.Broadcast, ChatChannel.Broadcast, "br @timer sync", true)]
    [InlineData(RemoteChannel.Broadcast, ChatChannel.Broadcast, "-@timer sync", true)]
    [InlineData(RemoteChannel.Broadcast, ChatChannel.Broadcast, ".@timer sync", false)]
    public void ATimerSyncOnASharedChannel_IsTheUsers_OnlyWhenTheUserSentItThere(
        RemoteChannel channel, ChatChannel heardOn, string usersLine, bool isIt)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        ChatLogEntry echo = Line(heardOn, heardOn == ChatChannel.Local ? null : "Us", "@timer sync");

        Assert.False(TransferChannels.IsUsersOwn(echo, channel, chat));
        chat.ObserveOutbound(Sent(usersLine), usersOwn: true);
        Assert.Equal(isIt, TransferChannels.IsUsersOwn(echo, channel, chat));
    }

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
            PlayerDatabase players = _players;
            Grant("Friend");
            players.RecordObservation("Stranger", null, null, null, null, null, null, Now);
            Commands = new RemoteCommandManager(new ChatRouter(router), Party, players, Log)
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

        // The party we are in: empty unless a test adds a member.
        public PartyState Party { get; } = new();

        private readonly PlayerDatabase _players = new();

        // Give a player every permission.
        public void Grant(string name) => Grant(name, (PlayerRemoteControls)~0);

        public void Grant(string name, PlayerRemoteControls controls)
        {
            _players.RecordObservation(name, null, null, null, null, null, null, Now);
            _players.EditCustomization(name, new PlayerCustomization(RemoteControls: controls));
        }
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

    // With its box unticked the broadcast channel is read like the other three: an
    // ordinary remote command from a player who holds its permission is carried
    // out, and answered on the channel. The log says where it was said.
    [Theory]
    [InlineData("@timer", "@timer", "br {ordinary answer}\r")]
    [InlineData("@timer dragon", "@timer", "br {ordinary answer}\r")]
    [InlineData("@health", "@health", "br {HP=1/1}\r")]
    [InlineData("@loop kings road", "@loop", "br {ordinary answer}\r")]
    [InlineData("@roomba torch", "@roomba", "br {ordinary answer}\r")]
    public void AnOrdinaryCommandOnTheBroadcastChannel_IsFollowed_AndAnsweredThere(
        string line, string command, string expected)
    {
        Engine e = new();

        e.Commands.DispatchForTests(Broadcast("Friend", line));

        Assert.StartsWith(command, Assert.Single(e.Ran));
        Assert.Equal(expected, e.Wire.Single());
        Assert.Contains(e.Log.Snapshot(), l => l.Severity == LogSeverity.Info
            && l.Message == $"Following {command} from Friend, said on the broadcast channel.");
    }

    // What is no command stays unread there, relayed or not.
    [Theory]
    [InlineData("@nosuchcommand sync")]
    [InlineData("hello @health")]
    [InlineData("&@nosuchcommand")]
    [InlineData("&@")]
    public void AWordThatIsNoCommand_OnTheBroadcastChannel_IsPassedOver(string line)
    {
        Engine e = new();

        e.Commands.DispatchForTests(Broadcast("Friend", line));
        e.Commands.DispatchForTests(Broadcast("Stranger", line));

        Assert.Empty(e.Ran);
        Assert.Empty(e.Wire);
    }

    // A relay-back asked for on the broadcast channel goes back on it, bare, as on
    // any channel: the asker's client then takes it as a command of ours. Nothing
    // is run here for it.
    [Theory]
    [InlineData("&@health", "br @health\r")]
    [InlineData("&@timer sync", "br @timer sync\r")]
    [InlineData("&@loop kings road", "br @loop kings road\r")]
    public void ARelayBackOnTheBroadcastChannel_IsEchoedThere(string line, string expected)
    {
        Engine e = new();

        e.Commands.DispatchForTests(Broadcast("Friend", line));

        Assert.Empty(e.Ran);
        Assert.Equal(expected, e.Wire.Single());
        Assert.Contains(e.Log.Snapshot(), l => l.Severity == LogSeverity.Info
            && l.Message == $"Relaying {line[1..]} back to Friend on Broadcast (&@ request).");
    }

    // Sneaking, a relay asked for on say goes back by telepath; one asked for on
    // the broadcast channel stays there, where speaking ends no sneak.
    [Fact]
    public void ARelayBackOnTheBroadcastChannel_StaysThereWhileSneaking()
    {
        Engine e = new();
        e.Commands.StealthedProvider = () => true;

        e.Commands.DispatchForTests(Broadcast("Friend", "&@health"));
        e.Commands.DispatchForTests(new ChatLogEntry(Now, ChatChannel.Local, "Friend", "&@health", "raw"));

        Assert.Equal(new[] { "br @health\r", "/Friend @health\r" }, e.Wire);
    }

    // Every gate a relay-back has elsewhere stands there: the grant (denied on the
    // channel, by the Warn setting), the master switch, the box, and the payloads
    // that are never relayed.
    [Fact]
    public void ARelayBackOnTheBroadcastChannel_IsHeldToTheUsualGates()
    {
        Engine denied = new();
        denied.Commands.DispatchForTests(Broadcast("Stranger", "&@health"));
        Assert.Equal("br {command invalid or not allowed}\r", denied.Wire.Single());

        Engine deniedQuietly = new(warn: false);
        deniedQuietly.Commands.DispatchForTests(Broadcast("Stranger", "&@health"));
        Assert.Empty(deniedQuietly.Wire);

        Engine off = new(masterSwitchOff: true);
        off.Commands.DispatchForTests(Broadcast("Friend", "&@health"));
        Assert.Empty(off.Wire);

        Engine ticked = new(broadcastOpen: false);
        ticked.Commands.DispatchForTests(Broadcast("Friend", "&@health"));
        ticked.Commands.DispatchForTests(Broadcast("Stranger", "&@health"));
        Assert.Empty(ticked.Wire);

        Engine blocked = new(everyCommand: true);
        foreach (string line in new[] { "&@do reroll", "&@party set suicide", "&@health reroll" })
        {
            blocked.Clock += TimeSpan.FromMinutes(2);
            blocked.Commands.DispatchForTests(Broadcast("Friend", line));
        }
        Assert.Empty(blocked.Wire);
        Assert.Empty(blocked.Ran);
    }

    // The echo is a line said on the channel, so it is one of the minute's answers:
    // a second relay from the same player inside the minute sends nothing, and so
    // does any relay once the channel has had its answers. A relay also uses up the
    // answer a command would have had.
    [Fact]
    public void ARelayBackOnTheBroadcastChannel_IsHeldToTheChannelsBound()
    {
        Engine e = new();

        e.Commands.DispatchForTests(Broadcast("Friend", "&@health"));
        e.Commands.DispatchForTests(Broadcast("Friend", "&@timer"));
        e.Commands.DispatchForTests(Broadcast("Friend", "@health"));

        Assert.Equal("br @health\r", e.Wire.Single());
        Assert.Empty(e.Ran);
        Assert.Contains(DebugLines(e), l => l ==
            "Passing over &@timer from Friend on the broadcast channel: they were answered there less than a minute ago.");

        // Players without the grant draw the channel's few refusals and no more, and
        // do not cost a player who holds it their relay.
        const int cap = BroadcastAnswerBudget.AnswersPerWindow;
        Engine full = new();
        for (int i = 0; i < cap + 2; i++)
            full.Commands.DispatchForTests(Broadcast($"Passerby{i}", "&@health"));
        Assert.Equal(cap, full.Wire.Length);
        Assert.All(full.Wire, line => Assert.Equal("br {command invalid or not allowed}\r", line));
        full.Commands.DispatchForTests(Broadcast("Friend", "&@health"));
        Assert.Equal("br @health\r", full.Wire.Last());

        // The players who hold it have the same few between them.
        Engine granted = new();
        for (int i = 0; i <= cap; i++) granted.Grant($"Granted{i}");
        for (int i = 0; i <= cap; i++)
            granted.Commands.DispatchForTests(Broadcast($"Granted{i}", "&@health"));
        Assert.Equal(cap, granted.Wire.Length);
    }

    // A command that is taken on telepath and gangpath only is not put on any
    // channel by a relay: `&@dupe` would go out as our own @dupe wherever the asker
    // chose. Dropped without a word on every channel, from anyone.
    [Theory]
    [InlineData(RemoteChannel.Telepath)]
    [InlineData(RemoteChannel.Gangpath)]
    [InlineData(RemoteChannel.Local)]
    [InlineData(RemoteChannel.Broadcast)]
    public void ARelayBackOfACommandForThePathChannelsOnly_IsNeverRelayed(RemoteChannel channel)
    {
        Engine e = new(everyCommand: true);

        e.Commands.DispatchForTests(On(channel, "Friend", "&@dupe Stranger"));
        e.Commands.DispatchForTests(On(channel, "Stranger", "&@dupe Friend"));

        Assert.Empty(e.Wire);
        Assert.Empty(e.Ran);
        Assert.Contains(DebugLines(e), l => l.StartsWith("Ignoring relay-back of @dupe from Friend:"));

        // Another command is relayed there as ever.
        e.Commands.DispatchForTests(On(channel, "Friend", "&@health"));
        Assert.EndsWith("@health\r", e.Wire.Single());
    }

    // Until the client knows its own name it cannot tell its own line on the
    // broadcast channel from a stranger's, so it relays nothing there: the echo of
    // what it relayed would be read as a command. The other channels are as ever.
    [Fact]
    public void WithOurOwnNameNotYetKnown_NothingIsRelayedOnTheBroadcastChannel()
    {
        Engine e = new();
        e.Commands.SelfNameProvider = () => null;

        e.Commands.DispatchForTests(Broadcast("Friend", "&@health"));
        e.Commands.DispatchForTests(Broadcast("Stranger", "&@health"));
        Assert.Empty(e.Wire);
        Assert.Contains(DebugLines(e), l => l.Contains("our own name is not known yet"));

        e.Commands.DispatchForTests(On(RemoteChannel.Gangpath, "Friend", "&@health"));
        Assert.Equal("bg @health\r", e.Wire.Single());

        // A command there is still read: only the relay waits for the name.
        e.Commands.DispatchForTests(Broadcast("Friend", "@health"));
        Assert.Equal("br {HP=1/1}\r", e.Wire.Last());
    }

    // ----- a party member's commands on the broadcast channel ----------------

    // The commands a party member may send whatever they were granted (@wait,
    // @comeback, @ptrain and the rest of that list) are read on the broadcast
    // channel as they are on gangpath: carried out for a member of our party, and
    // not for anyone else, who gets the Failure message by the Warn setting.
    [Theory]
    [InlineData("@wait")]
    [InlineData("@ok")]
    [InlineData("@comeback")]
    [InlineData("@ptrain ask")]
    [InlineData("@share")]
    public void APartyMembersCommand_OnTheBroadcastChannel_IsAsOnGangpath(string line)
    {
        string command = line.Split(' ')[0];
        (string[] Ran, string[] Wire) Heard(RemoteChannel channel, string sender, bool warn)
        {
            Engine e = new(warn: warn, everyCommand: true);
            e.Party.Members.Add(new PartyMember { Name = "Buddy" });
            e.Commands.DispatchForTests(On(channel, sender, line));
            // Without the channel's own speak verb, to compare the two.
            return (e.Ran.ToArray(), e.Wire.Select(w => w[(w.IndexOf(' ') + 1)..]).ToArray());
        }

        foreach (bool warn in new[] { true, false })
        {
            (string[] ran, string[] wire) = Heard(RemoteChannel.Broadcast, "Buddy", warn);
            (string[] ranOnGang, string[] wireOnGang) = Heard(RemoteChannel.Gangpath, "Buddy", warn);
            Assert.Equal(new[] { command }, ran);
            Assert.Equal(ranOnGang, ran);
            Assert.Equal(wireOnGang, wire);

            // Neither a stranger nor a player granted everything is in the party.
            foreach (string outsider in new[] { "Stranger", "Friend" })
            {
                (string[] notRun, string[] refusal) = Heard(RemoteChannel.Broadcast, outsider, warn);
                (string[] notRunOnGang, string[] refusalOnGang) = Heard(RemoteChannel.Gangpath, outsider, warn);
                Assert.Empty(notRun);
                Assert.Equal(warn ? new[] { "{command invalid or not allowed}\r" } : Array.Empty<string>(), refusal);
                Assert.Equal(notRunOnGang, notRun);
                Assert.Equal(refusalOnGang, refusal);
            }
        }
    }

    // With the master switch off a party member's command on the channel is not
    // followed or answered, like any other.
    [Fact]
    public void APartyMembersCommand_OnTheBroadcastChannel_IsNotFollowedWithTheMasterSwitchOff()
    {
        Engine e = new(masterSwitchOff: true, everyCommand: true);
        e.Party.Members.Add(new PartyMember { Name = "Buddy" });

        e.Commands.DispatchForTests(Broadcast("Buddy", "@comeback"));
        e.Commands.DispatchForTests(Broadcast("Buddy", "@ptrain ask"));

        Assert.Empty(e.Ran);
        Assert.Empty(e.Wire);
    }

    // On Paradigm the channel shows us our own line. The command we relayed comes
    // back under our own name and is not a command to us: nothing runs, nothing is
    // said, and it uses up nobody's answer.
    [Fact]
    public void OurOwnRelayedCommand_EchoedBackOnTheBroadcastChannel_IsNotRun()
    {
        Engine e = new();
        e.Commands.DispatchForTests(Broadcast("Friend", "&@health"));
        Assert.Equal("br @health\r", e.Wire.Single());

        e.Commands.DispatchForTests(Broadcast("Us", "@health"));
        e.Commands.DispatchForTests(Broadcast("Us", "&@health"));

        Assert.Empty(e.Ran);
        Assert.Single(e.Wire);
        Assert.Equal(1, e.Commands.BroadcastSpeakersRememberedForTests);
    }

    // Every gate that stands on the other channels stands there for an ordinary
    // command: the permission (answered with the Failure message, by the Warn
    // setting, on the channel), the master switch, "Disallow all", and the box
    // itself, which while ticked has nothing read.
    [Fact]
    public void AnOrdinaryCommandOnTheBroadcastChannel_IsHeldToTheUsualGates()
    {
        Engine denied = new();
        denied.Commands.DispatchForTests(Broadcast("Stranger", "@health"));
        Assert.Empty(denied.Ran);
        Assert.Equal("br {command invalid or not allowed}\r", denied.Wire.Single());
        Assert.Contains(DebugLines(denied), l => l.StartsWith("Denied @health from Stranger on broadcast (lacks"));

        Engine deniedQuietly = new(warn: false);
        deniedQuietly.Commands.DispatchForTests(Broadcast("Stranger", "@health"));
        Assert.Empty(deniedQuietly.Ran);
        Assert.Empty(deniedQuietly.Wire);

        Engine off = new(masterSwitchOff: true);
        off.Commands.DispatchForTests(Broadcast("Friend", "@health"));
        off.Commands.DispatchForTests(Broadcast("Stranger", "@health"));
        Assert.Empty(off.Ran);
        Assert.Empty(off.Wire);

        Engine disallowed = new();
        disallowed.Commands.MasterDisable = true;
        disallowed.Commands.DispatchForTests(Broadcast("Friend", "@health"));
        Assert.Empty(disallowed.Ran);
        Assert.Empty(disallowed.Wire);

        Engine ticked = new(broadcastOpen: false);
        ticked.Commands.DispatchForTests(Broadcast("Friend", "@health"));
        ticked.Commands.DispatchForTests(Broadcast("Stranger", "@health"));
        Assert.Empty(ticked.Ran);
        Assert.Empty(ticked.Wire);
        Assert.Empty(ticked.Log.Snapshot());
    }

    // The blocks that never answer do not answer there either, whoever asks and
    // whatever the Warn setting: a reroll anywhere in the line, `@party set
    // suicide`, and a command that hands out trust, which is for the path channels.
    [Theory]
    [InlineData("@do reroll")]
    [InlineData("@party reroll")]
    [InlineData("@party set suicide")]
    [InlineData("@dupe Stranger")]
    public void TheSilentBlocks_StaySilentOnTheBroadcastChannel(string line)
    {
        Engine e = new(everyCommand: true);

        e.Commands.DispatchForTests(Broadcast("Friend", line));
        e.Clock += TimeSpan.FromMinutes(2);
        e.Commands.DispatchForTests(Broadcast("Stranger", line));

        Assert.Empty(e.Ran);
        Assert.Empty(e.Wire);
    }

    // A refusal with a reason of its own is said on the channel too, to a sender
    // who holds the permission, and by the Warn setting.
    [Fact]
    public void TheSuicideRules_AnswerOnTheBroadcastChannel_ByTheWarnSetting()
    {
        Engine e = new(everyCommand: true);
        e.Commands.DispatchForTests(Broadcast("Friend", "@do suicide"));
        e.Clock += TimeSpan.FromMinutes(2);
        e.Commands.DispatchForTests(Broadcast("Friend", "@suicide"));
        Assert.Empty(e.Ran);
        Assert.Equal(new[]
        {
            "br {@do suicide is not allowed, use @suicide}\r",
            "br {suicide blocked, lives unknown to client}\r",
        }, e.Wire);

        Engine quiet = new(warn: false, everyCommand: true);
        quiet.Commands.DispatchForTests(Broadcast("Friend", "@do suicide"));
        quiet.Commands.DispatchForTests(Broadcast("Friend", "@suicide"));
        Assert.Empty(quiet.Ran);
        Assert.Empty(quiet.Wire);
    }

    // Speaking on the broadcast channel ends no sneak, so an answer there is not
    // moved to telepath while we are sneaking, as an answer to a say is.
    [Fact]
    public void AnAnswerOnTheBroadcastChannel_StaysThereWhileSneaking()
    {
        Engine e = new();
        e.Commands.StealthedProvider = () => true;

        e.Commands.DispatchForTests(Broadcast("Friend", "@health"));
        e.Commands.DispatchForTests(new ChatLogEntry(Now, ChatChannel.Local, "Friend", "@health", "raw"));

        Assert.Equal(new[] { "br {HP=1/1}\r", "/Friend {HP=1/1}\r" }, e.Wire);
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
    // ticked: nothing. Unticked, each line is held to the gates of any other
    // channel (ExpectedOnBroadcast spells them out), a handler runs only for a
    // player who holds the command's permission, and a relay-back sends the
    // command back on the channel only for a player with the grant for that.
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void OnTheBroadcastChannel_EveryLineIsGatedAsOnTheOtherChannels(bool open, bool warn)
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
    // is run for it. The gates, in the engine's order: the box, the relay-back,
    // a word that is no command, the silent blocks, the permission, the suicide
    // rule, and then the handler's own answer. Nobody here is in a party, so a
    // command for party members is denied to both players.
    private static (string[] Wire, bool HandlerRuns) ExpectedOnBroadcast(string sender, string line, bool open, bool warn)
    {
        string[] none = Array.Empty<string>();
        string[] failure = warn ? new[] { "br {command invalid or not allowed}\r" } : none;
        if (!open) return (none, false);
        bool relay = line.StartsWith("&@", StringComparison.Ordinal);
        string[] tokens = (relay ? line[1..] : line).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string command = tokens[0].TrimEnd('!');
        string[] args = tokens[1..];
        if (!command.StartsWith('@') || !RemoteCommandCatalog.Help.ContainsKey(command)) return (none, false);
        // A relay-back runs nothing here. A known command is sent back bare, as it
        // was typed, for a player with the grant; a blocked word in it is never
        // relayed, a help question included.
        if (relay)
        {
            if (args.Any(a => a.Contains("reroll"))) return (none, false);
            // A command for telepath and gangpath only is put on no channel by us.
            if (RemoteCommandCatalog.IsPathChannelOnly(command)) return (none, false);
            if (sender == "Stranger") return (failure, false);
            // What `@do` may not send for a player, a relay may not either.
            if (command == "@suicide") return (warn ? new[] { "br {suicide is not relayed}\r" } : none, false);
            return (new[] { $"br {line[1..]}\r" }, false);
        }
        // A blocked word anywhere in the line is never answered, on any channel.
        // Asking what a command does is not sending it.
        if (command != "@help" && args.Any(a => a.Contains("reroll"))) return (none, false);
        // Handing out trust is for telepath and gangpath: dropped without a word.
        if (RemoteCommandCatalog.IsPathChannelOnly(command)) return (none, false);
        RemoteCommandCatalog.TryGetCategory(command, out PlayerRemoteControls category);
        if (sender == "Stranger" || category == PlayerRemoteControls.None) return (failure, false);
        if (command == "@suicide")
            return (warn ? new[] { "br {suicide blocked, lives unknown to client}\r" } : none, false);

        if (command == "@health") return (new[] { "br {HP=1/1}\r" }, true);
        if (command is "@timer" or "@roomba" or "@loop")
        {
            if (TransferChannels.Identify(command, args) is not { } transfer) return (new[] { "br {ordinary answer}\r" }, true);
            if (transfer == Transfer.TimerSync) return (new[] { "br {data}\r" }, true);
            return (warn ? new[] { $"br {{{TransferChannels.Refusal(transfer)}}}\r" } : none, true);
        }
        // The stand-in for every other command answers, then refuses, then fails:
        // both lines are one answer, and the refusal goes by the Warn setting.
        return (warn ? new[] { "br {an answer}\r", "br {a refusal}\r" } : new[] { "br {an answer}\r" }, true);
    }

    // The remote commands for Events are ordinary remote commands: on the broadcast
    // channel they are read like any other, from a player who holds the permission,
    // and denied to one who does not.
    [Theory]
    [InlineData("@events", "@events")]
    [InlineData("@event 1 on", "@event")]
    [InlineData("@event daily bank off", "@event")]
    [InlineData("@event! 1", "@event")]
    public void TheEventCommandsOnTheBroadcastChannel_AreReadLikeAnyOther(string line, string command)
    {
        Engine e = new(everyCommand: true);

        e.Commands.DispatchForTests(Broadcast("Friend", line));
        Assert.Equal(new[] { command }, e.Ran);
        Assert.Equal(new[] { "br {an answer}\r", "br {a refusal}\r" }, e.Wire);

        e.Commands.DispatchForTests(Broadcast("Stranger", line));
        Assert.Equal(new[] { command }, e.Ran);
        Assert.Equal("br {command invalid or not allowed}\r", e.Wire.Last());
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

    // The bound holds for every command. A second one from a speaker already
    // answered this minute is passed over before anything runs for it: a command
    // that would get no answer is not carried out either. A minute on, it is.
    [Fact]
    public void OnTheBroadcastChannel_ASecondCommandInsideTheMinute_IsNeitherRunNorAnswered()
    {
        Engine e = new(everyCommand: true);

        e.Commands.DispatchForTests(Broadcast("Friend", "@health"));
        e.Commands.DispatchForTests(Broadcast("Friend", "@stop"));
        e.Commands.DispatchForTests(Broadcast("Friend", "@health"));

        Assert.Equal(new[] { "@health" }, e.Ran);
        Assert.Equal("br {HP=1/1}\r", e.Wire.Single());
        Assert.Contains(DebugLines(e), l => l ==
            "Passing over @stop from Friend on the broadcast channel: they were answered there less than a minute ago.");

        e.Clock += BroadcastAnswerBudget.Window;
        e.Commands.DispatchForTests(Broadcast("Friend", "@stop"));

        Assert.Equal(new[] { "@health", "@stop" }, e.Ran);
        Assert.Equal(new[] { "br {HP=1/1}\r", "br {an answer}\r", "br {a refusal}\r" }, e.Wire);
    }

    // Players nobody granted anything draw the channel's few refusals a minute and
    // no more, and cannot use up a granted player's turn: the two are counted apart.
    [Fact]
    public void OnTheBroadcastChannel_RefusalsToStrangers_CostAGrantedPlayerNothing()
    {
        const int cap = BroadcastAnswerBudget.AnswersPerWindow;
        Engine e = new();
        for (int i = 0; i < cap + 3; i++)
            e.Commands.DispatchForTests(Broadcast($"Passerby{i}", "@health"));
        Assert.Equal(cap, e.Wire.Length);
        Assert.All(e.Wire, line => Assert.Equal("br {command invalid or not allowed}\r", line));

        e.Commands.DispatchForTests(Broadcast("Friend", "@health"));

        Assert.Equal(new[] { "@health" }, e.Ran);
        Assert.Equal("br {HP=1/1}\r", e.Wire.Last());
    }

    // The granted players have the same few answers a minute between them: once
    // those are given, the next granted player's command is neither run nor
    // answered, and a stranger is still told no.
    [Fact]
    public void OnTheBroadcastChannel_OnceTheGrantedPlayersHaveHadTheMinutesAnswers_NoCommandRuns()
    {
        const int cap = BroadcastAnswerBudget.AnswersPerWindow;
        Engine e = new();
        for (int i = 0; i <= cap; i++) e.Grant($"Granted{i}");

        for (int i = 0; i < cap; i++)
            e.Commands.DispatchForTests(Broadcast($"Granted{i}", "@health"));
        Assert.Equal(cap, e.Ran.Count);

        e.Commands.DispatchForTests(Broadcast($"Granted{cap}", "@health"));
        Assert.Equal(cap, e.Ran.Count);
        Assert.Equal(cap, e.Wire.Length);
        Assert.Contains(DebugLines(e), l => l ==
            $"Passing over @health from Granted{cap} on the broadcast channel: {cap} answers were given there in the last minute.");

        e.Commands.DispatchForTests(Broadcast("Stranger", "@health"));
        Assert.Equal("br {command invalid or not allowed}\r", e.Wire.Last());
        Assert.Equal(cap, e.Ran.Count);

        // A minute after the first of those answers, there is room for one more.
        e.Clock += BroadcastAnswerBudget.Window;
        e.Commands.DispatchForTests(Broadcast($"Granted{cap}", "@health"));
        Assert.Equal(cap + 1, e.Ran.Count);
    }

    // One speaker, one answer a minute, whichever kind it is: a player who holds
    // some permissions and not others does not draw an answer and then a refusal,
    // or a refusal and then an answer. The second line inside the minute is passed
    // over, and a command among them is not run; a minute on, it is.
    [Theory]
    [InlineData("@health", "br {HP=1/1}\r", "@timer")]
    [InlineData("@timer", "br {command invalid or not allowed}\r", "@health")]
    public void OnTheBroadcastChannel_ASpeakerHasOneAnswerAMinute_AcrossAnswersAndRefusals(
        string first, string firstAnswer, string second)
    {
        Engine e = new();
        e.Grant("Half", PlayerRemoteControls.QueryHealthStatus);

        e.Commands.DispatchForTests(Broadcast("Half", first));
        e.Commands.DispatchForTests(Broadcast("Half", second));

        Assert.Equal(firstAnswer, e.Wire.Single());
        Assert.Equal(first == "@health" ? 1 : 0, e.Ran.Count);
        Assert.Contains(DebugLines(e), l => l ==
            $"Passing over {second} from Half on the broadcast channel: they were answered there less than a minute ago.");

        e.Clock += BroadcastAnswerBudget.Window;
        e.Commands.DispatchForTests(Broadcast("Half", second));
        Assert.Equal(2, e.Wire.Length);
        Assert.Single(e.Ran);
    }

    // What `@do` may not send for a player, a relay may not send for them: the
    // words go out as ours either way. `&@suicide` and `&@do suicide` are refused
    // on every channel, to a player with the grant by the Warn setting and with the
    // reason, and to one without it with the Failure message alone. A command the
    // `@do` rule does not cover is relayed as ever.
    [Theory]
    [InlineData(RemoteChannel.Telepath, "/Friend ")]
    [InlineData(RemoteChannel.Gangpath, "bg ")]
    [InlineData(RemoteChannel.Local, ">Friend ")]
    [InlineData(RemoteChannel.Broadcast, "br ")]
    public void ARelayBackOfSuicide_IsRefusedAsDoIs_OnEveryChannel(RemoteChannel channel, string verb)
    {
        foreach (string line in new[] { "&@suicide", "&@suicide!", "&@do suicide", "&@do br @suicide", "&@do /Friend \"suicide\"" })
        {
            Engine e = new(everyCommand: true);
            e.Commands.DispatchForTests(On(channel, "Friend", line));
            Assert.Equal($"{verb}{{suicide is not relayed}}\r", e.Wire.Single());
            Assert.Empty(e.Ran);

            Engine quiet = new(warn: false, everyCommand: true);
            quiet.Commands.DispatchForTests(On(channel, "Friend", line));
            Assert.Empty(quiet.Wire);

            Engine stranger = new(everyCommand: true);
            stranger.Commands.DispatchForTests(On(channel, "Stranger", line));
            Assert.EndsWith("{command invalid or not allowed}\r", stranger.Wire.Single());
        }

        Engine other = new(everyCommand: true);
        other.Commands.DispatchForTests(On(channel, "Friend", "&@hangup"));
        Assert.Equal($"{verb}@hangup\r", other.Wire.Single());
        // A word that only contains it is not the command.
        other.Clock += TimeSpan.FromMinutes(2);
        other.Commands.DispatchForTests(On(channel, "Friend", "&@do say suicidal"));
        Assert.Equal($"{verb}@do say suicidal\r", other.Wire.Last());
    }

    // A block that never answers is on record at Info where its sender had to reach
    // us by telepath, in our gang or our room. On the broadcast channel anyone can
    // say such a line as often as they like, so it is logged at Debug there.
    [Theory]
    [InlineData("@do reroll", "Blocked @do from Stranger")]
    [InlineData("&@do reroll", "Blocked relay-back of @do for Stranger")]
    [InlineData("@dupe Friend", "Ignoring @dupe from Stranger on")]
    public void ASilentBlock_IsLoggedAtDebugOnTheBroadcastChannel_AndAtInfoElsewhere(string line, string logged)
    {
        Engine onBroadcast = new(everyCommand: true);
        onBroadcast.Commands.DispatchForTests(Broadcast("Stranger", line));
        LogEntry there = Assert.Single(onBroadcast.Log.Snapshot(), l => l.Message.StartsWith(logged));
        Assert.Equal(LogSeverity.Debug, there.Severity);

        Engine onSay = new(everyCommand: true);
        onSay.Commands.DispatchForTests(On(RemoteChannel.Local, "Stranger", line));
        LogEntry here = Assert.Single(onSay.Log.Snapshot(), l => l.Message.StartsWith(logged));
        Assert.Equal(LogSeverity.Info, here.Severity);
    }

    // A refusal a handler gives a granted player (a transfer on a channel it does
    // not use) is part of that player's answer, not one of the strangers' refusals.
    [Fact]
    public void OnTheBroadcastChannel_AGrantedPlayersRefusal_IsCountedWithTheAnswers()
    {
        const int cap = BroadcastAnswerBudget.AnswersPerWindow;
        Engine e = new();
        for (int i = 0; i < cap; i++) e.Grant($"Granted{i}");

        for (int i = 0; i < cap; i++)
            e.Commands.DispatchForTests(Broadcast($"Granted{i}", "@roomba sync"));
        Assert.Equal(cap, e.Wire.Length);

        // The strangers' refusals are all still to be had.
        for (int i = 0; i < cap; i++)
            e.Commands.DispatchForTests(Broadcast($"Passerby{i}", "@roomba sync"));
        Assert.Equal(2 * cap, e.Wire.Length);
    }

    // What a handler says after the dispatch has returned (a result that follows
    // an acceptance) is part of the answer its command began: it goes out, and is
    // not counted again. A second command inside the minute is still passed over.
    [Fact]
    public void OnTheBroadcastChannel_AHandlersLaterLines_AreTheSameAnswer()
    {
        Engine e = new();
        Action<string>? later = null;
        Action<string?>? laterRefusal = null;
        e.Commands.RegisterHandler("@trap", PlayerRemoteControls.ExecuteCommands, ctx =>
        {
            e.Ran.Add("@trap");
            ctx.Reply("attempting");
            later = ctx.Reply;
            laterRefusal = ctx.Refusal;
        });

        e.Commands.DispatchForTests(Broadcast("Friend", "@trap n"));
        e.Clock += TimeSpan.FromSeconds(20);
        later!("disarmed");
        laterRefusal!("and one thing more");

        Assert.Equal(new[] { "br {attempting}\r", "br {disarmed}\r", "br {and one thing more}\r" }, e.Wire);
        Assert.Equal(1, e.Commands.BroadcastSpeakersRememberedForTests);

        e.Commands.DispatchForTests(Broadcast("Friend", "@trap s"));
        Assert.Single(e.Ran);
        Assert.Equal(3, e.Wire.Length);
    }

    // A handler that says nothing at first and answers only later begins its
    // answer then, under the bound as it stands at that moment.
    [Fact]
    public void OnTheBroadcastChannel_AnAnswerThatOnlyComesLater_IsBoundedWhenItComes()
    {
        Engine e = new();
        List<Action<string>> later = new();
        e.Commands.RegisterHandler("@goto", PlayerRemoteControls.MovePlayer, ctx => later.Add(ctx.Reply));

        e.Commands.DispatchForTests(Broadcast("Friend", "@goto bank"));
        Assert.Empty(e.Wire);
        // Nothing was said, so nothing was used up: the same player's next command runs.
        e.Commands.DispatchForTests(Broadcast("Friend", "@health"));
        Assert.Equal("br {HP=1/1}\r", e.Wire.Single());

        // The first command's answer now comes inside the minute of the second's.
        later[0]("arrived");
        Assert.Single(e.Wire);
        Assert.Contains(DebugLines(e), l => l.StartsWith("Not said to Friend on the broadcast channel:"));

        e.Clock += BroadcastAnswerBudget.Window;
        later[0]("arrived");
        later[0]("and resting");
        Assert.Equal(new[] { "br {HP=1/1}\r", "br {arrived}\r", "br {and resting}\r" }, e.Wire);
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

        // Those were refusals, to players nobody granted anything. They are counted
        // apart from what is said to a player who holds the permission, so a
        // granted player's timers do not wait behind them.
        e.Commands.DispatchForTests(Broadcast("Friend", "@timer sync"));
        Assert.Equal(new[] { "@timer sync" }, e.Ran);
        Assert.Equal("br {data}\r", e.Wire.Last());

        // The granted player's own minute still holds for them.
        e.Clock += TimeSpan.FromSeconds(10);
        e.Commands.DispatchForTests(Broadcast("Friend", "@timer sync"));
        Assert.Single(e.Ran);
        Assert.Equal(cap + 1, e.Wire.Length);

        // A minute after the first of the refusals, there is room for one more.
        e.Commands.DispatchForTests(Broadcast("Latecomer", "@timer sync"));
        Assert.Equal(cap + 2, e.Wire.Length);
        Assert.Equal("br {command invalid or not allowed}\r", e.Wire.Last());
        Assert.Equal(cap + 1, e.Commands.BroadcastSpeakersRememberedForTests);
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

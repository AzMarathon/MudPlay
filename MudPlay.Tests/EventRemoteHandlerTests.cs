using System.Collections.Specialized;
using System.IO;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Events;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.ViewModels.Settings;
using Xunit;
using Harness = MudPlay.Tests.EventEngineHarness;

namespace MudPlay.Tests;

// The remote @events and @event commands: a player granted Alter settings sees
// the character's Events and switches one on or off. What they may be told is an
// event's name and state, never what it sends.
public sealed class EventRemoteHandlerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);
    private const string Friend = "Friend";
    private const string Stranger = "Stranger";

    private readonly string _root;
    private readonly string _bbs = "test-remote-events-" + Path.GetRandomFileName();

    public EventRemoteHandlerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-remote-events-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
        try { Directory.Delete(AppPaths.BbsFolder(_bbs), recursive: true); }
        catch { /* best-effort */ }
    }

    private sealed class Rig : IDisposable
    {
        public required Harness H { get; init; }
        public required RemoteCommandManager Engine { get; init; }
        public required EventRemoteHandler Handler { get; init; }
        public bool MasterSwitchOff { get; set; }

        public EventManager Events => H.Events;

        public List<string> Sent => Engine.LastSentForTests.Select(b => Encoding.Latin1.GetString(b)).ToList();

        public void Tell(string sender, string message) => Engine.DispatchForTests(
            new ChatLogEntry(Now, ChatChannel.TelepathIncoming, sender, message, $"{sender} telepaths: {message}"));

        // One command from the trusted player, and the answers to it.
        public List<string> Ask(string message)
        {
            Engine.LastSentForTests.Clear();
            Tell(Friend, message);
            return Sent;
        }

        public void Dispose()
        {
            Handler.Dispose();
            Engine.Dispose();
            H.Dispose();
        }
    }

    // Friend holds Alter settings; Stranger holds everything but.
    private Rig Build()
    {
        Harness h = Harness.Create(_root, _bbs);
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PlayerDatabase players = new();
        players.RecordObservation(Friend, null, null, null, null, null, null, Now);
        players.EditCustomization(Friend, new PlayerCustomization(RemoteControls: PlayerRemoteControls.AlterSettings));
        players.RecordObservation(Stranger, null, null, null, null, null, null, Now);
        players.EditCustomization(Stranger,
            new PlayerCustomization(RemoteControls: PlayerRemoteControls.All & ~PlayerRemoteControls.AlterSettings));
        RemoteCommandManager engine = new(new ChatRouter(router), new PartyState(), players);
        Rig? rig = null;
        engine.BlockedByMasterSwitch = _ => rig!.MasterSwitchOff;
        rig = new Rig { H = h, Engine = engine, Handler = new EventRemoteHandler(engine, h.Events, h.Log) };
        return rig;
    }

    private static ScheduledEvent Command(string name, string text = "stat", bool disabled = false) => new()
    {
        Name = name, TriggerType = EventTriggerType.Every, EveryAmount = 30, EveryUnit = EventTimeUnit.Minutes,
        ActionType = EventActionType.Command, CommandText = text, Disabled = disabled,
    };

    private static ScheduledEvent BankTrip(string name) => new()
    {
        Name = name, TriggerType = EventTriggerType.AtTime, AtTime = "21:30", ActionType = EventActionType.BankTrip,
    };

    // A loop event whose loop isn't in the catalogue: the reconciler switches it
    // off the moment it is added.
    private static ScheduledEvent LoopNobodySaved(string name) => new()
    {
        Name = name, TriggerType = EventTriggerType.Logon, ActionType = EventActionType.Loop, LoopName = "Gone Loop",
    };

    private static string Reply(string text) => $"/{Friend} {{{text}}}\r";

    // ----- @events ---------------------------------------------------------

    [Fact]
    public void Events_WithNoneDefined_SaysSo()
    {
        using Rig r = Build();

        Assert.Equal(new[] { Reply("no events defined") }, r.Ask("@events"));
    }

    [Fact]
    public void Events_ListsEachByName_OnOrOff_InTheTabsOrder()
    {
        using Rig r = Build();
        r.Events.Add(Command("Night stats"));
        r.Events.Add(BankTrip("Bank run"));
        r.Events.Add(Command("Sell junk", disabled: true));

        Assert.Equal(new[] { Reply("events: Night stats on, Bank run on, Sell junk off") }, r.Ask("@events"));
    }

    [Fact]
    public void Events_MarksOneSwitchedOffForAMissingTarget()
    {
        using Rig r = Build();
        r.Events.Add(Command("Night stats"));
        r.Events.Add(LoopNobodySaved("Lair loop"));

        Assert.Equal(new[] { Reply("events: Night stats on, Lair loop off (target missing)") }, r.Ask("@events"));
    }

    [Fact]
    public void Events_WithDisableAllEventsTicked_SaysSoFirst()
    {
        using Rig r = Build();
        r.Events.Add(Command("Night stats"));
        r.H.Profile.Current!.EventsGloballyDisabled = true;

        Assert.Equal(new[] { Reply("events (all disabled): Night stats on") }, r.Ask("@events"));
    }

    [Fact]
    public void Events_AnUnnamedEvent_IsListedAsUnnamed()
    {
        using Rig r = Build();
        r.Events.Add(Command(""));

        Assert.Equal(new[] { Reply("events: (unnamed) on") }, r.Ask("@events"));
    }

    // More than one telepath holds: numbered parts, every event in one of them,
    // in order, and none over the line the game takes.
    [Fact]
    public void Events_MoreThanFitOneReply_AreSentInNumberedParts()
    {
        using Rig r = Build();
        for (int i = 1; i <= 30; i++) r.Events.Add(Command($"Scheduled event number {i:00}"));

        List<string> sent = r.Ask("@events");

        Assert.True(sent.Count > 1);
        for (int i = 0; i < sent.Count; i++)
        {
            Assert.StartsWith($"/{Friend} {{events ({i + 1}/{sent.Count}): ", sent[i]);
            Assert.True(sent[i].TrimEnd('\r').Length <= 225, sent[i]);
        }
        string all = string.Concat(sent);
        int at = -1;
        for (int i = 1; i <= 30; i++)
        {
            int found = all.IndexOf($"Scheduled event number {i:00} on", StringComparison.Ordinal);
            Assert.True(found > at, $"event {i} missing or out of order");
            at = found;
        }
    }

    // What an event sends is the user's own: a password typed into a command, a
    // loop's name. Neither form says any of it.
    [Fact]
    public void NoAnswer_CarriesWhatAnEventSends()
    {
        using Rig r = Build();
        r.Events.Add(Command("Log in helper", text: "hunter2secret"));
        r.Events.Add(LoopNobodySaved("Lair loop"));

        string everything = string.Concat(r.Ask("@events"))
            + string.Concat(r.Ask("@event log in helper"))
            + string.Concat(r.Ask("@event lair loop"))
            + string.Concat(r.Ask("@event lair loop on"));

        Assert.DoesNotContain("hunter2secret", everything);
        Assert.DoesNotContain("Gone Loop", everything);
    }

    [Fact]
    public void Events_WithWordsAfterIt_IsRefused_AndSwitchesNothing()
    {
        using Rig r = Build();
        ScheduledEvent e = Command("Night stats");
        r.Events.Add(e);

        Assert.Equal(new[] { Reply("usage: @events lists them; @event <name> on|off switches one") }, r.Ask("@events off"));
        Assert.False(e.Disabled);
    }

    // ----- @event <name> ----------------------------------------------------

    [Fact]
    public void Event_Bare_SaysItsStateAndItsKinds()
    {
        using Rig r = Build();
        r.Events.Add(BankTrip("Bank run"));
        r.Events.Add(Command("Sell junk", disabled: true));

        Assert.Equal(new[] { Reply("event 'Bank run' is on: fires at a set time, makes a bank trip") }, r.Ask("@event bank run"));
        Assert.Equal(new[] { Reply("event 'Sell junk' is off: fires on a timer, sends a command") }, r.Ask("@event sell junk"));
    }

    [Fact]
    public void Event_Bare_WithDisableAllEventsTicked_SaysSo()
    {
        using Rig r = Build();
        r.Events.Add(BankTrip("Bank run"));
        r.H.Profile.Current!.EventsGloballyDisabled = true;

        Assert.Equal(
            new[] { Reply("event 'Bank run' is on: fires at a set time, makes a bank trip; all events are disabled") },
            r.Ask("@event bank run"));
    }

    // An event whose own name ends in a state word can still be asked about.
    [Fact]
    public void Event_WhoseWholeNameEndsInOn_IsAskedAbout_NotSwitched()
    {
        using Rig r = Build();
        ScheduledEvent e = Command("Lights on", disabled: true);
        r.Events.Add(e);

        Assert.Equal(new[] { Reply("event 'Lights on' is off: fires on a timer, sends a command") }, r.Ask("@event lights on"));
        Assert.True(e.Disabled);

        Assert.Equal(new[] { Reply("event 'Lights on' switched on") }, r.Ask("@event lights on on"));
        Assert.False(e.Disabled);
    }

    // ----- @event <name> on | off --------------------------------------------

    [Theory]
    [InlineData("off")]
    [InlineData("disable")]
    [InlineData("OFF")]
    public void Event_Off_ByExactName_SwitchesItOff(string word)
    {
        using Rig r = Build();
        ScheduledEvent e = BankTrip("Bank run");
        r.Events.Add(e);

        Assert.Equal(new[] { Reply("event 'Bank run' switched off") }, r.Ask($"@event Bank run {word}"));
        Assert.True(e.Disabled);
        Assert.False(r.Events.IsAutoDisabled(e));
    }

    [Theory]
    [InlineData("on")]
    [InlineData("enable")]
    public void Event_On_SwitchesItOn(string word)
    {
        using Rig r = Build();
        ScheduledEvent e = Command("Sell junk", disabled: true);
        r.Events.Add(e);

        Assert.Equal(new[] { Reply("event 'Sell junk' switched on") }, r.Ask($"@event sell junk {word}"));
        Assert.False(e.Disabled);
    }

    // The words of the name in any order, as @loop takes a loop's.
    [Fact]
    public void Event_ByItsWordsInAnyOrder_IsFound()
    {
        using Rig r = Build();
        ScheduledEvent e = BankTrip("Evening bank run");
        r.Events.Add(e);
        r.Events.Add(Command("Night stats"));

        Assert.Equal(new[] { Reply("event 'Evening bank run' switched off") }, r.Ask("@event run bank off"));
        Assert.True(e.Disabled);
    }

    // A name that is one event's whole name wins over others that only hold it.
    [Fact]
    public void Event_AnExactName_WinsOverNamesThatContainIt()
    {
        using Rig r = Build();
        ScheduledEvent exact = BankTrip("Bank");
        r.Events.Add(exact);
        r.Events.Add(BankTrip("Bank run"));

        Assert.Equal(new[] { Reply("event 'Bank' switched off") }, r.Ask("@event bank off"));
        Assert.True(exact.Disabled);
    }

    [Fact]
    public void Event_ANameThatFitsSeveral_IsRefusedWithTheCandidates_AndSwitchesNothing()
    {
        using Rig r = Build();
        ScheduledEvent a = BankTrip("Morning bank run");
        ScheduledEvent b = BankTrip("Evening bank run");
        r.Events.Add(a);
        r.Events.Add(b);

        Assert.Equal(
            new[] { Reply("event name 'bank run' matches 2: 'Morning bank run', 'Evening bank run'") },
            r.Ask("@event bank run off"));
        Assert.False(a.Disabled);
        Assert.False(b.Disabled);
    }

    [Fact]
    public void Event_ANameNothingFits_IsRefused()
    {
        using Rig r = Build();
        r.Events.Add(BankTrip("Bank run"));

        Assert.Equal(new[] { Reply("no event named 'boss hunt'") }, r.Ask("@event boss hunt off"));
        Assert.Equal(new[] { Reply("no event named 'boss hunt'") }, r.Ask("@event boss hunt"));
    }

    [Theory]
    [InlineData("@event")]
    [InlineData("@event on")]
    [InlineData("@event off")]
    public void Event_WithNoName_IsRefusedWithTheUsage(string command)
    {
        using Rig r = Build();
        r.Events.Add(BankTrip("Bank run"));

        Assert.Equal(new[] { Reply("usage: @event <name> [on|off]") }, r.Ask(command));
    }

    [Fact]
    public void Event_AlreadyInThatState_SaysSo_AndSavesNothing()
    {
        using Rig r = Build();
        ScheduledEvent on = BankTrip("Bank run");
        ScheduledEvent off = Command("Sell junk", disabled: true);
        r.Events.Add(on);
        r.Events.Add(off);
        int changes = 0;
        r.Events.Events.CollectionChanged += (_, _) => changes++;

        Assert.Equal(new[] { Reply("event 'Bank run' is already on") }, r.Ask("@event bank run on"));
        Assert.Equal(new[] { Reply("event 'Sell junk' is already off") }, r.Ask("@event sell junk off"));
        Assert.Equal(0, changes);
    }

    // An event the client switched off because its loop is gone could not run:
    // a remote command doesn't switch it on.
    [Fact]
    public void Event_On_ForOneWhoseTargetIsMissing_IsRefused()
    {
        using Rig r = Build();
        ScheduledEvent e = LoopNobodySaved("Lair loop");
        r.Events.Add(e);
        Assert.True(r.Events.IsAutoDisabled(e));

        Assert.Equal(new[] { Reply("event 'Lair loop' can't be switched on: its loop is missing") }, r.Ask("@event lair loop on"));
        Assert.True(e.Disabled);
        Assert.Equal(new[] { Reply("event 'Lair loop' is off (target missing): fires on logon, runs a loop") }, r.Ask("@event lair loop"));
    }

    // Switched off in the middle of its run: the run goes on to its end, and the
    // answer says so.
    [Fact]
    public void Event_Off_WhileItsRunIsUnderWay_SaysTheRunFinishes()
    {
        using Rig r = Build();
        ScheduledEvent e = new()
        {
            Name = "Hold still", TriggerType = EventTriggerType.Every, EveryAmount = 1, EveryUnit = EventTimeUnit.Hours,
            ActionType = EventActionType.Wait, WaitSeconds = 600, Then = EventThenType.Nothing,
        };
        r.Events.Add(e);
        Assert.True(r.Events.Fire(e));
        Assert.True(r.Events.IsRunning(e));

        Assert.Equal(new[] { Reply("event 'Hold still' switched off; the run it has under way finishes") }, r.Ask("@event hold still off"));
        Assert.True(r.Events.IsRunning(e));
        Assert.False(r.Events.Fire(e));            // and it fires no more
    }

    [Fact]
    public void Event_On_WithDisableAllEventsTicked_SaysItWontFireYet()
    {
        using Rig r = Build();
        r.Events.Add(Command("Sell junk", disabled: true));
        r.H.Profile.Current!.EventsGloballyDisabled = true;

        Assert.Equal(
            new[] { Reply("event 'Sell junk' switched on; all events are disabled, so it won't fire yet") },
            r.Ask("@event sell junk on"));
    }

    // ----- who is obeyed, and what a refusal gives away -------------------------

    // Without the grant: the generic failure and nothing else, whether or not an
    // event of that name exists.
    [Theory]
    [InlineData("@events")]
    [InlineData("@event bank run")]
    [InlineData("@event bank run off")]
    [InlineData("@event no such thing")]
    [InlineData("@event no such thing off")]
    public void WithoutAlterSettings_OnlyTheGenericFailure_WhateverWasNamed(string command)
    {
        using Rig r = Build();
        ScheduledEvent e = BankTrip("Bank run");
        r.Events.Add(e);

        r.Tell(Stranger, command);

        Assert.Equal(new[] { $"/{Stranger} {{command invalid or not allowed}}\r" }, r.Sent);
        Assert.False(e.Disabled);
    }

    [Fact]
    public void WithoutAlterSettings_AndWarnOff_Silence()
    {
        using Rig r = Build();
        r.Events.Add(BankTrip("Bank run"));
        r.Engine.WarnOnDenial = false;

        r.Tell(Stranger, "@events");
        r.Tell(Stranger, "@event bank run off");

        Assert.Empty(r.Sent);
    }

    // The Warn setting silences every refusal and no answer.
    [Fact]
    public void WarnOff_RefusalsAreSilent_AnswersAreNot()
    {
        using Rig r = Build();
        r.Events.Add(BankTrip("Morning bank run"));
        r.Events.Add(BankTrip("Evening bank run"));
        r.Events.Add(LoopNobodySaved("Lair loop"));
        r.Engine.WarnOnDenial = false;

        Assert.Empty(r.Ask("@event"));
        Assert.Empty(r.Ask("@event boss hunt off"));
        Assert.Empty(r.Ask("@event bank run off"));
        Assert.Empty(r.Ask("@event lair loop on"));
        Assert.Empty(r.Ask("@events now"));

        Assert.Single(r.Ask("@events"));
        Assert.Single(r.Ask("@event morning bank run"));
        Assert.Equal(new[] { Reply("event 'Morning bank run' switched off") }, r.Ask("@event morning bank run off"));
    }

    // With the master switch off no remote command but @auto-all is followed.
    [Fact]
    public void MasterSwitchOff_NeitherIsFollowed_NorAnswered()
    {
        using Rig r = Build();
        ScheduledEvent e = BankTrip("Bank run");
        r.Events.Add(e);
        r.MasterSwitchOff = true;

        Assert.Empty(r.Ask("@events"));
        Assert.Empty(r.Ask("@event bank run off"));
        Assert.False(e.Disabled);
    }

    [Fact]
    public void ATrailingBang_OnEitherCommand_IsIgnored()
    {
        using Rig r = Build();
        ScheduledEvent e = BankTrip("Bank run");
        r.Events.Add(e);

        Assert.Equal(new[] { Reply("events: Bank run on") }, r.Ask("@events!"));
        Assert.Equal(new[] { Reply("event 'Bank run' switched off") }, r.Ask("@event! bank run off"));
        Assert.True(e.Disabled);
    }

    [Fact]
    public void BothAnswer_OnTheChannelTheyCameInOn()
    {
        using Rig r = Build();
        r.Events.Add(BankTrip("Bank run"));

        r.Engine.DispatchForTests(new ChatLogEntry(Now, ChatChannel.Gangpath, Friend, "@events", $"{Friend} gangpaths: @events"));
        r.Engine.DispatchForTests(new ChatLogEntry(Now, ChatChannel.Local, Friend, "@event bank run", $"{Friend} says: @event bank run"));

        Assert.Equal("bg {events: Bank run on}\r", r.Sent[0]);
        Assert.StartsWith($">{Friend} {{event 'Bank run' is on", r.Sent[1]);
    }

    // ----- the local API --------------------------------------------------------

    [Fact]
    public void LocalInvocation_ListsAndSwitches_AndIsToldWhyOnARefusal()
    {
        using Rig r = Build();
        ScheduledEvent e = BankTrip("Bank run");
        r.Events.Add(e);
        r.Engine.WarnOnDenial = false;      // the caller has the machine: told all the same
        List<string> replies = new();

        Assert.Equal(RemoteCommandManager.LocalInvokeResult.Ok, r.Engine.TryInvokeLocal("@events", null, replies.Add));
        Assert.Equal(RemoteCommandManager.LocalInvokeResult.Ok,
            r.Engine.TryInvokeLocal("event", new[] { "bank", "run", "off" }, replies.Add));
        Assert.Equal(RemoteCommandManager.LocalInvokeResult.Ok,
            r.Engine.TryInvokeLocal("@event", new[] { "boss", "hunt", "on" }, replies.Add));

        Assert.Equal(
            new[] { "events: Bank run on", "event 'Bank run' switched off", "no event named 'boss hunt'" }, replies);
        Assert.True(e.Disabled);
        Assert.Empty(r.Sent);               // nothing went out on a chat channel
        Assert.False(LocalApiActions_IsDestructive("@event"));
    }

    private static bool LocalApiActions_IsDestructive(string command) =>
        MudPlay.Services.Api.LocalApiActions.IsDestructive(command);

    [Fact]
    public void LocalInvocation_WithTheMasterSwitchOff_IsNotFollowed()
    {
        using Rig r = Build();
        ScheduledEvent e = BankTrip("Bank run");
        r.Events.Add(e);
        r.MasterSwitchOff = true;

        Assert.Equal(RemoteCommandManager.LocalInvokeResult.MasterSwitchOff,
            r.Engine.TryInvokeLocal("@event", new[] { "bank", "run", "off" }, _ => { }));
        Assert.False(e.Disabled);
    }

    // ----- what a switch does elsewhere -----------------------------------------

    // The list raises a Replace for the event: what restarts the scheduler's
    // timers and redraws an open Events tab.
    [Fact]
    public void ASwitch_RaisesAReplaceForThatEvent()
    {
        using Rig r = Build();
        r.Events.Add(Command("Night stats"));
        ScheduledEvent e = BankTrip("Bank run");
        r.Events.Add(e);
        List<NotifyCollectionChangedEventArgs> raised = new();
        r.Events.Events.CollectionChanged += (_, args) => raised.Add(args);

        r.Ask("@event bank run off");

        NotifyCollectionChangedEventArgs change = Assert.Single(raised);
        Assert.Equal(NotifyCollectionChangedAction.Replace, change.Action);
        Assert.Equal(1, change.NewStartingIndex);
        Assert.Same(e, change.NewItems![0]);
        Assert.Same(e, r.Events.Events[1]);
    }

    // The Events tab, open while a remote command switches an event: the row shows
    // it at once, and the selection stays where it was.
    [Fact]
    public void TheOpenEventsTab_ShowsARemoteSwitch_AndKeepsItsSelection()
    {
        using Rig r = Build();
        r.Events.Add(Command("Night stats"));
        r.Events.Add(BankTrip("Bank run"));
        using EventsSectionViewModel tab = new(r.Events, r.H.Profile);
        tab.SelectedRow = tab.Rows[1];
        Assert.False(tab.Rows[1].ShowsOffBadge);

        r.Ask("@event bank run off");

        Assert.True(tab.Rows[1].IsDisabled);
        Assert.True(tab.Rows[1].ShowsOffBadge);
        Assert.False(tab.Rows[0].ShowsOffBadge);
        Assert.Same(r.Events.Events[1], tab.SelectedRow!.Source);

        r.Ask("@event bank run on");
        Assert.False(tab.Rows[1].ShowsOffBadge);
    }

    // An event the reconciler switched off shows the target-missing badge, not
    // the plain "off" one.
    [Fact]
    public void TheOpenEventsTab_AnAutoDisabledEvent_ShowsTargetMissing_NotOff()
    {
        using Rig r = Build();
        r.Events.Add(LoopNobodySaved("Lair loop"));
        using EventsSectionViewModel tab = new(r.Events, r.H.Profile);

        Assert.True(tab.Rows[0].IsAutoDisabled);
        Assert.False(tab.Rows[0].ShowsOffBadge);
    }

    // The edit window, open on an event a remote command then switches: its
    // Disabled box moves with the switch and what else was typed there stays, so
    // Save keeps both, and the note of who switched it.
    [Fact]
    public void TheOpenEditor_FollowsARemoteSwitch_AndSaveKeepsItWithTheOtherEdits()
    {
        using Rig r = Build();
        r.Events.Add(Command("Night stats"));
        ScheduledEvent e = Command("Sell junk");
        r.Events.Add(e);
        EventEditDialogViewModel editor = new(e, isNew: false) { Name = "Sell the junk", CommandText = "sell all" };
        Action stop = editor.FollowSwitches(r.Events.Events, e);
        Assert.False(editor.DisabledFlag);

        r.Ask("@event night stats off");            // another event: not this window's
        Assert.False(editor.DisabledFlag);
        r.Ask("@event sell junk off");

        Assert.True(editor.DisabledFlag);
        Assert.Equal("Sell the junk", editor.Name);
        Assert.Equal("sell all", editor.CommandText);

        ScheduledEvent? saved = null;
        editor.CloseRequested += result => saved = result;
        editor.SaveCommand.Execute(null);
        stop();
        Assert.True(r.Events.Replace(e, saved!));

        Assert.True(saved!.Disabled);
        Assert.Equal("Sell the junk", saved.Name);
        Assert.Equal("sell all", saved.CommandText);
        Assert.StartsWith("switched off by remote @event from Friend at ", r.Events.LastSwitch(saved));
    }

    // Cancel saves nothing, so the switch made while the window was open stands.
    // The box also stays the user's to set: ticked back before Save, the event is
    // saved on, and the note of the remote switch goes with the state it described.
    [Fact]
    public void TheOpenEditor_Cancelled_LeavesTheRemoteSwitch_AndTheBoxStaysTheUsers()
    {
        using Rig r = Build();
        ScheduledEvent e = Command("Sell junk");
        r.Events.Add(e);
        EventEditDialogViewModel cancelled = new(e, isNew: false) { Name = "Never saved" };
        Action stop = cancelled.FollowSwitches(r.Events.Events, e);
        r.Ask("@event sell junk off");
        bool closed = false;
        ScheduledEvent? result = e;
        cancelled.CloseRequested += given => { closed = true; result = given; };
        cancelled.CancelCommand.Execute(null);
        stop();

        Assert.True(closed);
        Assert.Null(result);
        Assert.True(e.Disabled);
        Assert.Equal("Sell junk", e.Name);
        Assert.Same(e, r.Events.Events[0]);

        // Once the window is closed nothing follows any more.
        r.Ask("@event sell junk on");
        Assert.True(cancelled.DisabledFlag);

        EventEditDialogViewModel overruled = new(e, isNew: false);
        Action stopSecond = overruled.FollowSwitches(r.Events.Events, e);
        r.Ask("@event sell junk off");
        Assert.True(overruled.DisabledFlag);
        overruled.DisabledFlag = false;
        ScheduledEvent? saved = null;
        overruled.CloseRequested += given => saved = given;
        overruled.SaveCommand.Execute(null);
        stopSecond();
        r.Events.Replace(e, saved!);

        Assert.False(saved!.Disabled);
        Assert.Null(r.Events.LastSwitch(saved));
    }

    // Logged at Info with who and which way; shown on the bug report's event line.
    [Fact]
    public void ASwitch_IsLoggedWithWhoAsked_AndKeptForTheBugReport()
    {
        using Rig r = Build();
        ScheduledEvent e = BankTrip("Bank run");
        r.Events.Add(e);
        Assert.Null(r.Events.LastSwitch(e));

        r.Ask("@event bank run off");

        Assert.Contains(r.H.EventLog, line => line.Contains("Event 'Bank run' switched off by remote @event from Friend"));
        Assert.StartsWith("switched off by remote @event from Friend at ", r.Events.LastSwitch(e));
    }

    // Saved with the character at once, as the edit window's box is: loaded
    // again, the event is still off.
    [Fact]
    public void ASwitch_IsSavedWithTheCharacter_AndStillSetAfterAReload()
    {
        new ProfileService().CreateProfile(_bbs, "Tester");
        using Rig r = Build();
        r.H.Profile.Load(_bbs, "Tester");
        r.Events.Add(BankTrip("Bank run"));
        r.Events.Add(Command("Sell junk", disabled: true));

        r.Ask("@event bank run off");
        r.Ask("@event sell junk on");

        ProfileService reloaded = new();
        CharacterProfile again = reloaded.Load(_bbs, "Tester");
        Assert.True(again.Events!.Single(e => e.Name == "Bank run").Disabled);
        Assert.False(again.Events!.Single(e => e.Name == "Sell junk").Disabled);
    }

    // ----- EventManager.FindByName, the rule @loop uses for a loop ----------------

    [Fact]
    public void FindByName_ExactThenWordsInAnyOrder_ThenWithoutApostrophes()
    {
        using Rig r = Build();
        r.Events.Add(BankTrip("King's Road run"));
        r.Events.Add(BankTrip("Bank"));
        r.Events.Add(BankTrip("Bank run"));
        r.Events.Add(Command(""));

        Assert.Equal(new[] { "Bank" }, r.Events.FindByName("BANK").Select(e => e.Name));
        Assert.Equal(new[] { "King's Road run", "Bank run" }, r.Events.FindByName("run").Select(e => e.Name));
        Assert.Equal(new[] { "King's Road run" }, r.Events.FindByName("kings road").Select(e => e.Name));
        Assert.Empty(r.Events.FindByName("nothing like it"));
        Assert.Empty(r.Events.FindByName("  "));
    }

    // Two events of one name: an exact name fits both, and neither is picked.
    [Fact]
    public void Event_TwoEventsOfOneName_IsAnAmbiguity()
    {
        using Rig r = Build();
        r.Events.Add(BankTrip("Bank run"));
        r.Events.Add(Command("Bank run"));

        Assert.Equal(new[] { Reply("event name 'bank run' matches 2: 'Bank run', 'Bank run'") }, r.Ask("@event bank run off"));
    }

    // ----- the other readers of a braced reply leave these alone -----------------

    private static string Inner(string sent)
    {
        string opening = $"/{Friend} {{";
        Assert.StartsWith(opening, sent);
        Assert.EndsWith("}\r", sent);
        return sent[opening.Length..^2];
    }

    // An event's name is the user's and can say anything: a purse, a room, a
    // refusal. Every answer and refusal either command gives opens with fixed
    // words, so each is known as one of theirs whatever the name inside it.
    [Theory]
    [InlineData("Sell run (= 5 copper)")]
    [InlineData("Camp (map 1, room 5)")]
    [InlineData("Farm 2")]
    [InlineData("I can't come")]
    [InlineData("going idle")]
    [InlineData("no ailments check")]
    [InlineData("ailments: blind")]
    [InlineData("sending loop 'x'")]
    public void EveryReply_IsKnownAsOne_WhateverTheEventIsNamed(string name)
    {
        using Rig r = Build();
        List<string> wire = new();
        wire.AddRange(r.Ask("@events"));                          // none defined
        wire.AddRange(r.Ask("@events off"));                      // the list's usage
        wire.AddRange(r.Ask("@event"));                           // the one-event usage

        ScheduledEvent e = Command(name);
        r.Events.Add(e);
        wire.AddRange(r.Ask("@events"));
        wire.AddRange(r.Ask($"@event {name}"));                   // asked about
        wire.AddRange(r.Ask($"@event {name} off"));
        wire.AddRange(r.Ask($"@event {name} off"));               // already off
        wire.AddRange(r.Ask($"@event {name} on"));
        wire.AddRange(r.Ask($"@event {name} on"));                // already on
        wire.AddRange(r.Ask($"@event {name} nowhere"));           // nothing of that name

        r.Events.Add(Command(name));
        wire.AddRange(r.Ask($"@event {name}"));                   // fits two
        r.Events.Remove(r.Events.Events[1]);

        ScheduledEvent lost = LoopNobodySaved(name + " loop");
        r.Events.Add(lost);
        wire.AddRange(r.Ask($"@event {name} loop on"));           // target missing
        wire.AddRange(r.Ask("@events"));
        r.Events.Remove(lost);

        ScheduledEvent held = new()
        {
            Name = name + " hold", TriggerType = EventTriggerType.Every, EveryAmount = 1, EveryUnit = EventTimeUnit.Hours,
            ActionType = EventActionType.Wait, WaitSeconds = 600, Then = EventThenType.Nothing,
        };
        r.Events.Add(held);
        Assert.True(r.Events.Fire(held));
        wire.AddRange(r.Ask($"@event {name} hold off"));          // its run finishes

        r.H.Profile.Current!.EventsGloballyDisabled = true;
        wire.AddRange(r.Ask("@events"));                          // all disabled
        wire.AddRange(r.Ask($"@event {name}"));
        wire.AddRange(r.Ask($"@event {name} off"));
        wire.AddRange(r.Ask($"@event {name} on"));                // won't fire yet
        for (int i = 0; i < 30; i++) r.Events.Add(Command($"{name} {i:00}"));
        wire.AddRange(r.Ask("@events"));                          // numbered parts

        Assert.True(wire.Count >= 20, $"{wire.Count} replies");
        Assert.All(wire, sent => Assert.True(EventRemoteHandler.IsEventReplyLine(Inner(sent)), sent));
        Assert.All(wire, sent => Assert.True(UserNamedReply.IsLine(Inner(sent)), sent));
        Assert.All(wire, sent => Assert.True(UserNamedReply.IsMessage($"{{{Inner(sent)}}}"), sent));
    }

    [Theory]
    [InlineData("I can't come, forget me")]
    [InlineData("Standing; not moving; no ailments")]
    [InlineData("eventually coming to your location")]
    [InlineData("events are fun")]
    [InlineData("event horizon (map 1, room 5)")]
    [InlineData("1,200 gold (= 12,000 copper)")]
    [InlineData("MudPlay 3.179.0")]
    public void OtherReplies_AreNotEventReplies(string inner)
    {
        Assert.False(EventRemoteHandler.IsEventReplyLine(inner));
        Assert.False(UserNamedReply.IsLine(inner));
    }

    // The one check the readers make covers the `@loop send` lines as well.
    [Theory]
    [InlineData("{sending loop 'King's Road' (3 rooms, 1 line)}")]
    [InlineData("{@loopresult 9da659db saved as 'King's Road 2'}")]
    [InlineData("  {events: Night stats on}  ")]
    [InlineData("events: Night stats on")]
    public void TheSharedCheck_TakesALoopSendLineAndAnEventReply_BracedOrNot(string message) =>
        Assert.True(UserNamedReply.IsMessage(message));
}

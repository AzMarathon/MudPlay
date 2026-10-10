using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Events;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;
using Harness = MudPlay.Tests.EventEngineHarness;

namespace MudPlay.Tests;

// Event runs are handled start to finish: an event that fires while another's run
// is under way waits for it (report paradigm-20261009-185106), and what the last
// of them goes back to is what the first interrupted. Also what a finished event
// says when the way back is refused (report paradigm-20261009-220128).
public sealed class EventQueueTests : IDisposable
{
    private const string TestBbs = "test-event-queue";

    private readonly string _root;

    public EventQueueTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-event-queue-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
        try { Directory.Delete(AppPaths.BbsFolder(TestBbs), recursive: true); }
        catch { /* best-effort */ }
    }

    private Harness NewHarness() => Harness.Create(_root, TestBbs);

    private static readonly RoomKey A = new(1, 1);
    private static readonly RoomKey B = new(1, 2);
    private static readonly RoomKey C = new(1, 3);

    // A saved event, as the scheduler fires it: only an event still in the list
    // leaves the queue.
    private static ScheduledEvent Add(Harness h, ScheduledEvent e)
    {
        h.Events.Events.Add(e);
        return e;
    }

    private static ScheduledEvent WalkTo(string name, RoomKey room, EventThenType then = EventThenType.Resume) => new()
    {
        Name = name,
        TriggerType = EventTriggerType.Every,
        ActionType = EventActionType.WalkTo,
        WalkToTarget = new RoomRef(room.Map, room.Room),
        Then = then,
    };

    // The reports' events: fire at a boss's guaranteed spawn, walk to its room, then
    // start the farming loop again.
    private static ScheduledEvent BossWalk(string name, string boss, RoomKey room) => new()
    {
        Name = name,
        TriggerType = EventTriggerType.Boss,
        BossName = boss,
        BossMoment = EventBossMoment.Guaranteed,
        ActionType = EventActionType.WalkTo,
        WalkToTarget = new RoomRef(room.Map, room.Room),
        Then = EventThenType.Loop,
        ThenLoopName = "Farm",
    };

    private static Loop RunUserLoop(Harness h, string name = "ab")
    {
        h.Tracker.SetLocated(A);
        Loop loop = new(name, new[] { A, B });
        h.Runner.Start(loop, userStarted: true);
        return loop;
    }

    private static void Arrive(Harness h, RoomKey room) =>
        h.Events.OnWalkEvent(new WalkEvent(WalkEventKind.Finished, "arrived", room));

    // ----- The collision (paradigm-20261009-185106) ------------------------

    // The report: a boss event's walk was nine rooms from its boss when a second
    // boss's timer came up. The second event took the walker and went the other
    // way; the first boss was never reached. Now the second waits, the first walk
    // arrives, and the loop both end in is started once, after the second.
    [Fact]
    public void ASecondBossEventMidWalk_WaitsForTheFirstToArrive()
    {
        using Harness h = NewHarness();
        h.Loops.Save(new Loop("Farm", new[] { A, B }));
        Loop study = h.Loops.Loops.Single(l => l.Name == "Farm");
        h.Tracker.SetLocated(A);
        h.Runner.Start(study, userStarted: true);
        ScheduledEvent first = Add(h, BossWalk("First boss", "first boss", C));
        ScheduledEvent second = Add(h, BossWalk("Second boss", "second boss", B));

        h.Events.Fire(first);
        h.Events.Fire(second);

        Assert.Equal(C, h.Walker.Destination);                       // the first walk goes on
        Assert.Contains("'First boss' WalkTo", h.Events.RunSummary);
        Assert.Contains("'Second boss'", h.Events.QueueSummary);
        Assert.Contains(h.EventLog, l => l.Contains("'Second boss' queued behind 'First boss' (WalkTo)"));

        Arrive(h, C);

        Assert.Equal(B, h.Walker.Destination);                       // now the second
        Assert.Contains("'Second boss' WalkTo", h.Events.RunSummary);
        Assert.Contains("resume target loop 'Farm'", h.Events.RunSummary);
        Assert.Equal("(empty)", h.Events.QueueSummary);
        Assert.Equal(LoopState.Idle, h.Runner.State);                 // not started to be stopped again

        Arrive(h, B);

        Assert.Equal("(none)", h.Events.RunSummary);
        Assert.Same(study, h.Runner.CurrentLoop);
        Assert.NotEqual(LoopState.Idle, h.Runner.State);
    }

    // Three in a row run in the order they fired.
    [Fact]
    public void WaitingEvents_RunInTheOrderTheyFired()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        ScheduledEvent one = Add(h, WalkTo("one", C, EventThenType.Nothing));
        ScheduledEvent two = Add(h, WalkTo("two", B, EventThenType.Nothing));
        ScheduledEvent three = Add(h, WalkTo("three", A, EventThenType.Nothing));

        h.Events.Fire(one);
        h.Events.Fire(two);
        h.Events.Fire(three);
        Assert.Contains("'one'", h.Events.RunSummary);

        Arrive(h, C);
        Assert.Contains("'two'", h.Events.RunSummary);
        Assert.Contains("'three'", h.Events.QueueSummary);

        Arrive(h, B);
        Assert.Contains("'three'", h.Events.RunSummary);
    }

    // An "every 5 minutes" event whose run takes 7 runs once, not twice in a row.
    [Fact]
    public void TheSameEventFiringMidRun_IsSkipped()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        ScheduledEvent e = Add(h, WalkTo("bank", C, EventThenType.Nothing));

        h.Events.Fire(e);
        h.Events.Fire(e);

        Assert.Equal("(empty)", h.Events.QueueSummary);
        Assert.Contains(h.EventLog, l => l.Contains("'bank' fired again while its own run is still going"));

        Arrive(h, C);
        Assert.Equal("(none)", h.Events.RunSummary);
    }

    [Fact]
    public void TheSameEventFiringWhileItWaits_StaysQueuedOnce()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        ScheduledEvent running = Add(h, WalkTo("one", C, EventThenType.Nothing));
        ScheduledEvent waiting = Add(h, WalkTo("two", B, EventThenType.Nothing));

        h.Events.Fire(running);
        h.Events.Fire(waiting);
        h.Events.Fire(waiting);

        Assert.Equal(1, h.Events.QueueSummary.Split("'two'").Length - 1);
        Assert.Contains(h.EventLog, l => l.Contains("'two' fired again while it is already waiting"));
    }

    // A walk that can't be finished still runs its Then, as it always has, and the
    // waiting event starts.
    [Fact]
    public void AFailedWalk_EndsTheRun_AndTheNextStarts()
    {
        using Harness h = NewHarness();
        Loop loop = RunUserLoop(h);
        ScheduledEvent first = Add(h, WalkTo("one", C));
        ScheduledEvent second = Add(h, WalkTo("two", B));

        h.Events.Fire(first);
        h.Events.Fire(second);
        h.Events.OnWalkEvent(new WalkEvent(WalkEventKind.Failed, "no path", C));

        Assert.Contains("'two' WalkTo", h.Events.RunSummary);
        Assert.Contains("resume target loop 'ab'", h.Events.RunSummary);
        Assert.Contains(h.EventLog, l => l.Contains("'one': its WalkTo didn't get done"));

        Arrive(h, B);
        Assert.Same(loop, h.Runner.CurrentLoop);
    }

    // The user stopping the walk is a stop: the waiting events don't start.
    [Fact]
    public void AUserStop_AbandonsTheRun_AndEmptiesTheQueue()
    {
        using Harness h = NewHarness();
        RunUserLoop(h);
        ScheduledEvent first = Add(h, WalkTo("one", C));
        ScheduledEvent second = Add(h, WalkTo("two", B));
        h.Events.Fire(first);
        h.Events.Fire(second);

        h.Walker.Stop("user stop from toolbar");

        Assert.Equal("(none)", h.Events.RunSummary);
        Assert.Equal("(empty)", h.Events.QueueSummary);
        Assert.Equal(WalkState.Idle, h.Walker.State);
        Assert.Equal(LoopState.Idle, h.Runner.State);                 // no hand-back either
        Assert.Contains(h.EventLog, l => l.Contains("'one' abandoned at WalkTo — its walk was stopped"));
        Assert.Contains(h.EventLog, l => l.Contains("1 waiting event(s) dropped ('two')"));
    }

    // "Loop 3 times then go to the bank" finishes before anything else fires off
    // (user, 2026-10-10): an event that fires mid-loop waits for the laps and for
    // the Then walk after them.
    [Fact]
    public void ALoopEventWithAStopRule_HoldsTheQueue_UntilItsThenIsDone()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        h.Loops.Save(new Loop("farm", new[] { A, B }));
        ScheduledEvent farm = Add(h, new ScheduledEvent
        {
            Name = "farm", ActionType = EventActionType.Loop, LoopName = "farm", StopAfterLaps = 2,
            Then = EventThenType.WalkTo, ThenWalkTo = new RoomRef(C.Map, C.Room),
        });
        ScheduledEvent walk = Add(h, WalkTo("boss", B, EventThenType.Nothing));

        h.Events.Fire(farm);
        h.Events.Fire(walk);

        Assert.Contains("'farm' Loop", h.Events.RunSummary);
        Assert.Contains("'boss'", h.Events.QueueSummary);
        Assert.NotEqual(LoopState.Idle, h.Runner.State);              // the loop goes on

        h.Events.OnLoopEvent(new LoopEvent(LoopEventKind.RepeatStarted, "farm"));
        Assert.Contains("'boss'", h.Events.QueueSummary);
        h.Events.OnLoopEvent(new LoopEvent(LoopEventKind.RepeatStarted, "farm"));

        Assert.Equal(LoopState.Idle, h.Runner.State);                 // the laps are done
        Assert.Contains("'farm' Then walk-to", h.Events.RunSummary);
        Assert.Contains("'boss'", h.Events.QueueSummary);             // still waiting: the bank walk

        Arrive(h, C);
        Assert.Contains("'boss' WalkTo", h.Events.RunSummary);
        Assert.Equal("(empty)", h.Events.QueueSummary);
    }

    // A loop event with no Stop after rule can never finish, so it is done once
    // the loop has started: the loop is then what the character is doing, and a
    // later event takes over from it and goes back to it.
    [Fact]
    public void ALoopEventWithNoStopRule_IsDoneOnceTheLoopStarts()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        h.Loops.Save(new Loop("farm", new[] { A, B }));
        ScheduledEvent farm = Add(h, new ScheduledEvent
        {
            Name = "farm", ActionType = EventActionType.Loop, LoopName = "farm", Then = EventThenType.Nothing,
        });
        ScheduledEvent walk = Add(h, WalkTo("boss", C));

        h.Events.Fire(farm);

        Assert.Equal("(none)", h.Events.RunSummary);
        Assert.Equal("farm", h.Runner.CurrentLoop?.Name);
        Assert.Contains(h.EventLog, l => l.Contains("'farm' finished; started loop 'farm'") && l.Contains("no Stop after rule"));

        h.Events.Fire(walk);

        Assert.Contains("'boss' WalkTo", h.Events.RunSummary);
        Assert.Contains("resume target loop 'farm'", h.Events.RunSummary);
        Assert.Equal("(empty)", h.Events.QueueSummary);
        Assert.Equal(LoopState.Idle, h.Runner.State);

        Arrive(h, C);
        Assert.Equal("farm", h.Runner.CurrentLoop?.Name);
    }

    // The same event waiting behind another: it starts in its turn, is done at
    // once, and the event behind it takes its loop as what to go back to.
    [Fact]
    public void AnEventQueuedBehindALoopEvent_StartsOnceTheLoopHas()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        h.Loops.Save(new Loop("farm", new[] { A, B }));
        ScheduledEvent first = Add(h, WalkTo("one", C, EventThenType.Nothing));
        ScheduledEvent farm = Add(h, new ScheduledEvent
        {
            Name = "farm", ActionType = EventActionType.Loop, LoopName = "farm", Then = EventThenType.Nothing,
        });
        ScheduledEvent last = Add(h, WalkTo("last", B, EventThenType.Nothing));

        h.Events.Fire(first);
        h.Events.Fire(farm);
        h.Events.Fire(last);
        Arrive(h, C);

        Assert.Contains("'last' WalkTo", h.Events.RunSummary);
        Assert.Contains("resume target loop 'farm'", h.Events.RunSummary);
        Assert.Equal("(empty)", h.Events.QueueSummary);
    }

    // A Then walk-to is the event's last leg: the next event waits for it too.
    [Fact]
    public void AThenWalkTo_HoldsTheQueueUntilItArrives()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        ScheduledEvent first = Add(h, WalkTo("one", C, EventThenType.WalkTo));
        first.ThenWalkTo = new RoomRef(B.Map, B.Room);
        ScheduledEvent second = Add(h, WalkTo("two", A, EventThenType.Nothing));

        h.Events.Fire(first);
        Arrive(h, C);
        Assert.Contains("'one' Then walk-to", h.Events.RunSummary);

        h.Events.Fire(second);
        Assert.Contains("'two'", h.Events.QueueSummary);
        Assert.Equal(B, h.Walker.Destination);

        Arrive(h, B);
        Assert.Contains("'two' WalkTo", h.Events.RunSummary);
    }

    // A plain command isn't a run: it is sent at once and nothing waits or moves.
    [Fact]
    public void ACommandEventMidRun_IsSentAtOnce()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        List<string> sent = new();
        h.Events.SetWireSender(b => sent.Add(System.Text.Encoding.Latin1.GetString(b).TrimEnd('\r')));
        ScheduledEvent walk = Add(h, WalkTo("one", C, EventThenType.Nothing));

        h.Events.Fire(walk);
        h.Events.Fire(new ScheduledEvent { Name = "stat", ActionType = EventActionType.Command, CommandText = "stat" });

        Assert.Equal("stat", Assert.Single(sent));
        Assert.Equal("(empty)", h.Events.QueueSummary);
        Assert.Contains("'one' WalkTo", h.Events.RunSummary);
    }

    // ----- Bounds ------------------------------------------------------------

    [Fact]
    public void TheQueue_HoldsNoMoreThanItsCap()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        h.Events.Fire(Add(h, WalkTo("running", C, EventThenType.Nothing)));

        Assert.Equal(EventManager.DefaultMaxQueued, h.Events.MaxQueued);
        for (int i = 0; i < EventManager.DefaultMaxQueued + 1; i++)
            h.Events.Fire(Add(h, WalkTo($"w{i}", B, EventThenType.Nothing)));

        Assert.Contains($"'w{EventManager.DefaultMaxQueued - 1}'", h.Events.QueueSummary);
        Assert.DoesNotContain($"'w{EventManager.DefaultMaxQueued}'", h.Events.QueueSummary);
        Assert.Contains(h.EventLog, l => l.Contains(
            $"'w{EventManager.DefaultMaxQueued}' dropped — {EventManager.DefaultMaxQueued} events are already waiting"));
        Assert.Contains($"[Event 'w{EventManager.DefaultMaxQueued}' dropped:", Assert.Single(h.Notices));
    }

    // Both limits are the character's own settings (Settings → Events).
    [Fact]
    public void TheQueueLimits_ComeFromTheCharactersSettings()
    {
        using Harness h = NewHarness();
        DateTimeOffset now = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);
        h.Events.Now = () => now;
        h.Profile.Current!.EventQueueLimit = 1;
        h.Profile.Current!.EventQueueWaitMinutes = 5;
        h.Tracker.SetLocated(A);
        h.Events.Fire(Add(h, WalkTo("running", C, EventThenType.Nothing)));

        h.Events.Fire(Add(h, WalkTo("kept", B, EventThenType.Nothing)));
        h.Events.Fire(Add(h, WalkTo("one too many", A, EventThenType.Nothing)));

        Assert.Contains("'kept'", h.Events.QueueSummary);
        Assert.DoesNotContain("'one too many'", h.Events.QueueSummary);

        now += TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1);
        h.Events.Tick();

        Assert.Equal("(empty)", h.Events.QueueSummary);
        Assert.Contains(h.EventLog, l => l.Contains("'kept' dropped — it waited 5 minutes behind 'running' (WalkTo)"));
        Assert.Equal(2, h.Notices.Count);
    }

    // A run that doesn't end (a walk left paused) must not set off hours-old events
    // when it finally does.
    [Fact]
    public void AnEventThatWaitedTooLong_IsDropped()
    {
        using Harness h = NewHarness();
        DateTimeOffset now = new(2026, 10, 9, 18, 0, 0, TimeSpan.Zero);
        h.Events.Now = () => now;
        h.Tracker.SetLocated(A);
        h.Events.Fire(Add(h, WalkTo("stuck", C, EventThenType.Nothing)));
        h.Events.Fire(Add(h, WalkTo("stale", B, EventThenType.Nothing)));

        now += h.Events.MaxQueueWait - TimeSpan.FromSeconds(1);
        h.Events.Tick();
        Assert.Contains("'stale'", h.Events.QueueSummary);

        h.Events.Fire(Add(h, WalkTo("fresh", A, EventThenType.Nothing)));
        now += TimeSpan.FromSeconds(2);
        h.Events.Tick();

        Assert.DoesNotContain("'stale'", h.Events.QueueSummary);
        Assert.Contains("'fresh'", h.Events.QueueSummary);
        Assert.Contains(h.EventLog, l => l.Contains("'stale' dropped — it waited 30 minutes behind 'stuck' (WalkTo)"));

        Arrive(h, C);
        Assert.Contains("'fresh' WalkTo", h.Events.RunSummary);
    }

    // Removed (or edited: an edit replaces the instance) while it waited.
    [Fact]
    public void AnEventRemovedWhileItWaited_DoesNotRun()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        ScheduledEvent second = Add(h, WalkTo("two", B, EventThenType.Nothing));
        h.Events.Fire(Add(h, WalkTo("one", C, EventThenType.Nothing)));
        h.Events.Fire(second);

        h.Events.Remove(second);
        Arrive(h, C);

        Assert.Equal("(none)", h.Events.RunSummary);
    }

    // ----- What ends a run early -----------------------------------------------

    // Logoff events run in the minutes before a cleanup: one starts at once. The
    // run it interrupts is abandoned; what was waiting keeps its place behind it.
    [Fact]
    public void ALogoffEvent_JumpsTheQueue()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        ScheduledEvent logoff = Add(h, WalkTo("to the inn", B, EventThenType.Nothing));
        logoff.TriggerType = EventTriggerType.Logoff;
        h.Events.Fire(Add(h, WalkTo("one", C, EventThenType.Nothing)));
        h.Events.Fire(Add(h, WalkTo("two", A, EventThenType.Nothing)));

        h.Events.Fire(logoff);

        Assert.Contains("'to the inn' WalkTo", h.Events.RunSummary);
        Assert.Equal(B, h.Walker.Destination);
        Assert.Contains("'two'", h.Events.QueueSummary);
        Assert.Contains(h.EventLog, l => l.Contains("'to the inn' jumps the queue: 'one' is abandoned at WalkTo"));

        Arrive(h, B);
        Assert.Contains("'two' WalkTo", h.Events.RunSummary);
    }

    // A second logoff-type event doesn't interrupt the first: it waits, ahead of the
    // ordinary events. A boss event is an ordinary event.
    [Fact]
    public void ASecondLogoffEvent_WaitsAtTheHeadOfTheQueue()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        ScheduledEvent first = Add(h, WalkTo("bank", C, EventThenType.Nothing));
        first.TriggerType = EventTriggerType.Logoff;
        ScheduledEvent boss = Add(h, BossWalk("Boss", "boss", B));
        ScheduledEvent second = Add(h, WalkTo("to the inn", B, EventThenType.Nothing));
        second.TriggerType = EventTriggerType.Logoff;

        h.Events.Fire(first);
        h.Events.Fire(boss);
        h.Events.Fire(second);

        Assert.Contains("'bank' WalkTo", h.Events.RunSummary);
        Assert.StartsWith("'to the inn'", h.Events.QueueSummary);
        Assert.Contains("'Boss'", h.Events.QueueSummary);

        Arrive(h, C);
        Assert.Contains("'to the inn' WalkTo", h.Events.RunSummary);
    }

    // What counts as logoff-type besides the Logoff trigger: a command that sends a
    // log-off as a line of its own.
    [Theory]
    [InlineData(";o", true)]
    [InlineData("=x", true)]
    [InlineData("bank;=x", true)]
    [InlineData("quit now", true)]          // the realm's own exit command
    [InlineData("stat", false)]
    [InlineData("say =x", false)]
    public void ACommandThatLogsOff_IsLogoffType(string command, bool expected)
    {
        using Harness h = NewHarness();
        h.Events.SetExitCommandReader(() => "quit now");

        Assert.Equal(expected, h.Events.IsLogoffType(new ScheduledEvent
        {
            TriggerType = EventTriggerType.Every, ActionType = EventActionType.Command, CommandText = command,
            Then = EventThenType.Resume,
        }));
    }

    [Fact]
    public void OnlyTheLogoffTriggerAndLogOffCommands_AreLogoffType()
    {
        using Harness h = NewHarness();

        Assert.True(h.Events.IsLogoffType(new ScheduledEvent
        {
            TriggerType = EventTriggerType.Logoff, ActionType = EventActionType.BankTrip,
        }));
        Assert.False(h.Events.IsLogoffType(BossWalk("Boss", "boss", B)));
        Assert.False(h.Events.IsLogoffType(new ScheduledEvent
        {
            TriggerType = EventTriggerType.AtTime, ActionType = EventActionType.BankTrip,
        }));
    }

    // A relog command with something after it is a run, and it doesn't wait.
    [Fact]
    public void ALogOffCommandEvent_JumpsTheQueue()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        List<string> sent = new();
        h.Events.SetWireSender(b => sent.Add(System.Text.Encoding.Latin1.GetString(b).TrimEnd('\r')));
        h.Events.Fire(Add(h, WalkTo("one", C, EventThenType.Nothing)));
        h.Events.Fire(Add(h, WalkTo("two", B, EventThenType.Nothing)));

        h.Events.Fire(Add(h, new ScheduledEvent
        {
            Name = "relog", TriggerType = EventTriggerType.Every, ActionType = EventActionType.Command,
            CommandText = ";o", Then = EventThenType.Resume,
        }));

        Assert.Equal(";o", Assert.Single(sent));
        Assert.Contains(h.EventLog, l => l.Contains("'relog' jumps the queue"));
    }

    // A drop empties the queue (Logon and Re-log fire afresh on the way back in) and
    // leaves the run to the engines, no longer holding later events back.
    [Fact]
    public void ADisconnect_EmptiesTheQueue_AndTheRunStopsHoldingIt()
    {
        using Harness h = NewHarness();
        Loop loop = RunUserLoop(h);
        h.Events.Fire(Add(h, WalkTo("one", C)));
        h.Events.Fire(Add(h, WalkTo("two", B)));

        h.Events.NoteDisconnected();

        Assert.Equal("(empty)", h.Events.QueueSummary);
        Assert.Contains("'one' WalkTo", h.Events.RunSummary);
        Assert.Contains("not holding the queue", h.Events.RunSummary);

        // After the reconnect a Logon event takes over, as it did before events
        // queued, and still goes back to what the first interrupted.
        ScheduledEvent logon = Add(h, WalkTo("logon", B));
        h.Events.Fire(logon);
        Assert.Contains("'logon' WalkTo", h.Events.RunSummary);
        Arrive(h, B);
        Assert.Same(loop, h.Runner.CurrentLoop);
    }

    // The engines halt in the graveyard; a wait their stop doesn't reach, and the
    // event behind it, must not carry on from there.
    [Fact]
    public void ADeath_AbandonsTheRun_AndEmptiesTheQueue()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        h.Events.Fire(Add(h, new ScheduledEvent
        {
            Name = "hold", ActionType = EventActionType.Wait, WaitSeconds = 600, Then = EventThenType.Resume,
        }));
        h.Events.Fire(Add(h, WalkTo("two", C)));

        h.Events.NoteDeath();

        Assert.Equal("(none)", h.Events.RunSummary);
        Assert.Equal("(empty)", h.Events.QueueSummary);
        Assert.Equal(WalkState.Idle, h.Walker.State);
    }

    // ----- Going back ----------------------------------------------------------

    // The user's own walk or loop: an event that fires over it takes over at once
    // and goes back to it. Nothing here changed.
    [Fact]
    public void AnEventOverTheUsersOwnWalk_TakesOverAtOnce_AndGoesBack()
    {
        using Harness h = NewHarness();
        h.Tracker.SetLocated(A);
        h.Walker.WalkTo(C, preferTeleportFree: true);
        ScheduledEvent e = Add(h, WalkTo("errand", B));

        h.Events.Fire(e);

        Assert.Equal(B, h.Walker.Destination);
        Assert.Equal("(empty)", h.Events.QueueSummary);
        Assert.Contains("resume target walk to 1/3", h.Events.RunSummary);

        Arrive(h, B);
        Assert.Equal(C, h.Walker.Destination);
        Assert.Equal(WalkState.Walking, h.Walker.State);
    }

    // Two "go back" events in a row go back once, after the second, to what the
    // first one interrupted.
    [Fact]
    public void TwoGoBackEvents_GoBackOnce_AfterTheLast()
    {
        using Harness h = NewHarness();
        Loop loop = RunUserLoop(h);
        int starts = 0;
        h.Runner.Event += l => { if (l.Kind == LoopEventKind.Started) starts++; };
        h.Events.Fire(Add(h, WalkTo("one", C)));
        h.Events.Fire(Add(h, WalkTo("two", B)));

        Arrive(h, C);
        Assert.Equal(0, starts);
        Arrive(h, B);

        Assert.Equal(1, starts);
        Assert.Same(loop, h.Runner.CurrentLoop);
        Assert.Contains(h.EventLog, l => l.Contains("'two' finished; going back to loop 'ab'"));
        Assert.Contains("'two' → loop 'ab'", h.Events.LastThenSummary);
    }

    // An event that ends in a loop of its own doesn't go back to the one it
    // interrupted.
    [Fact]
    public void AnEventEndingInItsOwnLoop_DoesNotGoBackToTheInterruptedOne()
    {
        using Harness h = NewHarness();
        RunUserLoop(h);
        h.Loops.Save(new Loop("other", new[] { B, C }));
        ScheduledEvent e = Add(h, WalkTo("move camp", C, EventThenType.Loop));
        e.ThenLoopName = "other";

        h.Events.Fire(e);
        h.Tracker.SetLocated(C);
        Arrive(h, C);

        Assert.Equal("other", h.Runner.CurrentLoop?.Name);
    }

    // The user's pause is the shared user gate, which no engine stop lifts: the
    // event's walk holds on it, and so does the loop it goes back to.
    [Fact]
    public void AUserPause_HoldsTheEventsWalk_AndTheLoopItGoesBackTo()
    {
        using Harness h = NewHarness();
        Loop loop = RunUserLoop(h);
        h.Coordinator.AssertGate(MovementCoordinator.UserGate, "test");
        h.Sent.Clear();

        h.Events.Fire(Add(h, WalkTo("errand", C)));
        Assert.Empty(h.Sent);                                         // nothing moves
        Assert.Contains(MovementCoordinator.UserGate, h.Coordinator.AssertedGates);

        Arrive(h, C);

        Assert.Same(loop, h.Runner.CurrentLoop);
        Assert.Empty(h.Sent);
        Assert.Contains(MovementCoordinator.UserGate, h.Coordinator.AssertedGates);
    }

    // The master switch off freezes every engine on its own gate. An event's walk
    // caught by it is held, not abandoned: the run stays the running one, sends
    // nothing, the event behind it waits (and is dropped at the wait limit like any
    // other), and the walk goes on when the switch is back.
    [Fact]
    public void AutoAllOff_HoldsTheRunAndTheQueue_UntilItIsBackOn()
    {
        using Harness h = NewHarness();
        DateTimeOffset now = new(2026, 10, 9, 18, 0, 0, TimeSpan.Zero);
        h.Events.Now = () => now;
        h.Tracker.SetLocated(A);
        h.Coordinator.AssertGate(MovementCoordinator.AutoAllGate, "test");

        h.Events.Fire(Add(h, WalkTo("one", C, EventThenType.Nothing)));
        h.Events.Fire(Add(h, WalkTo("two", B, EventThenType.Nothing)));
        for (int i = 0; i < 5; i++)
        {
            now += TimeSpan.FromSeconds(1);
            h.Events.Tick();
        }

        Assert.Empty(h.Sent);
        Assert.Contains("'one' WalkTo", h.Events.RunSummary);
        Assert.Contains("'two'", h.Events.QueueSummary);

        h.Coordinator.ClearGate(MovementCoordinator.AutoAllGate, "test");

        Assert.Equal("n", Assert.Single(h.Sent));
        Assert.Contains("'one' WalkTo", h.Events.RunSummary);
        Assert.Contains("'two'", h.Events.QueueSummary);
    }

    // ----- The way back refused (paradigm-20261009-220128) ---------------------

    // The report's shape: the loop's rooms can be left on foot but only entered by a
    // room command, which is a teleport to the route search.
    //
    //   7/131 Study --N-- 7/132 Dusty Stair --E (one way)--> 1/21 City Entrance
    //   1/21 --W-- 1/10 Library --SW (CMD 100 teleport)--> 7/131
    private const string StudyGraphJson = """
        [
          { "Map Number": 7, "Room Number": 131, "Name": "Study",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "7/132", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 7, "Room Number": 132, "Name": "Dusty Stair",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "7/131", "E": "1/21", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 21, "Name": "City Entrance",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0,
            "N": "0", "S": "0", "E": "0", "W": "1/10",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 10, "Name": "Library",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 0, "CMD": 100,
            "N": "0", "S": "0", "E": "1/21", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "7/131 (Item: 474)",
            "U": "0", "D": "0" }
        ]
        """;

    private const string StudyTbinfoJson = """[ { "Number": 100, "Action": "pull book:teleport 131 7\n" } ]""";

    private static readonly RoomKey Study = new(7, 131);
    private static readonly RoomKey Stair = new(7, 132);
    private static readonly RoomKey City = new(1, 21);
    private static readonly RoomKey Library = new(1, 10);

    // The user starts the loop in the study; the boss event walks out to the city.
    private Harness StudyHarness(params (RoomKey From, RoomKey To)[] allowedTeleports)
    {
        Harness h = Harness.Create(_root, TestBbs, StudyGraphJson, StudyTbinfoJson, loopWalksItself: true);
        h.Walker.SetTeleportResolver((_, _) => "pull book");
        HashSet<(RoomKey From, RoomKey To)> allowed = new(allowedTeleports);
        h.Walker.SetAutomaticWalkTeleports(() => allowed);
        h.Loops.Save(new Loop("Farm", new[] { Study, Stair }));
        h.Tracker.SetLocated(Study);
        h.Runner.Start(h.Loops.Loops.Single(l => l.Name == "Farm"), userStarted: true);
        h.Events.Fire(Add(h, BossWalk("Second boss", "second boss", City)));
        h.Tracker.SetLocated(City);
        return h;
    }

    // What the report's client did, and all it said was in the program log: the walk
    // back to the loop is the client's own, the teleport on the only way back isn't
    // one Settings → Teleports allows such walks, so the loop ended where the event
    // did. That rule stands; the terminal now says so.
    [Fact]
    public void TheLoopBehindATeleportAutomaticWalksMayNotUse_SaysWhyItDidNotGoBack()
    {
        using Harness h = StudyHarness();

        Arrive(h, City);

        Assert.Equal(LoopState.Idle, h.Runner.State);
        string notice = Assert.Single(h.Notices);
        Assert.Contains("Event 'Second boss' finished, but loop 'Farm' didn't get going", notice);
        Assert.Contains("this is an automatic walk", notice);
        Assert.Contains("Tick \"Library (1/10) → Study (7/131)\" on Settings → Teleports", notice);
        Assert.Contains("failed", h.Events.LastThenSummary);
        Assert.Contains(h.EventLog, l => l.Contains("didn't get going"));
    }

    // The same refusal arriving a moment after the loop started (the walker waited on
    // something before planning) is said too.
    [Fact]
    public void ARefusalAfterTheLoopStarted_IsSaidToo()
    {
        using Harness h = StudyHarness((Library, Study));
        Arrive(h, City);
        Assert.Empty(h.Notices);

        h.Events.OnLoopEvent(new LoopEvent(LoopEventKind.Failed, "approach failed: no route"));

        Assert.Contains("loop 'Farm' didn't get going: approach failed: no route", Assert.Single(h.Notices));
    }

    // With the teleport ticked, the loop walks back.
    [Fact]
    public void WithTheTeleportAllowed_TheLoopWalksBack()
    {
        using Harness h = StudyHarness((Library, Study));

        Arrive(h, City);

        Assert.Empty(h.Notices);
        Assert.Equal(LoopState.Approaching, h.Runner.State);
        Assert.Equal("Farm", h.Runner.CurrentLoop?.Name);
        Assert.Contains(h.EventLog, l => l.Contains("'Second boss' finished; starting loop 'Farm'"));
    }
}

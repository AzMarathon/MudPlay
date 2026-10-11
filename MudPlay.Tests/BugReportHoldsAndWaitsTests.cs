using System.Text;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// "Holds and waits": time in state from the once-a-second watch, the movement
// gates with who raised them and since when, and the send gate.
public sealed class BugReportHoldsAndWaitsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Watch_StampsAChangeWhenItIsSeenAndRemembersTheLastFew()
    {
        string state = "Idle";
        EngineStateWatch watch = new();
        watch.Add("Walker", () => state, keep: 2);

        watch.Sample(T0);
        state = "Walking";
        watch.Sample(T0.AddSeconds(5));
        state = "Pending";
        watch.Sample(T0.AddSeconds(9));
        state = "Walking";
        watch.Sample(T0.AddSeconds(10));

        EngineStateWatch.Reading reading = Assert.Single(watch.Snapshot(T0.AddSeconds(70)));
        Assert.Equal("Walking", reading.Value);
        Assert.Equal(T0.AddSeconds(10), reading.Since);
        Assert.False(reading.SinceWatchStart);
        // Only the last two changes are kept.
        Assert.Equal(new[] { "Walking", "Pending" }, reading.Recent.Select(c => c.From));
    }

    [Fact]
    public void Watch_SnapshotSamplesFirst_AndAnUnchangedStateIsAtLeastAsOldAsTheWatch()
    {
        string state = "Idle";
        EngineStateWatch watch = new();
        watch.Add("Door handler", () => state);
        watch.Add("Broken", () => throw new InvalidOperationException("no"));
        watch.Sample(T0);
        state = "Opening";

        IReadOnlyList<EngineStateWatch.Reading> readings = watch.Snapshot(T0.AddSeconds(3));

        Assert.Equal("Opening", readings[0].Value);
        Assert.Equal(T0.AddSeconds(3), readings[0].Since);
        Assert.Equal("(unreadable: InvalidOperationException)", readings[1].Value);
        Assert.True(readings[1].SinceWatchStart);
    }

    // A snapshot shows a change the watch hasn't sampled yet, but remembers
    // nothing: only Sample writes, so a snapshot taken off the UI thread (a crash
    // report) changes nothing the UI thread's sampling is working on.
    [Fact]
    public void Watch_SnapshotWritesNothing_TheNextSampleStampsTheChange()
    {
        string state = "Idle";
        EngineStateWatch watch = new();
        watch.Add("Door handler", () => state, keep: 2);
        watch.Sample(T0);
        state = "Opening";

        EngineStateWatch.Reading first = Assert.Single(watch.Snapshot(T0.AddSeconds(3)));
        EngineStateWatch.Reading second = Assert.Single(watch.Snapshot(T0.AddSeconds(4)));

        // Had the first snapshot recorded the change, the second would date it
        // from then.
        Assert.Equal(T0.AddSeconds(3), first.Since);
        Assert.Equal(T0.AddSeconds(4), second.Since);
        Assert.Equal(new EngineStateWatch.Change(T0.AddSeconds(4), "Idle", "Opening"), Assert.Single(second.Recent));

        watch.Sample(T0.AddSeconds(5));
        EngineStateWatch.Reading afterSample = Assert.Single(watch.Snapshot(T0.AddSeconds(60)));
        Assert.Equal(T0.AddSeconds(5), afterSample.Since);
        Assert.Equal(new EngineStateWatch.Change(T0.AddSeconds(5), "Idle", "Opening"), Assert.Single(afterSample.Recent));
    }

    [Fact]
    public void Watch_AChangeSeenOnlyByTheSnapshot_StillKeepsNoMoreThanTheProbeAsksFor()
    {
        string state = "A";
        EngineStateWatch watch = new();
        watch.Add("Walker", () => state, keep: 2);
        watch.Sample(T0);
        state = "B";
        watch.Sample(T0.AddSeconds(1));
        state = "C";
        watch.Sample(T0.AddSeconds(2));
        state = "D";

        EngineStateWatch.Reading reading = Assert.Single(watch.Snapshot(T0.AddSeconds(3)));

        Assert.Equal("D", reading.Value);
        Assert.Equal(new[] { "B", "C" }, reading.Recent.Select(c => c.From));
    }

    // Snapshots from other threads while the watch samples: each reading is whole
    // (its value is where its last change went), and nothing throws.
    [Fact]
    public async Task Watch_SnapshotsFromOtherThreadsWhileSampling_AreEachConsistent()
    {
        int tick = 0;
        EngineStateWatch watch = new();
        for (int p = 0; p < 8; p++) watch.Add($"Engine {p}", () => "s" + (Volatile.Read(ref tick) % 5), keep: 4);
        using CancellationTokenSource stop = new();

        Task[] readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (EngineStateWatch.Reading reading in watch.Snapshot(DateTimeOffset.UtcNow))
                {
                    Assert.InRange(reading.Recent.Count, 0, 4);
                    if (reading.Recent.Count > 0) Assert.Equal(reading.Recent[^1].To, reading.Value);
                    for (int i = 1; i < reading.Recent.Count; i++)
                        Assert.Equal(reading.Recent[i - 1].To, reading.Recent[i].From);
                }
            }
        })).ToArray();

        for (int i = 0; i < 20_000; i++)
        {
            Interlocked.Increment(ref tick);
            watch.Sample(DateTimeOffset.UtcNow);
        }
        stop.Cancel();
        await Task.WhenAll(readers);
    }

    [Fact]
    public void States_PrintHowLongAndWhatCameBefore()
    {
        StringBuilder sb = new();
        BugReportHoldsAndWaits.States(sb,
        [
            new("Walker", "Pending", T0.AddSeconds(-240), false,
                [new(T0.AddSeconds(-300), "Idle", "Walking"), new(T0.AddSeconds(-240), "Walking", "Pending")]),
            new("Winch handler", "Idle", T0.AddHours(-3), true, []),
        ], T0);

        string text = sb.ToString();
        Assert.Contains("- **Walker**: Pending for 4 min (since 11:56:00); before that: Walking until 11:56:00 ← Idle until 11:55:00", text);
        Assert.Contains("- **Winch handler**: Idle for ≥ 3 h (since 09:00:00)", text);
    }

    [Fact]
    public void MovementGates_ShowAsserterReasonAndSince_AndTheLastTransitions()
    {
        MovementCoordinator coordinator = new();
        coordinator.AssertGate(MovementCoordinator.CombatGate, "CombatTracker", "hostile in the room");
        coordinator.AssertGate(MovementCoordinator.SearchGate, "AutoSearch", "searching");
        coordinator.ClearGate(MovementCoordinator.SearchGate, "AutoSearch", "done");

        StringBuilder sb = new();
        BugReportHoldsAndWaits.MovementGates(sb, coordinator, DateTimeOffset.Now.AddSeconds(30));

        string text = sb.ToString();
        Assert.Matches(@"- \*\*Combat\*\*: since \d\d:\d\d:\d\d \(3\d s\), by CombatTracker: hostile in the room", text);
        Assert.DoesNotContain("- **Search**", text);
        Assert.Contains("Last 3 gate transitions (oldest first):", text);
        Assert.Matches(@"cleared   Search  by AutoSearch  — done\n```", text);
    }

    [Fact]
    public void SendGate_ShowsItsHoldsAndWhatItDropped()
    {
        EngineSendGate gate = new();
        Action<byte[]> sender = gate.Named(gate.WrapEngineSender(_ => { }), "PartyPoller");
        sender(Encoding.Latin1.GetBytes("par\r"));
        gate.Hold("MortallyWounded");
        sender(Encoding.Latin1.GetBytes("par\r"));

        StringBuilder sb = new();
        BugReportHoldsAndWaits.SendGate(sb, gate);

        string text = sb.ToString();
        Assert.Contains("- **State**: HELD by MortallyWounded", text);
        Assert.Contains("- **Last client command sent**: `par`", text);
        Assert.Matches(@"dropped by a hold this session\*\*: 1; the last at \d\d:\d\d:\d\d\.\d\d\d: `par` from PartyPoller", text);
    }
}

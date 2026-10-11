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

using System.Collections.Generic;
using MudPlay.Game.Cash;
using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// The stash tally is persisted belief that funding spends real walking on, so its
// arithmetic is pinned here — particularly the clamps, which are what stop a
// drifted tally from going negative or a robbed room from being planned against
// forever.
public sealed class StashLedgerTests
{
    private static readonly RoomKey Room = new(1, 100);
    private static readonly RoomKey Other = new(1, 200);

    [Fact]
    public void Hidden_AccumulatesPerRoom()
    {
        StashLedger led = new();
        led.NoteHidden(Room, 400);
        led.NoteHidden(Room, 600);
        led.NoteHidden(Other, 50);

        Assert.Equal(1000, led.Believed(Room));
        Assert.Equal(50, led.Believed(Other));
    }

    [Fact]
    public void UnknownRoomBelievesNothing()
        => Assert.Equal(0, new StashLedger().Believed(Room));

    [Fact]
    public void Recovered_DrawsDownAndClampsAtZero()
    {
        StashLedger led = new();
        led.NoteHidden(Room, 1000);
        led.NoteRecovered(Room, 400);
        Assert.Equal(600, led.Believed(Room));

        // Over-recovery — someone else's coin was on the floor too, or our tally
        // had drifted. The room is empty afterwards, not negative.
        led.NoteRecovered(Room, 5000);
        Assert.Equal(0, led.Believed(Room));
    }

    [Fact]
    public void Reconcile_WritesOffARobbedStash()
    {
        // The one path that moves a balance down without us taking anything. Without
        // it a looted room stays on the funding plan every single run.
        StashLedger led = new();
        led.NoteHidden(Room, 5000);
        led.Reconcile(Room, 0);

        Assert.Equal(0, led.Believed(Room));
        Assert.Empty(led.NonEmpty());
    }

    [Fact]
    public void Reconcile_CanAlsoRaiseABelief()
    {
        StashLedger led = new();
        led.NoteHidden(Room, 100);
        led.Reconcile(Room, 900);
        Assert.Equal(900, led.Believed(Room));
    }

    [Fact]
    public void NonEmpty_OmitsDrainedRooms()
    {
        StashLedger led = new();
        led.NoteHidden(Room, 100);
        led.NoteHidden(Other, 100);
        led.NoteRecovered(Other, 100);

        IReadOnlyList<(RoomKey Room, long Copper)> live = led.NonEmpty();
        Assert.Single(live);
        Assert.Equal(Room, live[0].Room);
    }

    [Fact]
    public void SnapshotAndHydrate_RoundTrip()
    {
        StashLedger led = new();
        led.NoteHidden(Room, 750);
        led.NoteHidden(Other, 25);

        StashLedger restored = new();
        restored.Hydrate(led.Snapshot());

        Assert.Equal(750, restored.Believed(Room));
        Assert.Equal(25, restored.Believed(Other));
    }

    [Fact]
    public void Hydrate_ReplacesRatherThanMerges()
    {
        StashLedger led = new();
        led.NoteHidden(Room, 750);
        led.Hydrate(new Dictionary<string, long> { ["1/200"] = 10 });

        Assert.Equal(0, led.Believed(Room));
        Assert.Equal(10, led.Believed(Other));
    }

    [Fact]
    public void Hydrate_DropsNonPositiveRows()
    {
        StashLedger led = new();
        led.Hydrate(new Dictionary<string, long> { ["1/100"] = 0, ["1/200"] = -5 });
        Assert.Empty(led.NonEmpty());
    }

    [Fact]
    public void NonPositiveMovementsAreIgnored()
    {
        StashLedger led = new();
        led.NoteHidden(Room, 0);
        led.NoteHidden(Room, -100);
        Assert.Empty(led.NonEmpty());
    }
}

using System;
using System.Collections.Generic;
using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// Pins ShortcutSourceCoordinator — the FSM behind the route picker's "take the
// shortcut" pick when the shortcut item isn't held: walk to the item's source, let
// the room settle, grab the ground drop, then one live-filter walk to the
// destination (which self-selects shortcut vs long). All collaborators are
// delegates, so the flow is driven deterministically here.
public sealed class ShortcutSourceCoordinatorTests
{
    private sealed class Harness
    {
        public readonly HashSet<int> Carried = new();
        public bool Hostiles;
        public bool InCombat;
        public RoomKey? Source = new RoomKey(2, 50);   // default: item has a source
        public readonly List<RoomKey> WalkedToSource = new();
        public readonly List<RoomKey> LiveWalkedToDest = new();
        public readonly List<string> Gets = new();
        public readonly List<Action> Scheduled = new();

        public ShortcutSourceCoordinator Build() => new(
            resolveSource: _ => Source,
            isCarried: Carried.Contains,
            itemName: id => $"item{id}",
            hasHostiles: () => Hostiles,
            inCombat: () => InCombat,
            walkToSource: r => WalkedToSource.Add(r),
            liveWalkToDest: r => LiveWalkedToDest.Add(r),
            sendGet: Gets.Add,
            schedule: (_, a) => Scheduled.Add(a));   // capture; fire manually

        public void FireScheduled() { foreach (Action a in Scheduled.ToArray()) a(); }
    }

    private static WalkEvent Finished(RoomKey dest) => new(WalkEventKind.Finished, "", dest);
    private static WalkEvent Failed() => new(WalkEventKind.Failed, "blocked", null);
    private static WalkEvent Started() => new(WalkEventKind.Started, "", null);

    [Fact]
    public void TryBegin_WithSource_WalksToSource()
    {
        var h = new Harness();
        ShortcutSourceCoordinator c = h.Build();

        Assert.True(c.TryBegin(815, new RoomKey(8, 1699)));
        Assert.True(c.Active);
        Assert.Equal(new[] { new RoomKey(2, 50) }, h.WalkedToSource);
    }

    [Fact]
    public void TryBegin_NoSource_ReturnsFalse_AndDoesNothing()
    {
        var h = new Harness { Source = null };
        ShortcutSourceCoordinator c = h.Build();

        Assert.False(c.TryBegin(815, new RoomKey(8, 1699)));
        Assert.False(c.Active);
        Assert.Empty(h.WalkedToSource);
    }

    [Fact]
    public void CanAttempt_TracksSourceResolution()
    {
        var h = new Harness();
        ShortcutSourceCoordinator c = h.Build();
        Assert.True(c.CanAttempt(815));
        h.Source = null;
        Assert.False(c.CanAttempt(815));
    }

    [Fact]
    public void ArriveClear_ObtainedDrop_ResumesTowardDestination()
    {
        var h = new Harness();   // no hostiles on arrival
        ShortcutSourceCoordinator c = h.Build();
        c.TryBegin(815, new RoomKey(8, 1699));

        c.OnWalkEvent(Finished(new RoomKey(2, 50)));   // arrived at source

        // Room clear → grabs the drop, schedules the resume.
        Assert.Equal(new[] { "item815" }, h.Gets);
        h.Carried.Add(815);                            // the get landed it
        h.FireScheduled();

        Assert.Equal(new[] { new RoomKey(8, 1699) }, h.LiveWalkedToDest);
        Assert.False(c.Active);
    }

    [Fact]
    public void ArriveClear_NoDrop_StillResumes_LongRoute()
    {
        var h = new Harness();
        ShortcutSourceCoordinator c = h.Build();
        c.TryBegin(815, new RoomKey(8, 1699));

        c.OnWalkEvent(Finished(new RoomKey(2, 50)));
        h.FireScheduled();                             // item never carried

        // Still resumes to the destination — the live walk takes the long route.
        Assert.Equal(new[] { new RoomKey(8, 1699) }, h.LiveWalkedToDest);
    }

    [Fact]
    public void HostilePresent_WaitsForClear_ThenGrabsAndResumes()
    {
        var h = new Harness { Hostiles = true, InCombat = true };
        ShortcutSourceCoordinator c = h.Build();
        c.TryBegin(815, new RoomKey(8, 1699));

        c.OnWalkEvent(Finished(new RoomKey(2, 50)));   // arrived, but a hostile is up
        Assert.Empty(h.Gets);                          // not settled — no grab yet
        Assert.True(c.Active);

        // A lull in the fight (combat flag drops) but a hostile still stands — not settled.
        h.InCombat = false;
        c.OnCombatStateChanged();
        Assert.Empty(h.Gets);

        h.Hostiles = false;                            // auto-combat cleared it
        c.OnCombatStateChanged();

        Assert.Equal(new[] { "item815" }, h.Gets);
        h.Carried.Add(815);
        h.FireScheduled();
        Assert.Equal(new[] { new RoomKey(8, 1699) }, h.LiveWalkedToDest);
    }

    [Fact]
    public void InventoryPickup_DuringWait_ResumesImmediately()
    {
        var h = new Harness { Hostiles = true };
        ShortcutSourceCoordinator c = h.Build();
        c.TryBegin(815, new RoomKey(8, 1699));
        c.OnWalkEvent(Finished(new RoomKey(2, 50)));

        // Auto-get grabbed the drop mid-fight-cleanup.
        h.Carried.Add(815);
        c.OnInventoryChanged();

        Assert.Equal(new[] { new RoomKey(8, 1699) }, h.LiveWalkedToDest);
        Assert.False(c.Active);
    }

    [Fact]
    public void SourceUnreachable_FallsBackToLongRoute()
    {
        var h = new Harness();
        ShortcutSourceCoordinator c = h.Build();
        c.TryBegin(815, new RoomKey(8, 1699));

        c.OnWalkEvent(Failed());

        Assert.Equal(new[] { new RoomKey(8, 1699) }, h.LiveWalkedToDest);
        Assert.Empty(h.Gets);
        Assert.False(c.Active);
    }

    [Fact]
    public void UserRedirect_WhileSettling_StandsDown()
    {
        var h = new Harness { Hostiles = true };
        ShortcutSourceCoordinator c = h.Build();
        c.TryBegin(815, new RoomKey(8, 1699));
        c.OnWalkEvent(Finished(new RoomKey(2, 50)));

        c.OnWalkEvent(Started());   // user drove somewhere

        Assert.False(c.Active);
        h.Hostiles = false;
        c.OnCombatStateChanged();   // no effect — abandoned
        Assert.Empty(h.LiveWalkedToDest);
    }
}

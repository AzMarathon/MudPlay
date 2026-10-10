using MudPlay.Game.Inventory;
using Xunit;

namespace MudPlay.Tests;

// AutoOpenManager asks for one open for each flagged container that newly enters
// the pack, one at a time, and holds or forgets an owed open by what is going on.
// The baseline is seeded silently on the first change after inventory loads, so
// containers already carried at connect aren't opened. The open itself and the
// read after it are ChestOpenTracker's; AutoOpenChestOffloadTests runs the two
// together.
public sealed class AutoOpenManagerTests
{
    private sealed class Harness
    {
        public AutoOpenManager Open { get; }

        // What the engine asked to have opened, in order.
        public List<string> Opened { get; } = new();

        // Mutable carried list the manager reads on each change.
        public List<string> Carried { get; } = new();

        // canonical name -> (Number, AutoOpen flag). Absent = not an item.
        public Dictionary<string, (int Number, bool AutoOpen)> Items { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public bool Enabled { get; set; } = true;
        public bool Loaded { get; set; } = true;
        public bool TrackerBusy { get; set; }
        public bool Sweeping { get; set; }
        public bool GateOpen { get; set; } = true;
        public bool SneakKept { get; set; }
        public Dictionary<string, int> ComingBack { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Harness()
        {
            Open = new AutoOpenManager(
                carriedItems: () => Carried,
                resolve: Resolve,
                isEnabled: () => Enabled,
                isLoaded: () => Loaded,
                open: name =>
                {
                    if (TrackerBusy) return false;
                    Opened.Add(name);
                    return true;
                })
            {
                SuppressDuringSweep = () => Sweeping,
                SendGateOpen = () => GateOpen,
                SneakKept = () => SneakKept,
                ComingBack = name => ComingBack.GetValueOrDefault(name),
            };
        }

        private AutoOpenManager.ResolvedOpen? Resolve(string entry)
        {
            if (!Items.TryGetValue(entry.Trim(), out (int Number, bool AutoOpen) g))
                return null;
            return new AutoOpenManager.ResolvedOpen(g.Number, entry.Trim(), g.AutoOpen);
        }

        // Seed the silent baseline (first change after load).
        public void Seed() => Open.OnInventoryChanged();

        public void PickUp(string name)
        {
            Carried.Add(name);
            Open.OnInventoryChanged();
        }

        // The tracker has read the open: it gave something, or nothing.
        public void Settle(bool gaveSomething = true) => Open.OnOpenSettled(new ChestOpenTracker.OpenResult(
            Array.Empty<string>(),
            gaveSomething ? new[] { ("ruby", 1) } : Array.Empty<(string, int)>(),
            CurrencyHoldings.Empty));
    }

    [Fact]
    public void NewFlaggedContainer_IsOpenedOnce()
    {
        Harness h = new();
        h.Items["small sack"] = (100, true);

        h.Seed();                           // empty pack — seeds baseline
        h.PickUp("small sack");
        h.Open.OnInventoryChanged();        // the same pack again

        Assert.Equal(new[] { "small sack" }, h.Opened);
    }

    [Fact]
    public void SeveralContainers_AreOpenedOneAtATime()
    {
        Harness h = new();
        h.Items["small sack"] = (100, true);
        h.Items["leather pouch"] = (101, true);
        h.Seed();

        // Each pickup arrives as its own inventory change (separate "You took"
        // lines), even with different names — the real get-burst shape.
        h.PickUp("small sack");
        h.PickUp("leather pouch");
        Assert.Equal(new[] { "small sack" }, h.Opened);          // the pouch waits for the sack's read
        Assert.Equal(new[] { "leather pouch" }, h.Open.Owed);

        h.Settle();
        Assert.Equal(new[] { "small sack", "leather pouch" }, h.Opened);

        h.Settle();
        Assert.Empty(h.Open.Owed);
        Assert.Null(h.Open.Opening);
    }

    [Fact]
    public void UnflaggedItem_IsNotOpened()
    {
        Harness h = new();
        h.Items["torch"] = (200, false);    // not a flagged container

        h.Seed();
        h.PickUp("torch");

        Assert.Empty(h.Opened);
    }

    [Fact]
    public void ContainerCarriedAtConnect_NotOpened()
    {
        Harness h = new();
        h.Items["small sack"] = (100, true);
        h.Carried.Add("small sack");        // already carried before first change

        h.Seed();                           // baseline includes the sack
        h.Open.OnInventoryChanged();        // no new copy entered

        Assert.Empty(h.Opened);
    }

    [Fact]
    public void MasterDisabled_NothingOpens_ThenOrOnceBackOn()
    {
        Harness h = new() { Enabled = false };
        h.Items["small sack"] = (100, true);

        h.Seed();
        h.PickUp("small sack");
        h.Enabled = true;
        h.Open.OnInventoryChanged();

        Assert.Empty(h.Opened);
    }

    // The Auto-All switch going off while an open is still owed takes it along:
    // nothing automatic goes out afterwards, and it doesn't come back with the switch.
    [Fact]
    public void SwitchedOffWhileAnOpenIsOwed_ForgetsIt()
    {
        Harness h = new() { TrackerBusy = true };
        h.Items["small sack"] = (100, true);
        h.Seed();
        h.PickUp("small sack");
        Assert.Equal(new[] { "small sack" }, h.Open.Owed);

        h.Enabled = false;
        h.Open.Recheck();
        h.Enabled = true;
        h.TrackerBusy = false;
        h.Open.Recheck();

        Assert.Empty(h.Opened);
        Assert.Empty(h.Open.Owed);
    }

    // A container picked up during a Roomba sweep is one it is carrying to another
    // room.
    [Fact]
    public void PickedUpDuringASweep_IsNotOpened_EvenAfterIt()
    {
        Harness h = new() { Sweeping = true };
        h.Items["small sack"] = (100, true);
        h.Seed();

        h.PickUp("small sack");
        h.Sweeping = false;
        h.Open.OnInventoryChanged();

        Assert.Empty(h.Opened);
    }

    [Theory]
    [InlineData("gate")]
    [InlineData("sneak")]
    [InlineData("tracker")]
    public void Held_TheOpenWaits_AndGoesOutOnceClear(string hold)
    {
        Harness h = new();
        h.Items["small sack"] = (100, true);
        h.Seed();
        if (hold == "gate") h.GateOpen = false;
        if (hold == "sneak") h.SneakKept = true;
        if (hold == "tracker") h.TrackerBusy = true;

        h.PickUp("small sack");
        Assert.Empty(h.Opened);
        Assert.NotNull(h.Open.HeldFor);

        h.GateOpen = true;
        h.SneakKept = false;
        h.TrackerBusy = false;
        h.Open.Recheck();

        Assert.Equal(new[] { "small sack" }, h.Opened);
        Assert.Null(h.Open.HeldFor);
    }

    [Fact]
    public void AnOpenThatGaveNothing_IsNotAskedForAgain()
    {
        Harness h = new();
        h.Items["small sack"] = (100, true);
        h.Seed();
        h.PickUp("small sack");

        h.Settle(gaveSomething: false);     // still in the pack
        h.Open.OnInventoryChanged();
        h.Open.Recheck();

        Assert.Equal(new[] { "small sack" }, h.Opened);
    }

    // What a hang-up penalty dropped is picked up again by the client itself.
    [Fact]
    public void ACopyTheClientIsPickingBackUp_IsNotAnArrival()
    {
        Harness h = new();
        h.Items["small sack"] = (100, true);
        h.Seed();
        h.ComingBack["small sack"] = 1;

        h.PickUp("small sack");

        Assert.Empty(h.Opened);
    }

    [Fact]
    public void AnotherCharacter_StartsFromItsOwnPack()
    {
        Harness h = new();
        h.Items["small sack"] = (100, true);
        h.Seed();                           // the first character carried none

        h.Open.Reset();
        h.Carried.Add("small sack");        // the next one carries a sack already
        h.Open.OnInventoryChanged();

        Assert.Empty(h.Opened);
    }
}

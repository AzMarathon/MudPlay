using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using MudPlay.Game.Inventory;
using MudPlay.Services;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

public sealed class ChestOpenTrackerTests : IDisposable
{
    private readonly InventoryManager _inv = new(log: null, itemWeightResolver: null, slotResolver: null);
    private readonly LineExtractor _lines = new(new TerminalEmulator(80, 24));
    private readonly ProfileService _profile = new();
    private readonly OutboundOpenObserver _typed = new();
    private readonly List<string> _sent = new();
    private readonly List<Action> _scheduled = new();
    private ChestOpenTracker _tracker;

    public ChestOpenTrackerTests()
    {
        _inv.AttachLineExtractor(_lines);
        _profile.LoadBlank();
        _tracker = NewTracker();
        _tracker.SayLootToRoom = true;   // most tests read the room line; off is the default
    }

    private ChestOpenTracker NewTracker() => new(
        _inv, _profile, _typed,
        isContainer: name => name.EndsWith("chest", StringComparison.OrdinalIgnoreCase),
        send: _sent.Add,
        schedule: (_, a) => _scheduled.Add(a),
        post: a => a(),
        runicName: () => "runic");

    public void Dispose()
    {
        _tracker.Dispose();
        _inv.Dispose();
    }

    private void Feed(string text)
    {
        FieldInfo? field = typeof(LineExtractor).GetField(
            "LineEmitted", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(_lines) is Action<LineExtractor.EmittedLine> handler)
            handler(new LineExtractor.EmittedLine(
                text, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));
    }

    private void Inventory(string carrying, int gold)
    {
        Feed($"You are carrying {gold} gold crowns, {carrying}");
        Feed("You have no keys.");
        Feed($"Wealth:    {gold * 100} copper farthings");
        Feed("Encumbrance:    36/2880  -  Light  [1%]");
    }

    private void RunScheduled()
    {
        Action[] due = _scheduled.ToArray();
        _scheduled.Clear();
        foreach (Action a in due) a();
    }

    // The Open button flow: `i`, then `open`, then `i` again after the loot spills.
    private void OpenOakChest()
    {
        Inventory("oak chest, 2 rusty dagger", gold: 10);   // the fight's daggers are already in the pack
        _tracker.Open("oak chest");
        Inventory("oak chest, 2 rusty dagger", gold: 10);   // the before-read
        RunScheduled();                                      // settle delay → the after `i`
        Inventory("2 rusty dagger, 2 moonstone, ruby", gold: 15);
    }

    [Fact]
    public void Open_ListsOnlyWhatThatOpenAdded_AndSaysItToTheRoom()
    {
        OpenOakChest();

        Assert.Equal(new[] { "i", "open oak chest", "i", ".oak chest dropped: 2 moonstone, ruby, 5 gold" }, _sent);
        Assert.Equal(new[] { ("moonstone", 2), ("ruby", 1) },
                     _tracker.Loot(_inv.Snapshot.CarriedItems));
        Assert.Equal(5, _tracker.Coin.Gold);
    }

    // Out of the box nothing is said to the room: the list and the coin are kept all
    // the same.
    [Fact]
    public void SayingLootToTheRoom_IsOffUntilTicked()
    {
        _profile.LoadBlank();
        Assert.False(_tracker.SayLootToRoom);

        OpenOakChest();

        Assert.Equal(new[] { "i", "open oak chest", "i" }, _sent);
        Assert.Equal(new[] { ("moonstone", 2), ("ruby", 1) }, _tracker.Loot(_inv.Snapshot.CarriedItems));
        Assert.Equal(5, _tracker.Coin.Gold);

        _tracker.SayLootToRoom = true;
        Assert.True(_profile.Current!.SayChestLootToRoom);   // kept on the character
    }

    [Fact]
    public void List_OutlivesTheWindow_AndReloadsFromTheProfile()
    {
        OpenOakChest();
        _tracker.Dispose();

        _tracker = NewTracker();   // a fresh tracker reads the saved list back

        Assert.Equal(new[] { ("moonstone", 2), ("ruby", 1) },
                     _tracker.Loot(_inv.Snapshot.CarriedItems));
        Assert.Equal(5, _tracker.Coin.Gold);
    }

    [Fact]
    public void ConfirmedSale_RemoveAndClear_TakeItemsOff()
    {
        OpenOakChest();

        Feed("You sold 2 moonstone for 400 copper farthings.");
        Assert.Equal(new[] { ("ruby", 1) }, _tracker.Loot(new[] { "2 moonstone", "ruby" }));

        _tracker.RemoveItem("ruby");
        Assert.Empty(_tracker.Loot(new[] { "ruby" }));

        OpenOakChestAgain();
        _tracker.Clear();
        Assert.Empty(_tracker.Loot(_inv.Snapshot.CarriedItems));
        Assert.Equal(0, _tracker.Coin.TotalCopperValue);
        Assert.Null(_profile.Current!.ChestLoot);
    }

    // A Drop from the tab goes out as `hide` under "Hide items when discarding", and
    // the game then answers "You hid …" instead of "You dropped …".
    [Fact]
    public void ConfirmedHide_TakesItemsOff_LikeADrop()
    {
        OpenOakChest();

        Feed("You hid 2 moonstone.");   // Paradigm's counted echo
        Assert.Equal(new[] { ("ruby", 1) }, _tracker.Loot(new[] { "2 moonstone", "ruby" }));

        Feed("You hid ruby.");
        Assert.Empty(_tracker.Loot(new[] { "ruby" }));
        Assert.Empty(_profile.Current!.ChestLoot!.Items);   // saved, the coin tally stays
    }

    private void OpenOakChestAgain()
    {
        _tracker.Open("oak chest");
        Inventory("oak chest", gold: 15);
        RunScheduled();
        Inventory("sapphire", gold: 15);
    }

    [Fact]
    public void TwoChestsBackToBack_AddUp_WithoutDoubleCounting()
    {
        // The first chest gives 2 moonstone; the second 1 moonstone and a ruby. The
        // second open's before already holds the first chest's loot, so the list ends
        // at 3 moonstone and a ruby — not 5.
        Inventory("oak chest, iron chest", gold: 10);
        _tracker.Open("oak chest");
        Inventory("oak chest, iron chest", gold: 10);
        RunScheduled();
        Inventory("iron chest, 2 moonstone", gold: 10);

        _tracker.Open("iron chest");
        Inventory("iron chest, 2 moonstone", gold: 10);
        RunScheduled();
        Inventory("3 moonstone, ruby", gold: 12);

        Assert.Equal(new[] { ("moonstone", 3), ("ruby", 1) }, _tracker.Loot(_inv.Snapshot.CarriedItems));
        Assert.Contains(".oak chest dropped: 2 moonstone", _sent);
        Assert.Contains(".iron chest dropped: moonstone, ruby, 2 gold", _sent);
    }

    [Fact]
    public void SecondOpenClick_WhileTheFirstIsInFlight_WaitsItsTurn()
    {
        Inventory("oak chest, iron chest", gold: 10);
        _tracker.Open("oak chest");
        _tracker.Open("iron chest");                      // clicked straight away — queued
        Inventory("oak chest, iron chest", gold: 10);    // oak's before-read
        Assert.Equal(new[] { "i", "open oak chest" }, _sent);

        RunScheduled();
        Inventory("iron chest, 2 moonstone", gold: 10);  // oak's after-read → iron starts
        Assert.Equal("i", _sent.Last());
        Inventory("iron chest, 2 moonstone", gold: 10);  // iron's before-read
        RunScheduled();
        Inventory("3 moonstone, ruby", gold: 10);

        Assert.Contains("open iron chest", _sent);
        Assert.Equal(new[] { ("moonstone", 3), ("ruby", 1) }, _tracker.Loot(_inv.Snapshot.CarriedItems));
    }

    [Fact]
    public void ChestTypedOpenWhileTheButtonsReadIsOut_IsCountedOnce()
    {
        // The iron chest's loot lands before the oak chest's before-read parses. The
        // before is taken from the moment it was typed, so its loot still counts.
        Inventory("oak chest, iron chest", gold: 10);
        _tracker.Open("oak chest");
        _typed.ObserveOutbound(Encoding.Latin1.GetBytes("open iron chest\r\n"));
        Inventory("oak chest, ruby", gold: 10);          // before-read already shows the ruby
        RunScheduled();
        Inventory("ruby, 2 moonstone", gold: 10);

        Assert.Equal(new[] { ("ruby", 1), ("moonstone", 2) }, _tracker.Loot(_inv.Snapshot.CarriedItems));
        Assert.Contains(_sent, l => l.StartsWith(".oak chest + iron chest dropped: "));
    }

    [Fact]
    public void ChestTypedOpenAfterTheAfterReadWentOut_GetsAFollowUpRead()
    {
        Inventory("oak chest, iron chest", gold: 10);
        _tracker.Open("oak chest");
        Inventory("oak chest, iron chest", gold: 10);
        RunScheduled();                                  // oak's after `i` is out
        _typed.ObserveOutbound(Encoding.Latin1.GetBytes("open iron chest\r\n"));
        Inventory("iron chest, 2 moonstone", gold: 10);  // oak's after-read, before iron's spill
        RunScheduled();                                  // the follow-up after `i`
        Inventory("2 moonstone, ruby", gold: 10);

        Assert.Equal(new[] { ("moonstone", 2), ("ruby", 1) }, _tracker.Loot(_inv.Snapshot.CarriedItems));
        Assert.Contains(".iron chest dropped: ruby", _sent);
    }

    [Fact]
    public void TypedOpen_IsTrackedToo()
    {
        Inventory("oak chest, 2 rusty dagger", gold: 10);

        _typed.ObserveOutbound(Encoding.Latin1.GetBytes("open chest\r\n"));
        RunScheduled();
        Inventory("2 rusty dagger, sapphire", gold: 10);

        Assert.Equal(new[] { ("sapphire", 1) }, _tracker.Loot(_inv.Snapshot.CarriedItems));
        Assert.Contains(".oak chest dropped: sapphire", _sent);
    }

    // With the master switch off a typed open draws no `i` and no loot line from
    // the client ("stop those too"; user, 2026-10-10). The open is not settled as
    // having given nothing: the user's own next `i` lists what it gave.
    [Fact]
    public void TypedOpen_MasterSwitchOff_SendsNothing_AndIsReadByTheUsersNextInventory()
    {
        bool off = true;
        _tracker.MasterSwitchOff = () => off;
        Inventory("oak chest, 2 rusty dagger", gold: 10);

        _typed.ObserveOutbound(Encoding.Latin1.GetBytes("open chest\r\n"));
        RunScheduled();

        Assert.Empty(_sent);
        Assert.Empty(_scheduled);                                   // no timeout to settle it on stale data
        Assert.Empty(_tracker.Loot(_inv.Snapshot.CarriedItems));    // not read, not "gave nothing"

        Inventory("2 rusty dagger, sapphire", gold: 10);            // the user types `i`

        Assert.Equal(new[] { ("sapphire", 1) }, _tracker.Loot(_inv.Snapshot.CarriedItems));
        Assert.Empty(_sent);                                        // and still nothing said to the room
    }

    [Fact]
    public void TypedOpen_OfSomethingThatIsntACarriedChest_IsIgnored()
    {
        Inventory("oak chest", gold: 10);

        _typed.ObserveOutbound(Encoding.Latin1.GetBytes("open door\r\n"));

        Assert.Empty(_scheduled);
    }

    [Fact]
    public void AnnounceLines_SplitLongListsAcrossLines()
    {
        var items = Enumerable.Range(1, 12).Select(i => ($"long item name number {i}", 1)).ToList();

        IReadOnlyList<string> lines = ChestOffloadPlanner.AnnounceLines("oak chest", items, new[] { "5 gold" }, 120);

        Assert.True(lines.Count > 1);
        Assert.All(lines, l => Assert.True(l.Length <= 120, l));
        Assert.StartsWith("oak chest dropped: ", lines[0]);
        Assert.StartsWith("oak chest (more): ", lines[1]);
        Assert.EndsWith("5 gold", lines[^1]);
    }

    [Fact]
    public void AnnounceLines_NothingGiven_SaysNothing()
        => Assert.Empty(ChestOffloadPlanner.AnnounceLines("oak chest", Array.Empty<(string, int)>(), Array.Empty<string>(), 120));
}

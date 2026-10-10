using MudPlay.Game.Map;
using Xunit;
using static MudPlay.Tests.RoombaHouse;

namespace MudPlay.Tests;

// What recon makes a room's record from: its display is the floor, on every pass
// through it, and a list that isn't shown to be a display can only add to that.
// Played out in RoombaHouse against the real sweep manager.
public sealed class GhSweepReconRecordTests : IDisposable
{
    private readonly RoombaHouse _house = new();

    public void Dispose() => _house.Dispose();

    private GhSweepManager Sweep => _house.Sweep;

    // A list that follows neither a room name nor the echo of a search may be part
    // of a floor only: a reply to a search with its echo knocked away, say. Read
    // while the walk is held in the armour room, and taken for the whole floor, it
    // wiped the war hammer off the room's record and cut the maces to two, and
    // neither was sorted.
    [Fact]
    public void AListNotShownToBeADisplay_AddsToARoomsRecord_AndTakesNothingOffIt()
    {
        _house.Floor[B]["mace"] = 3;
        _house.Floor[B]["war hammer"] = 1;
        Assert.True(Sweep.Start());

        int walks = 0;
        _house.PlayOn(beforeEach: () =>
        {
            if (_house.NextCommand == "n" && ++walks == 2) _house.HoldTheWalk();   // into the armour room
        });
        Assert.Equal(GhSweepManager.SweepPhase.Reconning, Sweep.Phase);
        Assert.Equal(new[] { "3 mace", "war hammer" }, Sweep.ObservedItemsAt(B));

        _house.Wire(_house.Prompt, "You notice 2 mace, iron helm here.");
        Assert.Equal(new[] { "3 mace", "war hammer", "iron helm" }, Sweep.ObservedItemsAt(B));

        _house.ReleaseTheWalk();
        _house.PlayOn();
        Assert.Equal(GhSweepManager.SweepPhase.Idle, Sweep.Phase);
        Assert.Equal(3, _house.Floor[A].GetValueOrDefault("mace"));
        Assert.Equal(1, _house.Floor[A].GetValueOrDefault("war hammer"));
    }

    // An inventory-only sweep has no final lap to put a record right. The middle
    // room is passed twice; emptied between the two, its second display lists no
    // floor, and that is the room: nothing is logged there.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InventoryOnly_ARoomEmptiedBetweenItsTwoPasses_IsLoggedEmpty(bool searching)
    {
        _house.Labels.SetSearchForHidden(searching);
        _house.Floor[C]["mace"] = 2;
        _house.Floor[B]["chain shirt"] = 1;
        Assert.True(Sweep.Start(GhSweepManager.SweepMode.InventoryOnly));

        int walks = 0;
        bool loggedOnTheFirstPass = false;
        _house.PlayOn(beforeEach: () =>
        {
            if (_house.NextCommand is not ("n" or "s") || ++walks != 3) return;
            loggedOnTheFirstPass = _house.LoggedAt(C).ContainsKey("mace");
            _house.Floor[C].Clear();   // ahead of the second pass through C
        });

        Assert.True(loggedOnTheFirstPass);
        Assert.Equal(GhSweepManager.SweepPhase.Idle, Sweep.Phase);
        Assert.Empty(_house.LoggedAt(C));
        Assert.Empty(Sweep.ObservedItemsAt(C));
        Assert.Equal(new Dictionary<string, int> { ["chain shirt"] = 1 }, _house.LoggedAt(B));
        Assert.Contains(_house.Sent, s => s.StartsWith(
            "bg Roomba inventory complete - inventoried 1 item(s). ", StringComparison.Ordinal));
    }

    // The whole room block, with someone standing in the room: the rows between the
    // room's name and its exits are the display, and a search's reply after the
    // exits line is not.
    [Fact]
    public void ADisplayWithItsDescriptionAndWhoIsThere_IsReadAsTheFloor_AndASearchAfterItAsHidden()
    {
        _house.AlsoHere = "a bronze guardian";
        _house.Labels.SetSearchForHidden(true);
        _house.Floor[B]["mace"] = 3;
        _house.Stash[B]["mace"] = 2;
        _house.Stash[B]["war hammer"] = 1;
        Assert.True(Sweep.Start());

        (int, int, int, int)? ledger = null;
        _house.PlayOn(beforeEach: () =>
        {
            if (Sweep.Phase == GhSweepManager.SweepPhase.Sorting) ledger ??= Sweep.FloorLedgerAt(B);
        });

        // Three on display in one stack; three hidden in two.
        Assert.Equal((1, 3, 2, 3), ledger);
        Assert.Equal(GhSweepManager.SweepPhase.Idle, Sweep.Phase);
        Assert.Empty(Sweep.LeftInPlace);
        Assert.Equal(5, _house.Floor[A].GetValueOrDefault("mace"));
        Assert.Equal(1, _house.Floor[A].GetValueOrDefault("war hammer"));
        Assert.Empty(_house.SweepLog("a list read at "));
    }
}

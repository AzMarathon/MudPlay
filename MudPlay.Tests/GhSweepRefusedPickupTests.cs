using MudPlay.Game.Map;
using Xunit;
using static MudPlay.Tests.RoombaHouse;

namespace MudPlay.Tests;

// Pickups the game refuses because a stack is smaller by sort time than recon
// recorded it. What must hold whatever the room turns out to hold: every copy that
// left a floor is delivered or reported, and none is left in the pack unsaid. Played
// out in RoombaHouse against the real sweep manager.
public sealed class GhSweepRefusedPickupTests : IDisposable
{
    private readonly RoombaHouse _house = new();

    public void Dispose() => _house.Dispose();

    private GhSweepManager Sweep => _house.Sweep;

    private int MovedItems() => Sweep.MovedSoFar.Sum(m => m.Count);

    private int InTheHouse(string item) => new[] { A, B, C }.Sum(room =>
        _house.Floor[room].GetValueOrDefault(item) + _house.Stash[room].GetValueOrDefault(item));

    private IEnumerable<string> LooksAndPickups() =>
        _house.Sent.Where(c => c == "l" || c.StartsWith("get ", StringComparison.Ordinal));

    // Change the house once, as the sort begins: someone was at the floors meanwhile.
    private void PlayOn_WithTheFloorsChangedBeforeTheSort(Action change)
    {
        bool changed = false;
        _house.PlayOn(beforeEach: () =>
        {
            if (changed || Sweep.Phase != GhSweepManager.SweepPhase.Sorting) return;
            changed = true;
            change();
        });
        Assert.True(changed);
    }

    // Five maces in plain sight and one hidden were recorded; three are in plain
    // sight by sort time. `get 5 mace` is refused whole. The hidden one's `get mace`
    // was queued behind it, and the game serves a `get` from the copies in plain
    // sight first: sent next, it took one of the three, was counted as part of the
    // refused five, and the hidden pickup then took another, which was delivered by
    // nobody. The hidden pickup now waits until the stack in plain sight is settled.
    [Fact]
    public void Paradigm_ARefusedVisibleStack_HoldsBackItsHiddenTwin_UntilItIsSettled()
    {
        _house.Labels.SetSearchForHidden(true);
        _house.Floor[B]["mace"] = 5;
        _house.Stash[B]["mace"] = 1;
        Assert.True(Sweep.Start());
        PlayOn_WithTheFloorsChangedBeforeTheSort(() => _house.Floor[B]["mace"] = 3);

        Assert.Equal(GhSweepManager.SweepPhase.Idle, Sweep.Phase);
        Assert.Equal(new[] { "get 5 mace", "l", "get 3 mace", "get mace" }, LooksAndPickups());

        // Four were there at sort time; four left the room, and all four arrived.
        int leftTheRoom = 4 - _house.Floor[B].GetValueOrDefault("mace") - _house.Stash[B].GetValueOrDefault("mace");
        Assert.Equal(4, leftTheRoom);
        Assert.Equal(leftTheRoom, _house.Floor[A].GetValueOrDefault("mace"));
        Assert.Equal(leftTheRoom, MovedItems());
        Assert.Equal(4, InTheHouse("mace"));
        Assert.Empty(Sweep.LeftInPlace);
        Assert.Equal(new Dictionary<string, int> { ["mace"] = 4 }, _house.LoggedAt(A));
        Assert.Empty(_house.LoggedAt(B));
    }

    // The same house on Stock, which takes a stack one `get` a copy: the fourth
    // `get mace` of the five is served from the hidden stack the sort's search
    // found, and the fifth is refused. Every copy that left the room is delivered
    // and counted; none stays in the pack.
    [Fact]
    public void Stock_AVisibleStackThatRunsShort_WithAHiddenTwin_DeliversEveryCopyItTook()
    {
        _house.Paradigm = false;
        _house.Labels.SetSearchForHidden(true);
        _house.Floor[B]["mace"] = 5;
        _house.Stash[B]["mace"] = 1;
        Assert.True(Sweep.Start());
        PlayOn_WithTheFloorsChangedBeforeTheSort(() => _house.Floor[B]["mace"] = 3);

        Assert.Equal(GhSweepManager.SweepPhase.Idle, Sweep.Phase);
        Assert.Equal(0, _house.Sends("l"));

        int leftTheRoom = 4 - _house.Floor[B].GetValueOrDefault("mace") - _house.Stash[B].GetValueOrDefault("mace");
        Assert.Equal(4, leftTheRoom);
        Assert.Equal(leftTheRoom, _house.Floor[A].GetValueOrDefault("mace"));
        Assert.Equal(leftTheRoom, MovedItems());
        Assert.Equal(leftTheRoom, _house.Sends("drop mace"));
        Assert.Equal(4, InTheHouse("mace"));
        Assert.All(Sweep.LeftInPlace, left => Assert.Equal((B, "mace"), (left.Room, left.ItemName)));
    }

    // Two stacks of a room both smaller than recorded: two counted pickups refused
    // in one batch. The room is looked at once, after the rest of the batch, and
    // that one display settles both.
    [Fact]
    public void Paradigm_SeveralRefusedStacksInARoom_AreSettledByOneLook()
    {
        _house.Floor[C]["mace"] = 5;
        _house.Floor[C]["war hammer"] = 4;
        _house.Floor[C]["chain shirt"] = 6;
        Assert.True(Sweep.Start());
        PlayOn_WithTheFloorsChangedBeforeTheSort(() =>
        {
            _house.Floor[C]["mace"] = 3;
            _house.Floor[C]["war hammer"] = 2;
        });

        Assert.Equal(GhSweepManager.SweepPhase.Idle, Sweep.Phase);
        Assert.Equal(
            new[] { "get 5 mace", "get 4 war hammer", "get 6 chain shirt", "l", "get 3 mace", "get 2 war hammer" },
            LooksAndPickups());
        Assert.Empty(Sweep.LeftInPlace);
        Assert.Equal(3, _house.Floor[A].GetValueOrDefault("mace"));
        Assert.Equal(2, _house.Floor[A].GetValueOrDefault("war hammer"));
        Assert.Equal(6, _house.Floor[B].GetValueOrDefault("chain shirt"));
        Assert.Empty(_house.Floor[C]);
        Assert.Equal(11, MovedItems());
        Assert.Single(_house.SweepLog("1/3 holds 3 mace, not 5"));
        Assert.Single(_house.SweepLog("1/3 holds 2 war hammer, not 4"));
        Assert.Empty(Sweep.RoomsChangedAfterSort);
    }

    // `get 5 mace` is answered `You cannot carry that much!`: the pack is fuller
    // than reckoned, and it is read again before anything more is tried. The two
    // pickups queued behind the maces used to go out all the same, one of them a
    // room later, where the re-plan had walked to by then.
    [Fact]
    public void Paradigm_APickupTooHeavy_GivesUpTheRestOfTheBatch_UntilThePackIsRead()
    {
        _house.Floor[C]["mace"] = 5;
        _house.Floor[C]["war hammer"] = 1;
        _house.Floor[C]["chain shirt"] = 1;
        bool refused = false;
        _house.TooHeavy = argument =>
            !refused && argument.EndsWith("mace", StringComparison.Ordinal) && (refused = true);
        Assert.True(Sweep.Start());
        _house.PlayOn();

        Assert.True(refused);
        Assert.Equal(GhSweepManager.SweepPhase.Idle, Sweep.Phase);
        List<(Game.Map.RoomKey Room, string Command)> packReadsAndPickups = _house.Played
            .Where(p => p.Command == "i" || p.Command.StartsWith("get ", StringComparison.Ordinal)).ToList();
        Assert.Equal(("get 5 mace", "i"), (packReadsAndPickups[0].Command, packReadsAndPickups[1].Command));
        // One pickup each for the three stacks after that, all in the room they lie in.
        Assert.Equal(new[] { "get 5 mace", "get chain shirt", "get war hammer" },
            packReadsAndPickups.Skip(2).Select(p => p.Command).OrderBy(c => c, StringComparer.Ordinal));
        Assert.All(packReadsAndPickups, p => Assert.Equal(C, p.Room));
        Assert.Empty(Sweep.LeftInPlace);
        Assert.Equal(5, _house.Floor[A].GetValueOrDefault("mace"));
        Assert.Equal(1, _house.Floor[A].GetValueOrDefault("war hammer"));
        Assert.Equal(1, _house.Floor[B].GetValueOrDefault("chain shirt"));
    }

    // A stack refused as smaller than recorded, then a pickup too heavy, in one
    // batch. The room's second look is given up with the rest of the batch: it used
    // to go out later, in another room, and the stack, marked as read again when it
    // never was, was left as gone on the next visit with three copies in plain sight.
    [Fact]
    public void Paradigm_ARefusedStack_ThenAPickupTooHeavy_IsReadAgainOnTheNextVisit()
    {
        _house.Labels.SetSearchForHidden(true);
        _house.Floor[B]["mace"] = 5;
        _house.Stash[B]["mace"] = 1;
        _house.Floor[B]["war hammer"] = 2;
        bool refused = false;
        _house.TooHeavy = argument =>
            !refused && argument.EndsWith("war hammer", StringComparison.Ordinal) && (refused = true);
        Assert.True(Sweep.Start());
        PlayOn_WithTheFloorsChangedBeforeTheSort(() => _house.Floor[B]["mace"] = 3);

        Assert.True(refused);
        Assert.Equal(GhSweepManager.SweepPhase.Idle, Sweep.Phase);
        Assert.All(_house.Played.Where(p => p.Command == "l"), p => Assert.Equal(B, p.Room));
        Assert.Equal(1, _house.Sends("l"));
        Assert.Empty(Sweep.LeftInPlace);
        Assert.Equal(4, _house.Floor[A].GetValueOrDefault("mace"));
        Assert.Equal(2, _house.Floor[A].GetValueOrDefault("war hammer"));
        Assert.Equal(4, InTheHouse("mace"));
        Assert.Empty(_house.LoggedAt(B));
    }

    // Two hidden war hammers were recorded and one is there by sort time. The
    // second search shows one, and one is picked up; the other comes off the room's
    // record with it, or the item-location log would keep a war hammer in a room
    // that holds none.
    [Fact]
    public void Paradigm_AHiddenStackFoundSmaller_ComesOffTheRoomsRecordAtItsRecordedCount()
    {
        _house.Labels.SetSearchForHidden(true);
        _house.Stash[B]["war hammer"] = 2;
        Assert.True(Sweep.Start());
        PlayOn_WithTheFloorsChangedBeforeTheSort(() => _house.Stash[B]["war hammer"] = 1);

        Assert.Equal(GhSweepManager.SweepPhase.Idle, Sweep.Phase);
        Assert.Equal(new[] { "get 2 war hammer", "get war hammer" },
            _house.Sent.Where(c => c.StartsWith("get ", StringComparison.Ordinal)));
        Assert.Single(_house.SweepLog("1/2 holds 1 war hammer, not 2"));
        Assert.Empty(Sweep.LeftInPlace);
        Assert.Equal(1, _house.Floor[A].GetValueOrDefault("war hammer"));
        Assert.Empty(_house.Stash[B]);
        Assert.Empty(_house.LoggedAt(B));
        Assert.Equal(new Dictionary<string, int> { ["war hammer"] = 1 }, _house.LoggedAt(A));
    }

    // One of the two refused stacks is gone altogether: the one look still serves
    // both, taking what is left of the one and leaving the other as gone.
    [Fact]
    public void Paradigm_OneLook_TakesTheStackThatIsThere_AndLeavesTheOneThatIsNot()
    {
        _house.Floor[C]["mace"] = 5;
        _house.Floor[C]["war hammer"] = 4;
        Assert.True(Sweep.Start());
        PlayOn_WithTheFloorsChangedBeforeTheSort(() =>
        {
            _house.Floor[C]["mace"] = 3;
            _house.Floor[C].Remove("war hammer");
        });

        Assert.Equal(GhSweepManager.SweepPhase.Idle, Sweep.Phase);
        Assert.Equal(new[] { "get 5 mace", "get 4 war hammer", "l", "get 3 mace" }, LooksAndPickups());
        GhSweepItemFound left = Assert.Single(Sweep.LeftInPlace);
        Assert.Equal((C, "war hammer", GhLeftReason.GoneBySortTime), (left.Room, left.ItemName, left.Reason));
        Assert.Equal(3, _house.Floor[A].GetValueOrDefault("mace"));
        Assert.Equal(3, MovedItems());
    }
}

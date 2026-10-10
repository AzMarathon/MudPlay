using MudPlay.Game.Map;
using Xunit;
using static MudPlay.Tests.RoombaHouse;

namespace MudPlay.Tests;

// The lap Roomba walks once its sort is done. It used to record nothing it saw, so
// the item-location log kept the floors of before the sort, and a room someone had
// changed meanwhile went unnoticed. Played out in RoombaHouse against the real sweep
// manager.
public sealed class GhSweepFinalLapTests : IDisposable
{
    private readonly RoombaHouse _house = new();

    public void Dispose() => _house.Dispose();

    private GhSweepManager Sweep => _house.Sweep;

    private int MovesSent() => _house.Sends("get ") + _house.Sends("drop ");

    // A war hammer and a chain shirt lie in the middle room; each belongs in a room
    // of its own, where two maces and a leather cap already are.
    private void AHouseWithTwoStraysInTheMiddleRoom()
    {
        _house.Floor[A]["mace"] = 2;
        _house.Floor[B]["leather cap"] = 1;
        _house.Floor[C]["war hammer"] = 1;
        _house.Floor[C]["chain shirt"] = 1;
    }

    [Fact]
    public void TheFinalLap_WritesEachRoomAsItIsAfterTheSort()
    {
        AHouseWithTwoStraysInTheMiddleRoom();
        Assert.True(Sweep.Start());
        _house.PlayOn();

        Assert.Equal(GhSweepManager.SweepPhase.Idle, Sweep.Phase);
        Assert.Equal(4, MovesSent());
        Assert.Equal(new Dictionary<string, int> { ["mace"] = 2, ["war hammer"] = 1 }, _house.LoggedAt(A));
        Assert.Equal(new Dictionary<string, int> { ["leather cap"] = 1, ["chain shirt"] = 1 }, _house.LoggedAt(B));
        Assert.Empty(_house.LoggedAt(C));
        // Every room is as the sort left it, so the lap has nothing to say.
        Assert.Empty(_house.SweepLog("final recon at "));
        Assert.Empty(Sweep.RoomsChangedAfterSort);
    }

    // Between the sort and the final lap someone drops two iron helms in the armour
    // room and takes the maces from the weapons room.
    [Fact]
    public void ARoomThatGainedAStack_AndOneThatLostAStack_AreLoggedOnceAndBroughtUpToDate()
    {
        AHouseWithTwoStraysInTheMiddleRoom();
        Assert.True(Sweep.Start());

        bool changed = false;
        _house.PlayOn(beforeEach: () =>
        {
            if (changed || Sweep.Phase != GhSweepManager.SweepPhase.FinalRecon) return;
            changed = true;
            _house.Floor[B]["iron helm"] = 2;
            _house.Floor[A].Remove("mace");
        });

        Assert.True(changed);
        Assert.Equal(GhSweepManager.SweepPhase.Idle, Sweep.Phase);
        // The sort was over: the lap looks, and moves nothing more.
        Assert.Equal(4, MovesSent());

        Assert.Equal(new[]
        {
            "final recon at 1/2: not as the sort left it; 1 more than expected: 2 iron helm; item log brought up to date",
            "final recon at 1/1: not as the sort left it; 1 missing: 2 mace; item log brought up to date",
        }, _house.SweepLog("final recon at ").OrderByDescending(l => l, StringComparer.Ordinal));
        Assert.Equal(new[] { A, B }, Sweep.RoomsChangedAfterSort.OrderBy(r => r.Room));

        Assert.Equal(new Dictionary<string, int> { ["war hammer"] = 1 }, _house.LoggedAt(A));
        Assert.Equal(
            new Dictionary<string, int> { ["leather cap"] = 1, ["chain shirt"] = 1, ["iron helm"] = 2 },
            _house.LoggedAt(B));
        Assert.Empty(_house.LoggedAt(C));
        Assert.Equal(new[] { "war hammer" }, Sweep.ObservedItemsAt(A));
    }

    // A room emptied meanwhile prints no floor list at all. That is a floor with
    // nothing on it, not a room that wasn't read.
    [Fact]
    public void ARoomEmptiedMeanwhile_IsWrittenEmpty()
    {
        AHouseWithTwoStraysInTheMiddleRoom();
        Assert.True(Sweep.Start());

        _house.PlayOn(beforeEach: () =>
        {
            if (Sweep.Phase == GhSweepManager.SweepPhase.FinalRecon) _house.Floor[B].Clear();
        });

        Assert.Equal(GhSweepManager.SweepPhase.Idle, Sweep.Phase);
        Assert.Empty(_house.LoggedAt(B));
        Assert.Equal(
            new[] { "final recon at 1/2: not as the sort left it; 2 missing: leather cap, chain shirt; item log brought up to date" },
            _house.SweepLog("final recon at "));
    }

    // The final lap doesn't search, so it can't see what is hidden. What recon's
    // searches found and the sort didn't take stays on the room's record.
    [Fact]
    public void HiddenStacksTheSortLeft_StayOnTheRoomsRecord()
    {
        _house.Labels.SetSearchForHidden(true);
        _house.Stash[B]["mace"] = 2;          // a weapon hidden in the armour room: sorted out
        _house.Stash[B]["leather cap"] = 1;   // armour hidden where it belongs: left
        Assert.True(Sweep.Start());
        _house.PlayOn();

        Assert.Equal(GhSweepManager.SweepPhase.Idle, Sweep.Phase);
        Assert.Contains("get 2 mace", _house.Sent);
        Assert.Equal(new Dictionary<string, int> { ["mace"] = 2 }, _house.LoggedAt(A));
        Assert.Equal(new Dictionary<string, int> { ["leather cap"] = 1 }, _house.LoggedAt(B));
        Assert.Empty(_house.SweepLog("final recon at "));
    }

    // The final lap can miss a room recon read: its circuit is plotted afresh, and a
    // room too dark to show displays nothing. The strays recon saw there were sorted
    // out of it, so its item-log entry is written from the sort's own account rather
    // than left as recon wrote it.
    [Fact]
    public void ARoomTheFinalLapCouldNotRead_IsWrittenFromTheSortsOwnAccount()
    {
        AHouseWithTwoStraysInTheMiddleRoom();
        Assert.True(Sweep.Start());

        _house.IsDark = room => room.Equals(C) && Sweep.Phase == GhSweepManager.SweepPhase.FinalRecon;
        _house.PlayOn();

        Assert.Equal(GhSweepManager.SweepPhase.Idle, Sweep.Phase);
        Assert.Empty(_house.LoggedAt(C));
        Assert.Equal(
            new[] { "final recon: 1 room(s) not read again; their item-log entries are the sort's own account of them: 1/3" },
            _house.SweepLog("final recon: "));
        Assert.Empty(Sweep.RoomsChangedAfterSort);
    }

    // Several rooms missed are one write of the item-location file between them,
    // not one each: the file holds the whole house and is rewritten whole.
    [Fact]
    public void RoomsTheFinalLapCouldNotRead_AreWrittenInOneGo()
    {
        AHouseWithTwoStraysInTheMiddleRoom();
        Assert.True(Sweep.Start());

        int writesOnTheFinalLap = 0;
        _house.Locations.Changed += () =>
        {
            if (Sweep.Phase == GhSweepManager.SweepPhase.FinalRecon) writesOnTheFinalLap++;
        };
        _house.IsDark = _ => Sweep.Phase == GhSweepManager.SweepPhase.FinalRecon;
        _house.PlayOn();

        Assert.Equal(GhSweepManager.SweepPhase.Idle, Sweep.Phase);
        Assert.Equal(
            new[] { "final recon: 3 room(s) not read again; their item-log entries are the sort's own account of them: 1/1, 1/2, 1/3" },
            _house.SweepLog("final recon: "));
        Assert.Equal(1, writesOnTheFinalLap);
        Assert.Equal(new Dictionary<string, int> { ["mace"] = 2, ["war hammer"] = 1 }, _house.LoggedAt(A));
        Assert.Equal(new Dictionary<string, int> { ["leather cap"] = 1, ["chain shirt"] = 1 }, _house.LoggedAt(B));
        Assert.Empty(_house.LoggedAt(C));
    }

    // And the other way about: a room recon never read has no floor to be held
    // against. What the final lap sees there is written down, and nothing in it is
    // called extra.
    [Fact]
    public void ARoomReconNeverRead_IsWrittenAsSeen_AndHeldAgainstNothing()
    {
        _house.Floor[A]["mace"] = 2;
        _house.Floor[B]["war hammer"] = 1;     // a stray, so that there is a sort and a final lap
        _house.Floor[C]["iron helm"] = 3;
        Assert.True(Sweep.Start());

        _house.IsDark = room => room.Equals(C) && Sweep.Phase != GhSweepManager.SweepPhase.FinalRecon;
        _house.PlayOn();

        Assert.Equal(GhSweepManager.SweepPhase.Idle, Sweep.Phase);
        Assert.Equal(new Dictionary<string, int> { ["iron helm"] = 3 }, _house.LoggedAt(C));
        Assert.Empty(_house.SweepLog("final recon at "));
        Assert.Empty(Sweep.RoomsChangedAfterSort);
    }
}

using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Game.Recovery;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// The plain death-room pickup, which every death uses, in the cases where the room
// is not simply walked into lit with its floor on show. The rule they pin: an armed
// grab is never spent on a guess. When nothing was read, it stays armed for the next
// display of the room, as it did before the spill sweep existed.
public sealed partial class DeathRecoveryManagerTests
{
    private static RoomObservation ObsNowhere()
        => new("Some Other Place", new HashSet<Direction>(new[] { Direction.E, Direction.W }));

    // ----- Recover Now from inside the room ---------------------------

    [Fact]
    public void RecoverNow_InTheRoom_TrackerConfirmed_GetsTheFloor()
    {
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.EnterGates();
        Assert.True(h.Recovery.RecoverNow(h.Latest));
        h.FeedSurvey("a torch");
        h.EnterGates();
        h.Recovery.FeedTestLine("Obvious exits: north");
        Assert.Contains("get torch", h.Sent);
    }

    [Fact]
    public void RecoverNow_InTheRoom_TrackerSuspect_StillGetsTheFloor()
    {
        // The tracker saw a display it couldn't place and is Suspect, still in the
        // death room. The look's floor line arrives before the exits line that
        // re-confirms it. That floor is the room's: no move has gone out.
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.EnterGates();                               // back in the death room, Auto-Recover off
        h.Tracker.NoteRoomObserved(ObsNowhere());
        Assert.Equal(RoomConfidence.Suspect, h.Tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), h.Tracker.State.CurrentRoom!.Key);

        Assert.True(h.Recovery.RecoverNow(h.Latest));
        Assert.Contains("look", h.Sent);
        h.FeedSurvey("a torch");
        h.EnterGates();
        h.Recovery.FeedTestLine("Obvious exits: north");
        Assert.Contains("get torch", h.Sent);

        h.Recovery.FeedTestLine("You took torch.");
        for (int i = 0; i < 3; i++) h.Heartbeat();
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
        Assert.DoesNotContain(h.Sent, s => s.StartsWith("look "));   // no sweep over a pile underfoot
    }

    [Fact]
    public void RecoverNow_InTheRoom_AMoveSentAfterTheLook_ItsDisplayIsNotTakenForThisRooms()
    {
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.EnterGates();
        Assert.True(h.Recovery.RecoverNow(h.Latest));
        System.Threading.Thread.Sleep(5);
        h.Tracker.NoteMoveSent(Direction.N);          // changed their mind and stepped off
        h.Sent.Clear();

        h.FeedSurvey("a torch");                      // the next room's floor, a torch like ours on it
        h.Recovery.FeedTestLine("Obvious exits: south");
        for (int i = 0; i < 3; i++) h.Heartbeat();

        Assert.Empty(h.Sent);
    }

    // ----- walking in -------------------------------------------------

    [Fact]
    public void Stock_DraggedInByAPartyLeader_Gets()
    {
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.Tracker.NoteRoomObserved(Obs3());
        h.Tracker.NoteFollowMove(Direction.S);
        h.FeedSurvey("a torch");
        h.EnterGates();
        Assert.Contains("get torch", h.Sent);
    }

    [Fact]
    public void Stock_DiedInTheRoomTheCharacterRespawnsIn_Gets()
    {
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.FeedSurvey("a torch");
        h.EnterGates();
        Assert.Contains("get torch", h.Sent);
    }

    [Fact]
    public void Stock_ArrivalThatReanchorsASuspectTracker_Gets()
    {
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.Tracker.NoteRoomObserved(Obs3());
        h.Tracker.NoteRoomObserved(ObsNowhere());
        Assert.Equal(RoomConfidence.Suspect, h.Tracker.State.Confidence);
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.FeedSurvey("a torch");
        h.EnterGates();
        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        Assert.Contains("get torch", h.Sent);
    }

    [Fact]
    public void Stock_HostileInTheDeathRoom_DoesNotHoldThePickup()
    {
        using GraphHarness h = new() { Paradigm = false };
        Die(h, new[] { new EquippedItem("iron sword", "Weapon Hand") }, new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.Hostiles = true;
        h.Tracker.NoteRoomObserved(Obs3());
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.FeedSurvey("a torch");
        h.EnterGates();
        Assert.Contains("get torch", h.Sent);
        h.Recovery.FeedTestLine("You took torch.");
        for (int i = 0; i < 4; i++) h.Heartbeat();
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);
        Assert.Equal(new[] { "iron sword" }, h.Latest.UnrecoveredItems);
    }

    [Fact]
    public void Stock_TypedAheadArrival_ConfirmedInPlace_FloorIsNotTakenAsEmpty()
    {
        // The step into the death room lands with one more move still queued, the
        // floor list is emptied by that room change, and the extra move bounces: the
        // room confirms in place. Nothing was read at that moment, so nothing is
        // concluded, and the room's next display is acted on.
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.Tracker.NoteRoomObserved(Obs3());
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.Tracker.NoteMoveSentByObserver(Direction.S);   // one too many
        h.FeedSurvey("a torch");
        h.EnterGates();                               // lands, Pending
        Assert.Equal(RoomConfidence.Pending, h.Tracker.State.Confidence);
        h.Tracker.NoteMoveBlocked();                  // the extra one bounces
        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        for (int i = 0; i < 3; i++) h.Heartbeat();

        h.FeedSurvey("a torch");                      // a later display of the room
        Assert.Contains("get torch", h.Sent);
    }

    [Fact]
    public void Stock_BlindWalkIn_StaysArmedUntilTheRoomIsSeen()
    {
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.Tracker.NoteRoomObserved(Obs3());
        h.Tracker.NoteMoveSent(Direction.S);
        h.Tracker.NoteBlindMove();
        Assert.Equal(new RoomKey(1, 1), h.Tracker.State.CurrentRoom!.Key);
        Assert.True(h.Tracker.EnteredBlind);
        for (int i = 0; i < 3; i++) h.Heartbeat();
        Assert.Empty(h.Sent);

        h.FeedSurvey("a torch");                      // sight is back and the room is looked at
        h.EnterGates();
        Assert.Contains("get torch", h.Sent);
        Assert.False(h.Tracker.EnteredBlind);
    }

    [Fact]
    public void Stock_DarkWalkIn_AFloorOfTheRoomLeftReadOnTheWay_DoesNotSpendTheGrab()
    {
        // A re-display of the room being left is still on its way when the move into
        // the dark death room goes out. It is newer than the move, yet it isn't the
        // death room's floor: in the dark nothing of it was shown.
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.Tracker.NoteRoomObserved(Obs3());
        h.Tracker.NoteMoveSent(Direction.S);
        System.Threading.Thread.Sleep(5);
        h.FeedSurvey("a lantern");
        h.Tracker.NoteDarkRoomEntered();
        for (int i = 0; i < 3; i++) h.Heartbeat();

        h.FeedSurvey("a torch");
        Assert.Contains("get torch", h.Sent);
    }

    [Fact]
    public void Stock_EmptyFloorAtTheWalkIn_ALaterSurveyInTheRoomIsStillActedOn()
    {
        // Lit, displayed, no floor line: empty, and the pile is noted as not here.
        // The grab stays armed all the same: a search may show the room's hidden side.
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.Tracker.NoteRoomObserved(Obs3());
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.EnterGates();
        h.Heartbeat();
        Assert.Empty(h.Sent);
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);

        h.FeedSurvey("a torch");
        Assert.Contains("get torch", h.Sent);
        h.Recovery.FeedTestLine("You took torch.");
        Assert.Equal(DeathRecoveryStatus.Recovered, h.Latest.Status);
    }

    [Fact]
    public void Stock_EmptyFloorAtTheWalkIn_ASurveyBeforeTheNextHeartbeatIsActedOnToo()
    {
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.Tracker.NoteRoomObserved(Obs3());
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.EnterGates();

        h.FeedSurvey("a torch");
        Assert.Contains("get torch", h.Sent);
    }

    [Fact]
    public void Stock_ArmedGrab_IgnoresTheNextRoomsFloor_ReadWithTheMoveOnTheWire()
    {
        // What makes staying armed safe: the next room prints its floor before it
        // confirms, with the move that leads there still pending.
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.Tracker.NoteRoomObserved(Obs3());
        h.Tracker.NoteMoveSent(Direction.S);
        h.Tracker.NoteDarkRoomEntered();              // armed, in the dark
        System.Threading.Thread.Sleep(5);

        h.Tracker.NoteMoveSent(Direction.N);          // walking on
        h.FeedSurvey("a torch");                      // 1/3's floor, a torch like ours on it
        h.Tracker.NoteRoomObserved(Obs3());

        Assert.Empty(h.Sent);
    }

    // ----- a disconnect while armed ------------------------------------

    [Fact]
    public void Paradigm_ArmedGrab_SurvivesADisconnect()
    {
        // A Paradigm walk-in arms the corpse grab after the arrival's survey has
        // printed, so armed-and-waiting is its normal state. The room isn't entered
        // again after logging back in, so nothing would arm it a second time.
        using GraphHarness h = new();   // Paradigm
        Die(h, new[] { new EquippedItem("rusty dagger", "Weapon Hand") }, new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.FeedSurvey("corpse of Ermias");
        h.EnterGates();
        Assert.Empty(h.Sent);

        h.Recovery.NotifyDisconnected();
        h.FeedSurvey("corpse of Ermias");   // the room display after logging back in
        h.EnterGates();

        Assert.Contains("recover corpse Ermias", h.Sent);
    }

    [Fact]
    public void Stock_ArmedGrabInTheDark_SurvivesADisconnect()
    {
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Recovery.AutoRecover = true;
        h.Tracker.NoteRoomObserved(Obs3());
        h.Tracker.NoteMoveSent(Direction.S);
        h.Tracker.NoteDarkRoomEntered();

        h.Recovery.NotifyDisconnected();
        h.FeedSurvey("a torch");
        Assert.Contains("get torch", h.Sent);
    }

    [Fact]
    public void Stock_ADisconnect_TakesTheSweepAway_NotThePickup()
    {
        // Recover Now, then the line drops before the look is answered. Back in, the
        // floor is still got; no sweep follows a recovery the connection cut short.
        using GraphHarness h = new() { Paradigm = false };
        Die(h,
            new[] { new EquippedItem("iron sword", "Weapon Hand"), new EquippedItem("steel helm", "Head") },
            Array.Empty<string>());
        h.EnterGates();
        Assert.True(h.Recovery.RecoverNow(h.Latest));
        h.Recovery.NotifyDisconnected();
        h.Sent.Clear();

        h.FeedSurvey("an iron sword");
        Assert.Contains("get iron sword", h.Sent);
        h.Recovery.FeedTestLine("You took an iron sword.");
        for (int i = 0; i < 5; i++) h.Heartbeat();

        Assert.DoesNotContain(h.Sent, s => s.StartsWith("look "));
        Assert.Equal(DeathRecoveryStatus.Partial, h.Latest.Status);
    }

    // ----- a Recover Now that never arrives -----------------------------

    [Fact]
    public void RecoverNow_WhoseRouteCardsWereClosed_DoesNotForceALaterWalkIn()
    {
        using GraphHarness h = new() { Paradigm = false };
        Die(h, Array.Empty<EquippedItem>(), new[] { "torch" });
        h.Tracker.NoteRoomObserved(Obs3());            // standing next door
        h.Recovery.SetDemandedWalk(_ => true);         // the cards went up
        Assert.True(h.Recovery.RecoverNow(h.Latest));

        h.Recovery.ForgetRecoverNowWalk("the route cards were closed");
        h.Tracker.NoteMoveSentByObserver(Direction.S); // later, by hand, Auto-Recover off
        h.FeedSurvey("a torch");
        h.EnterGates();

        Assert.Empty(h.Sent);
    }
}

using System.Collections.Generic;
using System.Text;
using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// AutoSearchManager — a `search` won't run mid-combat, so the engine defers the
// `sea` past a fight and holds the walker via the Search gate until the room
// clears. The DispatcherTimer ticks (classify delay before a clear-room search,
// settle after a post-fight search) are Avalonia plumbing not exercised in
// headless xUnit; the internal OnClassifyElapsed / OnSettleElapsed stand in for
// those ticks so the state machine runs deterministically.
public sealed class AutoSearchManagerTests
{
    private static string Decode(byte[] b) => Encoding.Latin1.GetString(b).TrimEnd('\r');

    // A confirmed room key for OnRoomChanged; the search is now keyed by room.
    private static RoomKey Key(int room = 1) => new(1, room);

    private static bool SearchHeld(MovementCoordinator c) =>
        c.AssertedGates.Contains(MovementCoordinator.SearchGate);

    // ----- clear room (no fight): searches after the classify delay -----

    [Fact]
    public void ClearRoom_Enabled_SearchesAfterClassify()
    {
        var mgr = new AutoSearchManager(isEnabled: () => true);
        mgr.SetWireSender(_ => { });

        mgr.OnRoomChanged(Key());                 // arm — nothing sent yet
        Assert.Empty(mgr.LastSentForTests);
        mgr.OnClassifyElapsed();             // classify delay fires

        Assert.Single(mgr.LastSentForTests);
        Assert.Equal("sea", Decode(mgr.LastSentForTests[0]));
    }

    [Fact]
    public void ClearRoom_Disabled_SendsNothing()
    {
        var mgr = new AutoSearchManager(isEnabled: () => false);
        mgr.SetWireSender(_ => { });

        mgr.OnRoomChanged(Key());
        mgr.OnClassifyElapsed();

        Assert.Empty(mgr.LastSentForTests);
    }

    [Fact]
    public void ClearRoom_OncePerRoom()
    {
        var mgr = new AutoSearchManager(isEnabled: () => true);
        mgr.SetWireSender(_ => { });

        for (int i = 0; i < 3; i++) { mgr.OnRoomChanged(Key()); mgr.OnClassifyElapsed(); }

        Assert.Equal(3, mgr.LastSentForTests.Count);
        Assert.All(mgr.LastSentForTests, b => Assert.Equal("sea", Decode(b)));
    }

    [Fact]
    public void ClassifyElapsed_ReadsMasterGateLive()
    {
        bool enabled = false;
        var mgr = new AutoSearchManager(isEnabled: () => enabled);
        mgr.SetWireSender(_ => { });

        mgr.OnRoomChanged(Key()); mgr.OnClassifyElapsed();   // off — no send
        enabled = true;
        mgr.OnRoomChanged(Key()); mgr.OnClassifyElapsed();   // on — one send

        Assert.Single(mgr.LastSentForTests);
    }

    [Fact]
    public void Send_ReachesBoundSink()
    {
        var sink = new List<byte[]>();
        var mgr = new AutoSearchManager(isEnabled: () => true);
        mgr.SetWireSender(sink.Add);

        mgr.OnRoomChanged(Key()); mgr.OnClassifyElapsed();

        Assert.Single(sink);
        Assert.Equal("sea", Decode(sink[0]));
    }

    [Fact]
    public void DemandGate_ArmsSearch_WhenMasterOff()
    {
        var mgr = new AutoSearchManager(isEnabled: () => false, isDemandActive: () => true);
        mgr.SetWireSender(_ => { });

        mgr.OnRoomChanged(Key()); mgr.OnClassifyElapsed();

        Assert.Single(mgr.LastSentForTests);
        Assert.Equal("sea", Decode(mgr.LastSentForTests[0]));
    }

    [Fact]
    public void BothGatesOff_SendsNothing()
    {
        var mgr = new AutoSearchManager(isEnabled: () => false, isDemandActive: () => false);
        mgr.SetWireSender(_ => { });

        mgr.OnRoomChanged(Key()); mgr.OnClassifyElapsed();

        Assert.Empty(mgr.LastSentForTests);
    }

    // ----- settle hold only when a loot consumer is armed -----

    [Fact]
    public void ClearRoom_NoLootConsumer_ReleasesGateImmediately_NoSettleHold()
    {
        // report paradigm-20260818-060742: with the get engines off (nothing collects
        // what the search reveals) the settle window is dead time on every room — release
        // the walker the moment the `sea` goes out rather than idling a full settle for it.
        var coord = new MovementCoordinator();
        var mgr = new AutoSearchManager(
            isEnabled: () => true,
            coordinator: coord);              // no get engine, no demand
        mgr.SetWireSender(_ => { });

        mgr.OnRoomChanged(Key());
        Assert.True(SearchHeld(coord));       // held from entry so the sea fires in place
        mgr.OnClassifyElapsed();              // clear room → sea fires

        Assert.Single(mgr.LastSentForTests);
        Assert.Equal("sea", Decode(mgr.LastSentForTests[0]));
        Assert.False(SearchHeld(coord));      // released immediately — no settle idle
    }

    [Fact]
    public void ClearRoom_GetEngineArmed_HoldsForSettle()
    {
        // A get engine is armed to collect the reveal, so the walker is held through the
        // settle window (until OnSettleElapsed) exactly as before.
        var coord = new MovementCoordinator();
        var mgr = new AutoSearchManager(
            isEnabled: () => true,
            hasGetEngineArmed: () => true,
            coordinator: coord);
        mgr.SetWireSender(_ => { });

        mgr.OnRoomChanged(Key());
        mgr.OnClassifyElapsed();

        Assert.Single(mgr.LastSentForTests);
        Assert.True(SearchHeld(coord));       // held for the collection settle
        mgr.OnSettleElapsed();
        Assert.False(SearchHeld(coord));
    }

    // ----- fight in the room: defer + hold, fire on clear -----

    [Fact]
    public void Fight_DefersAndHolds_ThenSearchesOnClear()
    {
        var coord = new MovementCoordinator();
        bool hostiles = true;
        var mgr = new AutoSearchManager(
            isEnabled: () => true,
            hasEngageableHostiles: () => hostiles,
            hasGetEngineArmed: () => true,   // a consumer is armed, so the settle window is held
            coordinator: coord);
        mgr.SetWireSender(_ => { });

        mgr.OnRoomChanged(Key());          // arm
        mgr.OnRoomObserved();         // occupants → fight → defer + hold, no send
        Assert.Empty(mgr.LastSentForTests);
        Assert.True(SearchHeld(coord));

        // A stray classify tick during combat must not leak a mid-fight search.
        mgr.OnClassifyElapsed();
        Assert.Empty(mgr.LastSentForTests);

        hostiles = false;
        mgr.OnRoomObserved();         // room cleared → sea fires, gate held for settle
        Assert.Single(mgr.LastSentForTests);
        Assert.Equal("sea", Decode(mgr.LastSentForTests[0]));
        Assert.True(SearchHeld(coord));

        mgr.OnSettleElapsed();        // settle → release
        Assert.False(SearchHeld(coord));
    }

    [Fact]
    public void HostilePresent_ButAutoCombatOff_Searches_InsteadOfDeadlocking()
    {
        // A hostile is in the room but auto-attack is off, so nothing will fight or
        // clear it. Deferring the search here would hold the Search gate forever and
        // deadlock the walker (report -074607). The manager must search instead and
        // release the gate so pathing continues.
        var coord = new MovementCoordinator();
        var mgr = new AutoSearchManager(
            isEnabled: () => true,
            hasEngageableHostiles: () => true,
            isCombatEngaging: () => false,   // auto-combat off — no fight incoming
            hasGetEngineArmed: () => true,
            coordinator: coord);
        mgr.SetWireSender(_ => { });

        mgr.OnRoomChanged(Key());     // arm + assert the Search gate
        mgr.OnRoomObserved();         // hostile seen but not engaging → must NOT defer
        Assert.False(mgr.LastSentForTests.Count > 1);
        mgr.OnClassifyElapsed();      // classify fires the search
        Assert.Single(mgr.LastSentForTests);
        Assert.Equal("sea", Decode(mgr.LastSentForTests[0]));

        mgr.OnSettleElapsed();        // settle → release; walker proceeds
        Assert.False(SearchHeld(coord));
    }

    [Fact]
    public void Fight_DoesNotSearchMidCombat_OnRepeatObservations()
    {
        var coord = new MovementCoordinator();
        var mgr = new AutoSearchManager(
            isEnabled: () => true,
            hasEngageableHostiles: () => true,
            coordinator: coord);
        mgr.SetWireSender(_ => { });

        mgr.OnRoomChanged(Key());
        mgr.OnRoomObserved();
        mgr.OnRoomObserved();
        mgr.OnRoomObserved();

        Assert.Empty(mgr.LastSentForTests);
        Assert.True(SearchHeld(coord));
    }

    [Fact]
    public void EmptyObservation_BeforeOccupants_HoldsButDoesNotSearch()
    {
        // Room entry holds the Search gate so a zero-dwell loop can't step out
        // before the classify-delayed `sea` fires (report stock-20260730-163244).
        // The empty room-entry observation (no hostiles, precedes the occupant
        // line) must NOT fire the search — the classify timer covers a genuinely
        // clear room, and a fight arriving a beat later still defers under the
        // same hold.
        var coord = new MovementCoordinator();
        bool hostiles = false;
        var mgr = new AutoSearchManager(
            isEnabled: () => true,
            hasEngageableHostiles: () => hostiles,
            coordinator: coord);
        mgr.SetWireSender(_ => { });

        mgr.OnRoomChanged(Key());
        Assert.True(SearchHeld(coord));   // held from entry to search in place
        mgr.OnRoomObserved();             // empty wipe → nothing sent, still held
        Assert.Empty(mgr.LastSentForTests);
        Assert.True(SearchHeld(coord));

        hostiles = true;
        mgr.OnRoomObserved();             // occupants revealed → defer, still held
        Assert.True(SearchHeld(coord));
        Assert.Empty(mgr.LastSentForTests);
    }

    [Fact]
    public void SearchesOncePerRoom_NoResearchWhenFightArrivesAfterClearSearch()
    {
        var coord = new MovementCoordinator();
        bool hostiles = false;
        var mgr = new AutoSearchManager(
            isEnabled: () => true,
            hasEngageableHostiles: () => hostiles,
            coordinator: coord);
        mgr.SetWireSender(_ => { });

        mgr.OnRoomChanged(Key());
        mgr.OnClassifyElapsed();      // clear room → one sea, gate held for settle
        Assert.Single(mgr.LastSentForTests);

        // A monster wanders in after the clear-room search: must not re-arm or
        // fire a second search for this room.
        hostiles = true;
        mgr.OnRoomObserved();
        Assert.Single(mgr.LastSentForTests);

        hostiles = false;
        mgr.OnRoomObserved();         // that fight clears — still no second search
        Assert.Single(mgr.LastSentForTests);

        mgr.OnSettleElapsed();        // settle window ends → gate released
        Assert.False(SearchHeld(coord));
    }

    [Fact]
    public void RoomChange_DiscardsHeldSearch_ReleasesGate()
    {
        var coord = new MovementCoordinator();
        bool enabled = true;
        var mgr = new AutoSearchManager(
            isEnabled: () => enabled,
            hasEngageableHostiles: () => true,
            coordinator: coord);
        mgr.SetWireSender(_ => { });

        mgr.OnRoomChanged(Key());
        mgr.OnRoomObserved();         // deferred + held
        Assert.True(SearchHeld(coord));

        // Search disarmed for the next room so the release isn't masked by the
        // fresh room-entry hold; the old room's held search must still be dropped.
        enabled = false;
        mgr.OnRoomChanged(Key());          // moved on → discard + release, nothing new to hold
        Assert.False(SearchHeld(coord));
    }

    [Fact]
    public void Dispose_ReleasesGate()
    {
        var coord = new MovementCoordinator();
        var mgr = new AutoSearchManager(
            isEnabled: () => true,
            hasEngageableHostiles: () => true,
            coordinator: coord);
        mgr.SetWireSender(_ => { });
        mgr.OnRoomChanged(Key());
        mgr.OnRoomObserved();
        Assert.True(SearchHeld(coord));

        mgr.Dispose();
        Assert.False(SearchHeld(coord));
    }

    // ----- reliability: queued-move skip + death reset (report -090736) ----------

    [Fact]
    public void QueuedMoves_SkipTransitRoomSearch_FiresOnceSettled()
    {
        // A manual n;e;n burst: transit rooms are already passed, so a `sea` would land
        // in whichever room the player stops in. While moves are queued the transit
        // search is skipped; once settled (queue empty) the current room searches once.
        bool queued = true;
        var coord = new MovementCoordinator();
        var mgr = new AutoSearchManager(
            isEnabled: () => true,
            hasQueuedMoves: () => queued,
            coordinator: coord);
        mgr.SetWireSender(_ => { });

        mgr.OnRoomChanged(Key(1));       // transit room
        mgr.OnClassifyElapsed();         // moves still queued → skipped, gate released
        Assert.Empty(mgr.LastSentForTests);
        Assert.False(SearchHeld(coord));

        queued = false;                  // settled in the landing room
        mgr.OnRoomChanged(Key(2));
        mgr.OnClassifyElapsed();
        Assert.Single(mgr.LastSentForTests);   // one search, in the room we stopped in
    }

    [Fact]
    public void NullRoom_OnDeath_ClearsDeferredSearch_NoLateFire()
    {
        // Death → respawn-pending sends a null-room change; a search deferred in the
        // room we died in must be cleared so the death-driven roster wipe can't fire a
        // `sea` for a room we've already left (report paradigm-20260820-090736 Face B).
        bool hostiles = true;
        var coord = new MovementCoordinator();
        var mgr = new AutoSearchManager(
            isEnabled: () => true,
            hasEngageableHostiles: () => hostiles,
            coordinator: coord);
        mgr.SetWireSender(_ => { });

        mgr.OnRoomChanged(Key(934));     // died-in room, fight present
        mgr.OnRoomObserved();            // deferred + held
        Assert.True(SearchHeld(coord));

        mgr.OnRoomChanged(null);         // death → respawn-pending → clear owed, release
        Assert.False(SearchHeld(coord));

        hostiles = false;                // the death-driven wipe (now no hostiles)…
        mgr.OnRoomObserved();
        Assert.Empty(mgr.LastSentForTests);   // …must NOT fire a stale search
    }
}

using System.Collections.Generic;
using System.Text;
using FujinTerm.Game.Map;
using Xunit;

namespace FujinTerm.Tests;

// AutoSearchManager — a `search` won't run mid-combat, so the engine defers the
// `sea` past a fight and holds the walker via the Search gate until the room
// clears. The DispatcherTimer ticks (classify delay before a clear-room search,
// settle after a post-fight search) are Avalonia plumbing not exercised in
// headless xUnit; the internal OnClassifyElapsed / OnSettleElapsed stand in for
// those ticks so the state machine runs deterministically.
public sealed class AutoSearchManagerTests
{
    private static string Decode(byte[] b) => Encoding.Latin1.GetString(b).TrimEnd('\r');

    private static bool SearchHeld(MovementCoordinator c) =>
        c.AssertedGates.Contains(MovementCoordinator.SearchGate);

    // ----- clear room (no fight): searches after the classify delay -----

    [Fact]
    public void ClearRoom_Enabled_SearchesAfterClassify()
    {
        var mgr = new AutoSearchManager(isEnabled: () => true);
        mgr.SetWireSender(_ => { });

        mgr.OnRoomChanged();                 // arm — nothing sent yet
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

        mgr.OnRoomChanged();
        mgr.OnClassifyElapsed();

        Assert.Empty(mgr.LastSentForTests);
    }

    [Fact]
    public void ClearRoom_OncePerRoom()
    {
        var mgr = new AutoSearchManager(isEnabled: () => true);
        mgr.SetWireSender(_ => { });

        for (int i = 0; i < 3; i++) { mgr.OnRoomChanged(); mgr.OnClassifyElapsed(); }

        Assert.Equal(3, mgr.LastSentForTests.Count);
        Assert.All(mgr.LastSentForTests, b => Assert.Equal("sea", Decode(b)));
    }

    [Fact]
    public void ClassifyElapsed_ReadsMasterGateLive()
    {
        bool enabled = false;
        var mgr = new AutoSearchManager(isEnabled: () => enabled);
        mgr.SetWireSender(_ => { });

        mgr.OnRoomChanged(); mgr.OnClassifyElapsed();   // off — no send
        enabled = true;
        mgr.OnRoomChanged(); mgr.OnClassifyElapsed();   // on — one send

        Assert.Single(mgr.LastSentForTests);
    }

    [Fact]
    public void Send_ReachesBoundSink()
    {
        var sink = new List<byte[]>();
        var mgr = new AutoSearchManager(isEnabled: () => true);
        mgr.SetWireSender(sink.Add);

        mgr.OnRoomChanged(); mgr.OnClassifyElapsed();

        Assert.Single(sink);
        Assert.Equal("sea", Decode(sink[0]));
    }

    [Fact]
    public void DemandGate_ArmsSearch_WhenMasterOff()
    {
        var mgr = new AutoSearchManager(isEnabled: () => false, isDemandActive: () => true);
        mgr.SetWireSender(_ => { });

        mgr.OnRoomChanged(); mgr.OnClassifyElapsed();

        Assert.Single(mgr.LastSentForTests);
        Assert.Equal("sea", Decode(mgr.LastSentForTests[0]));
    }

    [Fact]
    public void BothGatesOff_SendsNothing()
    {
        var mgr = new AutoSearchManager(isEnabled: () => false, isDemandActive: () => false);
        mgr.SetWireSender(_ => { });

        mgr.OnRoomChanged(); mgr.OnClassifyElapsed();

        Assert.Empty(mgr.LastSentForTests);
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
            coordinator: coord);
        mgr.SetWireSender(_ => { });

        mgr.OnRoomChanged();          // arm
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
    public void Fight_DoesNotSearchMidCombat_OnRepeatObservations()
    {
        var coord = new MovementCoordinator();
        var mgr = new AutoSearchManager(
            isEnabled: () => true,
            hasEngageableHostiles: () => true,
            coordinator: coord);
        mgr.SetWireSender(_ => { });

        mgr.OnRoomChanged();
        mgr.OnRoomObserved();
        mgr.OnRoomObserved();
        mgr.OnRoomObserved();

        Assert.Empty(mgr.LastSentForTests);
        Assert.True(SearchHeld(coord));
    }

    [Fact]
    public void EmptyObservation_BeforeOccupants_DoesNotSearchOrHold()
    {
        // The empty room-entry observation (no hostiles, not yet holding) precedes
        // the occupant line; it must NOT fire the search — the classify timer
        // covers a genuinely clear room, and a fight arriving a beat later must
        // still defer.
        var coord = new MovementCoordinator();
        bool hostiles = false;
        var mgr = new AutoSearchManager(
            isEnabled: () => true,
            hasEngageableHostiles: () => hostiles,
            coordinator: coord);
        mgr.SetWireSender(_ => { });

        mgr.OnRoomChanged();
        mgr.OnRoomObserved();         // empty wipe → nothing
        Assert.Empty(mgr.LastSentForTests);
        Assert.False(SearchHeld(coord));

        hostiles = true;
        mgr.OnRoomObserved();         // occupants revealed → defer + hold
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

        mgr.OnRoomChanged();
        mgr.OnClassifyElapsed();      // clear room → one sea
        Assert.Single(mgr.LastSentForTests);

        // A monster wanders in after the clear-room search: must not re-arm/hold
        // or fire a second search for this room.
        hostiles = true;
        mgr.OnRoomObserved();
        Assert.False(SearchHeld(coord));
        Assert.Single(mgr.LastSentForTests);

        hostiles = false;
        mgr.OnRoomObserved();         // that fight clears — still no second search
        Assert.Single(mgr.LastSentForTests);
    }

    [Fact]
    public void RoomChange_DiscardsHeldSearch_ReleasesGate()
    {
        var coord = new MovementCoordinator();
        var mgr = new AutoSearchManager(
            isEnabled: () => true,
            hasEngageableHostiles: () => true,
            coordinator: coord);
        mgr.SetWireSender(_ => { });

        mgr.OnRoomChanged();
        mgr.OnRoomObserved();         // deferred + held
        Assert.True(SearchHeld(coord));

        mgr.OnRoomChanged();          // moved on → discard + release
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
        mgr.OnRoomChanged();
        mgr.OnRoomObserved();
        Assert.True(SearchHeld(coord));

        mgr.Dispose();
        Assert.False(SearchHeld(coord));
    }
}

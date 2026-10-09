using System.Text;
using System.Text.Json;
using MudPlay.Game.Health;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A board can drop some of a character's items on the floor for a hang-up in a
// fight. On a realm whose settings say so, the client compares what was held
// before the link dropped with the login's inventory read and picks up what is
// short and lying in the room. Nothing here decides a hang-up.
public sealed class HangupItemRecheckTests
{
    private static InventorySnapshot Snap(
        (string Name, string Slot)[]? worn = null, string[]? carried = null, string[]? keys = null,
        ReadiedLight? light = null) =>
        new(CurrencyHoldings.Empty, EncumbranceReading.Empty,
            (worn ?? []).Select(w => new EquippedItem(w.Name, w.Slot)).ToList(),
            carried ?? [], DateTimeOffset.Now, light, keys);

    // ----- The realm's settings ----------------------------------------

    [Theory]
    [InlineData(false, 3, false, 0, false)]   // master switch off
    [InlineData(true, 0, false, 0, false)]    // HP only, as most boards
    [InlineData(true, 2, false, 0, true)]     // PvP side drops items
    [InlineData(true, 0, false, 4, false)]    // PvE count set but that side isn't ticked
    [InlineData(true, 0, true, 4, true)]      // PvE side drops items
    [InlineData(true, -5, true, -1, false)]   // a hand-edited file reads through the limits
    public void TheRealmDropsItems_OnlyWhenASideThatAppliesHasACount(
        bool enabled, int pvpItems, bool pve, int pveItems, bool expected)
    {
        RealmProfile realm = new()
        {
            HangupPenaltyEnabled = enabled,
            HangupPvpItemsDropped = pvpItems,
            HangupPvePenaltyEnabled = pve,
            HangupPveItemsDropped = pveItems,
        };

        Assert.Equal(expected, HangupPenaltyNotice.DropsItems(realm));
        Assert.False(HangupPenaltyNotice.DropsItems(null));
    }

    // ----- What is held, and what is short ------------------------------

    [Fact]
    public void Held_CountsByName_OverWornCarriedLitAndKeyRing()
    {
        List<HeldItem> held = HangupItemPlan.Held(Snap(
            worn: [("gold ring", "Finger"), ("gold ring", "Finger"), ("dagger", "Weapon Hand")],
            carried: ["2 dagger", "3 torch", "rope"],
            keys: ["2 black star key", "brass key"],
            light: new ReadiedLight("torch", 40)));

        Assert.Equal(2, held.Single(h => h.Name == "gold ring").Count);
        Assert.Equal(["Finger", "Finger"], held.Single(h => h.Name == "gold ring").WornSlots);
        Assert.Equal(3, held.Single(h => h.Name == "dagger").Count);
        Assert.Equal(["Weapon Hand"], held.Single(h => h.Name == "dagger").WornSlots);
        Assert.Equal(4, held.Single(h => h.Name == "torch").Count);
        Assert.Null(held.Single(h => h.Name == "rope").WornSlots);
        Assert.Equal(2, held.Single(h => h.Name == "black star key").Count);
        Assert.Equal(1, held.Single(h => h.Name == "brass key").Count);
    }

    [Fact]
    public void Missing_IsByNameAndCount()
    {
        List<HeldItem> before = HangupItemPlan.Held(Snap(
            worn: [("chainmail hauberk", "Torso")], carried: ["3 torch", "rope"], keys: ["2 black star key"]));

        List<HangupMissingItem> missing = HangupItemPlan.Missing(before, Snap(
            carried: ["torch", "rope"], keys: ["black star key"]));

        Assert.Equal(3, missing.Count);
        HangupMissingItem hauberk = missing.Single(m => m.Name == "chainmail hauberk");
        Assert.Equal(1, hauberk.Count);
        Assert.Equal(["Torso"], hauberk.WornSlots);
        Assert.Equal(2, missing.Single(m => m.Name == "torch").Count);
        Assert.Empty(missing.Single(m => m.Name == "torch").WornSlots);
        Assert.Equal(1, missing.Single(m => m.Name == "black star key").Count);
    }

    [Fact]
    public void Missing_NothingWhenEverythingIsStillHeld_HoweverItIsHeld()
    {
        List<HeldItem> before = HangupItemPlan.Held(Snap(
            worn: [("dagger", "Weapon Hand")], carried: ["2 torch"], keys: ["brass key"]));

        // The weapon is in the pack now, a torch is lit, and something new was gained.
        Assert.Empty(HangupItemPlan.Missing(before, Snap(
            carried: ["dagger", "torch", "waterskin"], keys: ["brass key"], light: new ReadiedLight("torch", 80))));
    }

    // Two of one item worn: the one still worn keeps its slot, the other is the
    // missing worn copy.
    [Fact]
    public void Missing_Duplicates_OnlyTheShortCopiesAndTheirSlots()
    {
        List<HeldItem> before = HangupItemPlan.Held(Snap(
            worn: [("gold ring", "Finger"), ("gold ring", "Finger"), ("dagger", "Weapon Hand")],
            carried: ["2 dagger"]));

        List<HangupMissingItem> missing = HangupItemPlan.Missing(before, Snap(
            worn: [("gold ring", "Finger"), ("dagger", "Weapon Hand")], carried: ["dagger"]));

        HangupMissingItem ring = missing.Single(m => m.Name == "gold ring");
        Assert.Equal(1, ring.Count);
        Assert.Equal(["Finger"], ring.WornSlots);
        // A dagger is short, but the one in hand is still in hand.
        HangupMissingItem dagger = missing.Single(m => m.Name == "dagger");
        Assert.Equal(1, dagger.Count);
        Assert.Empty(dagger.WornSlots);
    }

    // ----- The pickup plan ----------------------------------------------

    [Fact]
    public void Pickup_OnlyMissingNames_OnlyUpToTheMissingCount()
    {
        List<HangupMissingItem> missing =
        [
            new("torch", 2, []),
            new("chainmail hauberk", 1, ["Torso"]),
            new("rope", 1, []),
        ];

        // Five torches lie here, a hauberk, and things that were never ours.
        List<(string Name, int Count)> plan = HangupItemPlan.Pickup(
            missing, ["5 torch", "a chainmail hauberk", "rusty dagger", "2 black star key"]);

        Assert.Equal([("torch", 2), ("chainmail hauberk", 1)], plan);
    }

    [Fact]
    public void Pickup_TakesNoMoreThanLiesThere_AndNothingFromAnEmptyFloor()
    {
        List<HangupMissingItem> missing = [new("torch", 3, [])];

        Assert.Equal([("torch", 1)], HangupItemPlan.Pickup(missing, ["torch"]));
        Assert.Empty(HangupItemPlan.Pickup(missing, []));
    }

    [Fact]
    public void Outstanding_DropsWhatCameBack_WornCopiesFirst()
    {
        List<HangupMissingItem> missing = [new("gold ring", 2, ["Finger"]), new("torch", 1, [])];

        List<HangupMissingItem> left = HangupItemPlan.Outstanding(
            missing, new Dictionary<string, int> { ["gold ring"] = 1, ["torch"] = 1 });

        HangupMissingItem ring = Assert.Single(left);
        Assert.Equal(1, ring.Count);
        Assert.Empty(ring.WornSlots);
    }

    // ----- The list on disk ---------------------------------------------

    [Fact]
    public void HeldList_RoundTripsThroughTheProfileFile()
    {
        CharacterProfile profile = new()
        {
            HeldAtDisconnect = new HeldAtDisconnect
            {
                At = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero),
                Room = new RoomRef(1, 3),
                Items = [new HeldItem("gold ring", 2, ["Finger", "Finger"]), new HeldItem("torch", 3)],
            },
        };

        CharacterProfile back = JsonSerializer.Deserialize<CharacterProfile>(
            JsonSerializer.Serialize(profile, JsonStore.Options), JsonStore.Options)!;

        HeldAtDisconnect held = back.HeldAtDisconnect!;
        Assert.Equal(profile.HeldAtDisconnect.At, held.At);
        Assert.Equal(3, held.Room!.Room);
        Assert.Equal(2, held.Items.Count);
        Assert.Equal(["Finger", "Finger"], held.Items[0].WornSlots);
        Assert.Equal(3, held.Items[1].Count);
        Assert.Null(held.Items[1].WornSlots);
    }

    // The list is a new optional field: a profile from before has none, and the
    // schema upgrade neither needs a step for it nor invents one.
    [Fact]
    public void AnOlderProfile_LoadsWithNoList_AndMigratesWithoutOne()
    {
        CharacterProfile old = JsonSerializer.Deserialize<CharacterProfile>(
            """{ "SchemaVersion": 8, "Name": "Ermias" }""", JsonStore.Options)!;

        Assert.Null(old.HeldAtDisconnect);
        Assert.True(ProfileMigrations.Apply(old));
        Assert.Equal(CharacterProfile.CurrentSchemaVersion, old.SchemaVersion);
        Assert.Null(old.HeldAtDisconnect);
    }

    // ----- The check ----------------------------------------------------

    private sealed class Harness
    {
        public MovementCoordinator Coordinator { get; } = new();
        public CharacterProfile Profile { get; } = new();
        public bool RealmDrops { get; set; } = true;
        public InventorySnapshot Inventory { get; set; } = InventorySnapshot.Empty;
        public RoomKey? Room { get; set; } = new RoomKey(1, 3);
        public List<string> Floor { get; } = new();
        public bool Fighting { get; set; }
        public bool Hostile { get; set; }
        public bool AutoAll { get; set; } = true;
        public List<(string Name, int Count)> Collected { get; } = new();
        public List<DeathItem> Reworn { get; } = new();
        public HangupItemRecheck Check { get; }

        public bool Held => Coordinator.IsGateAsserted(MovementCoordinator.HangupItemCheckGate);
        public List<string> Sent => Check.LastSentForTests.Select(b => Encoding.Latin1.GetString(b)).ToList();

        public Harness()
        {
            Check = new HangupItemRecheck(
                Coordinator,
                profile: () => Profile,
                // A profile save runs the saving hook, as ProfileService does.
                saveProfile: () => Check!.StampForSave(Profile),
                realmDropsItems: () => RealmDrops,
                inventory: () => Inventory,
                confirmedRoom: () => Room,
                floor: () => Floor,
                fighting: () => Fighting,
                hostilePresent: () => Hostile || Fighting,
                isAutoEnabled: () => AutoAll,
                roomRedisplayFree: () => true,
                collect: (name, count) => Collected.Add((name, count)),
                rewear: worn => Reworn.AddRange(worn));
            Check.SetWireSender(_ => { });
        }

        // The list a save left on disk before the link dropped, in room 1/3.
        public void Stored(InventorySnapshot before, RoomRef? room = null) =>
            Profile.HeldAtDisconnect = new HeldAtDisconnect
            {
                At = DateTimeOffset.Now.AddMinutes(-2),
                Room = room ?? new RoomRef(1, 3),
                Items = HangupItemPlan.Held(before),
            };

        // Connect, and the login's room display with this floor, then the prompt.
        public void ConnectAndEnter(params string[] floor)
        {
            Check.NoteConnected();
            ShowRoom(floor);
            Check.OnInGameChanged(true);
        }

        public void ShowRoom(params string[] floor)
        {
            Floor.Clear();
            Floor.AddRange(floor);
            if (floor.Length > 0) Check.NoteFloorSurveyed();
            Check.NoteRoomDisplayed();
        }

        public void ReadInventory(InventorySnapshot now)
        {
            Inventory = now;
            Check.OnInventoryRead();
        }

        public void Heartbeats(int count)
        {
            for (int i = 0; i < count; i++) Check.OnHeartbeat();
        }
    }

    [Fact]
    public void ARealmThatDropsNoItems_IsLeftAlone()
    {
        Harness h = new() { RealmDrops = false };
        h.Stored(Snap(carried: ["2 torch"]));

        h.ConnectAndEnter("2 torch");
        h.ReadInventory(Snap());

        Assert.False(h.Held);
        Assert.Empty(h.Collected);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void NoStoredList_NothingToCheck()
    {
        Harness h = new();

        h.ConnectAndEnter("2 torch");
        h.ReadInventory(Snap());

        Assert.False(h.Held);
        Assert.Empty(h.Collected);
    }

    // The hold goes up at the connect, ahead of the first game prompt a loop
    // restarts on, and comes down when the inventory read shows nothing short.
    [Fact]
    public void MovementIsHeldFromTheConnect_UntilTheInventoryShowsNothingMissing()
    {
        Harness h = new();
        h.Stored(Snap(worn: [("dagger", "Weapon Hand")], carried: ["2 torch"]));

        h.Check.NoteConnected();
        Assert.True(h.Held);
        h.ShowRoom();
        h.Check.OnInGameChanged(true);
        Assert.True(h.Held);

        h.ReadInventory(Snap(worn: [("dagger", "Weapon Hand")], carried: ["2 torch"]));

        Assert.False(h.Held);
        Assert.Empty(h.Collected);
        Assert.Empty(h.Sent);
        Assert.Contains("nothing held before is missing", h.Check.LastOutcome);
    }

    [Fact]
    public void MissingItemsOnTheFloor_ArePickedUp_AndTheWornOnesGoBackOn()
    {
        Harness h = new();
        h.Stored(Snap(worn: [("chainmail hauberk", "Torso")], carried: ["3 torch", "rope"]));

        h.ConnectAndEnter("2 torch", "chainmail hauberk", "a rusty dagger");
        h.ReadInventory(Snap(carried: ["torch"]));

        // Only what is ours and lying here: not the dagger, and no get for the rope.
        Assert.Equal([("chainmail hauberk", 1), ("torch", 2)], h.Collected);
        Assert.True(h.Held);

        h.Check.OnItemTaken("chainmail hauberk", 1);
        h.Check.OnItemTaken("torch", 2);

        Assert.False(h.Held);
        DeathItem worn = Assert.Single(h.Reworn);
        Assert.Equal("chainmail hauberk", worn.Name);
        Assert.Equal("Torso", worn.Slot);
        HangupMissingItem still = Assert.Single(h.Check.LastStillMissing);
        Assert.Equal("rope", still.Name);
        Assert.Equal(3, h.Check.LastMissing.Count);
    }

    // Stock answers one `You took` a copy; the count is reached over several.
    [Fact]
    public void GetsAnsweredOneCopyAtATime_FinishWhenTheLastLands()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["3 torch"]));
        h.ConnectAndEnter("3 torch");
        h.ReadInventory(Snap());

        h.Check.OnItemTaken("torch", 1);
        h.Check.OnItemTaken("torch", 1);
        Assert.True(h.Held);
        h.Check.OnItemTaken("torch", 1);

        Assert.False(h.Held);
        Assert.Empty(h.Check.LastStillMissing);
    }

    // A get the game refuses prints no `You took`: movement isn't held on it.
    [Fact]
    public void GetsThatAreNeverAnswered_EndTheCheck_WithTheItemStillMissing()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["torch"]));
        h.ConnectAndEnter("torch");
        h.ReadInventory(Snap());
        Assert.True(h.Held);

        h.Heartbeats(3);

        Assert.False(h.Held);
        Assert.Single(h.Check.LastStillMissing);
        Assert.Empty(h.Reworn);
    }

    [Fact]
    public void ComingInSomewhereElse_PicksNothingUp()
    {
        Harness h = new() { Room = new RoomKey(1, 99) };
        h.Stored(Snap(carried: ["2 torch"]), new RoomRef(1, 3));

        h.ConnectAndEnter("2 torch");
        h.ReadInventory(Snap());

        Assert.Empty(h.Collected);
        Assert.False(h.Held);
        Assert.Contains("1/99", h.Check.LastOutcome);
    }

    // The login's display moved the map to another room: known to be elsewhere
    // without drawing the room a second time.
    [Fact]
    public void ComingInSomewhereElse_AsksForNoRedisplay()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["torch"]));
        h.Check.NoteConnected();
        h.ShowRoom("torch");
        h.Room = new RoomKey(1, 99);
        h.Check.OnRoomChanged();
        h.Check.OnInGameChanged(true);

        h.ReadInventory(Snap());

        Assert.Empty(h.Sent);
        Assert.Empty(h.Collected);
        Assert.False(h.Held);
    }

    // The room the character left wasn't known, and the map isn't sure where it
    // is now: the floor of the room it is in is checked all the same.
    [Fact]
    public void RoomNotKnown_ChecksTheFloorOfTheRoomItIsIn()
    {
        Harness h = new() { Room = null };
        h.Stored(Snap(carried: ["torch"]));
        h.Profile.HeldAtDisconnect!.Room = null;

        h.ConnectAndEnter("torch");
        h.ReadInventory(Snap());

        Assert.Equal([("torch", 1)], h.Collected);
    }

    // The floor list the tracker holds is from before the link dropped when the
    // room printed none this time: an empty floor prints no list.
    [Fact]
    public void AFloorListFromBeforeTheConnect_IsNotTheFloorNow()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["2 torch"]));
        h.Floor.Add("2 torch");

        h.Check.NoteConnected();
        h.Check.NoteRoomDisplayed();
        h.Check.OnInGameChanged(true);
        h.ReadInventory(Snap());

        Assert.Empty(h.Collected);
        Assert.Contains("none of the missing items is on the floor", h.Check.LastOutcome);
    }

    [Fact]
    public void AFightInTheRoom_ThePickupWaitsForItToEnd()
    {
        Harness h = new() { Fighting = true };
        h.Stored(Snap(carried: ["2 torch"]));

        h.ConnectAndEnter("2 torch");
        h.ReadInventory(Snap());
        h.Heartbeats(5);

        Assert.Empty(h.Collected);
        Assert.True(h.Held);

        h.Fighting = false;
        h.Check.OnRoomObserved();

        Assert.Equal([("torch", 2)], h.Collected);
    }

    [Fact]
    public void LeavingTheRoomMidFight_EndsTheCheck()
    {
        Harness h = new() { Fighting = true };
        h.Stored(Snap(carried: ["torch"]));
        h.ConnectAndEnter("torch");
        h.ReadInventory(Snap());

        h.Check.OnRoomChanged();
        h.Fighting = false;
        h.Check.OnRoomObserved();

        Assert.Empty(h.Collected);
        Assert.False(h.Held);
    }

    // A monster the client isn't set to fight won't go away by itself: nothing is
    // picked up under it, and nothing waits on it.
    [Fact]
    public void AHostileNotBeingFought_LeavesTheItemsOnTheFloor()
    {
        Harness h = new() { Hostile = true };
        h.Stored(Snap(carried: ["torch"]));

        h.ConnectAndEnter("torch");
        h.ReadInventory(Snap());

        Assert.Empty(h.Collected);
        Assert.False(h.Held);
        Assert.Contains("hostile", h.Check.LastOutcome);
    }

    [Fact]
    public void NoRoomDisplayRead_AsksForOneRedisplay_ThenPicksUp()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["torch"]));
        h.Check.NoteConnected();
        h.Check.OnInGameChanged(true);

        h.ReadInventory(Snap());

        Assert.Equal(["\r"], h.Sent);
        Assert.Empty(h.Collected);

        h.ShowRoom("torch");

        Assert.Equal([("torch", 1)], h.Collected);
        Assert.Equal(["\r"], h.Sent);
    }

    [Fact]
    public void ARedisplayThatNeverComes_DoesNotHoldMovement()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["torch"]));
        h.Check.NoteConnected();
        h.Check.OnInGameChanged(true);
        h.ReadInventory(Snap());

        h.Heartbeats(2);

        Assert.False(h.Held);
        Assert.Empty(h.Collected);
    }

    // An entry made by hand sends no `i`: one is asked for, once, and a check that
    // still gets no read gives the hold up and keeps the list for the next entry.
    [Fact]
    public void NoInventoryRead_OneIsAskedFor_ThenTheCheckGivesUp_KeepingTheList()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["torch"]));
        HeldAtDisconnect stored = h.Profile.HeldAtDisconnect!;
        h.ConnectAndEnter("torch");

        h.Heartbeats(3);
        Assert.Empty(h.Sent);
        h.Heartbeats(1);
        Assert.Equal(["i\r"], h.Sent);
        Assert.True(h.Held);

        h.Heartbeats(4);

        Assert.Equal(["i\r"], h.Sent);
        Assert.False(h.Held);
        Assert.Same(stored, h.Profile.HeldAtDisconnect);
        Assert.Contains("not finished", h.Check.LastOutcome);
    }

    [Fact]
    public void AutoAllOff_NothingIsSent()
    {
        Harness h = new() { AutoAll = false };
        h.Stored(Snap(carried: ["torch"]));

        h.ConnectAndEnter("torch");
        h.Heartbeats(4);
        Assert.Empty(h.Sent);
        Assert.False(h.Held);

        // And with the read in hand, the missing item is reported and left.
        Harness read = new() { AutoAll = false };
        read.Stored(Snap(carried: ["torch"]));
        read.ConnectAndEnter("torch");
        read.ReadInventory(Snap());

        Assert.Empty(read.Collected);
        Assert.Empty(read.Sent);
        Assert.False(read.Held);
        Assert.Single(read.Check.LastMissing);
    }

    [Fact]
    public void OnePassAConnection_ALaterInventoryReadStartsNothing()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["2 torch"]));
        h.ConnectAndEnter();
        h.ReadInventory(Snap());
        Assert.False(h.Held);

        h.ShowRoom("2 torch");
        h.ReadInventory(Snap());

        Assert.Empty(h.Collected);
        Assert.False(h.Held);
    }

    [Fact]
    public void ALinkThatDropsBeforeTheGame_GivesTheHoldUp_AndKeepsTheList()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["torch"]));
        HeldAtDisconnect stored = h.Profile.HeldAtDisconnect!;

        h.Check.NoteConnected();
        Assert.True(h.Held);
        h.Check.NoteDisconnected();

        Assert.False(h.Held);
        Assert.Same(stored, h.Profile.HeldAtDisconnect);
    }

    // ----- When the list is written -------------------------------------

    [Fact]
    public void TheList_IsWrittenOnlyInTheGame_WithAnInventoryReadOnThisConnection()
    {
        Harness h = new();
        h.Inventory = Snap(carried: ["2 torch"]);

        // At the board's login: the record in memory is the last session's.
        h.Check.NoteConnected();
        h.Check.StampForSave(h.Profile);
        Assert.Null(h.Profile.HeldAtDisconnect);

        // In the game, before the login's `i`: still nothing new known.
        h.Check.OnInGameChanged(true);
        h.Check.StampForSave(h.Profile);
        Assert.Null(h.Profile.HeldAtDisconnect);

        h.ReadInventory(Snap(worn: [("dagger", "Weapon Hand")], carried: ["2 torch"], keys: ["brass key"]));
        h.Check.StampForSave(h.Profile);

        HeldAtDisconnect held = h.Profile.HeldAtDisconnect!;
        Assert.Equal(3, held.Room!.Room);
        Assert.Equal(3, held.Items.Count);
        Assert.Equal(2, held.Items.Single(i => i.Name == "torch").Count);
    }

    // Leaving the game saves, so the list is as of the moment the link dropped.
    [Fact]
    public void LeavingTheGame_SavesWhatIsHeldThen()
    {
        Harness h = new();
        h.Check.NoteConnected();
        h.Check.OnInGameChanged(true);
        h.ReadInventory(Snap(carried: ["torch"]));

        h.Inventory = Snap(carried: ["torch", "rope"]);
        h.Check.OnInGameChanged(false);

        Assert.Equal(2, h.Profile.HeldAtDisconnect!.Items.Count);

        // Out of the game, a later save changes nothing.
        h.Inventory = Snap();
        h.Check.StampForSave(h.Profile);
        Assert.Equal(2, h.Profile.HeldAtDisconnect!.Items.Count);
    }

    // The stored list must outlast a second drop that comes before the login's
    // inventory read: the record in memory is no newer than the list.
    [Fact]
    public void ADropBeforeTheInventoryRead_KeepsTheListStillToBeCompared()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["3 torch"]));
        HeldAtDisconnect stored = h.Profile.HeldAtDisconnect!;
        h.Inventory = Snap(carried: ["torch"]);

        h.ConnectAndEnter("2 torch");
        h.Check.OnInGameChanged(false);
        h.Check.NoteDisconnected();

        Assert.Same(stored, h.Profile.HeldAtDisconnect);
        Assert.False(h.Held);
    }

    // Hanging up again mid-fight, before the first drop was picked up: the next
    // entry still knows about it.
    [Fact]
    public void ADropMidCheck_CarriesTheUnfoundItemsToTheNextEntry()
    {
        Harness h = new() { Fighting = true };
        h.Stored(Snap(worn: [("chainmail hauberk", "Torso")], carried: ["2 torch"]));
        h.ConnectAndEnter("chainmail hauberk", "2 torch");
        h.ReadInventory(Snap());

        h.Check.OnInGameChanged(false);
        h.Check.NoteDisconnected();

        HeldAtDisconnect carriedOver = h.Profile.HeldAtDisconnect!;
        Assert.Equal(2, carriedOver.Items.Single(i => i.Name == "torch").Count);
        Assert.Equal(["Torso"], carriedOver.Items.Single(i => i.Name == "chainmail hauberk").WornSlots);

        h.Fighting = false;
        h.ConnectAndEnter("chainmail hauberk", "2 torch");
        h.ReadInventory(Snap());

        Assert.Equal([("chainmail hauberk", 1), ("torch", 2)], h.Collected);
    }

    // What a finished check didn't find is not carried: it isn't looked for again.
    [Fact]
    public void AFinishedCheck_LeavesOnlyWhatIsHeldOnTheList()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["torch", "rope"]));
        h.ConnectAndEnter();

        h.ReadInventory(Snap(carried: ["rope"]));

        HeldItem only = Assert.Single(h.Profile.HeldAtDisconnect!.Items);
        Assert.Equal("rope", only.Name);
    }
}

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
    [InlineData(false, 3, false, 0, 0)]   // master switch off
    [InlineData(true, 0, false, 0, 0)]    // HP only, as most boards
    [InlineData(true, 2, false, 0, 2)]    // PvP side drops items
    [InlineData(true, 0, false, 4, 0)]    // PvE count set but that side isn't ticked
    [InlineData(true, 0, true, 4, 4)]     // PvE side drops items
    [InlineData(true, 3, true, 5, 5)]     // either side may have applied: the larger
    [InlineData(true, 6, true, 5, 6)]
    [InlineData(true, -5, true, -1, 0)]   // a hand-edited file reads through the limits
    public void TheMostARealmDrops_IsTheLargerCountOfTheSidesThatApply(
        bool enabled, int pvpItems, bool pve, int pveItems, int expected)
    {
        RealmProfile realm = new()
        {
            HangupPenaltyEnabled = enabled,
            HangupPvpItemsDropped = pvpItems,
            HangupPvePenaltyEnabled = pve,
            HangupPveItemsDropped = pveItems,
        };

        Assert.Equal(expected, HangupPenaltyNotice.MaxItemsDropped(realm));
        Assert.Equal(0, HangupPenaltyNotice.MaxItemsDropped(null));
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
        Assert.Equal(3, held.Single(h => h.Name == "dagger").Count);
        Assert.Equal(4, held.Single(h => h.Name == "torch").Count);
        Assert.Equal(1, held.Single(h => h.Name == "rope").Count);
        Assert.Equal(2, held.Single(h => h.Name == "black star key").Count);
        Assert.Equal(1, held.Single(h => h.Name == "brass key").Count);
    }

    [Fact]
    public void Missing_IsByNameAndCount()
    {
        List<HeldItem> before = HangupItemPlan.Held(Snap(
            worn: [("chainmail hauberk", "Torso")], carried: ["3 torch", "rope"], keys: ["2 black star key"]));

        List<(string Name, int Count)> missing = HangupItemPlan.Missing(before, Snap(
            carried: ["torch", "rope"], keys: ["black star key"]));

        Assert.Equal([("chainmail hauberk", 1), ("torch", 2), ("black star key", 1)], missing);
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

    [Fact]
    public void Missing_Duplicates_OnlyTheShortCopies()
    {
        List<HeldItem> before = HangupItemPlan.Held(Snap(
            worn: [("gold ring", "Finger"), ("gold ring", "Finger"), ("dagger", "Weapon Hand")],
            carried: ["2 dagger"]));

        List<(string Name, int Count)> missing = HangupItemPlan.Missing(before, Snap(
            worn: [("gold ring", "Finger"), ("dagger", "Weapon Hand")], carried: ["dagger"]));

        Assert.Equal([("gold ring", 1), ("dagger", 1)], missing);
    }

    // ----- The pickup plan ----------------------------------------------

    [Fact]
    public void Pickup_OnlyMissingNames_OnlyUpToTheMissingCount()
    {
        List<(string Name, int Count)> missing = [("torch", 2), ("chainmail hauberk", 1), ("rope", 1)];

        // Five torches lie here, a hauberk, and things that were never ours.
        List<(string Name, int Count)> plan = HangupItemPlan.Pickup(
            missing, ["5 torch", "a chainmail hauberk", "rusty dagger", "2 black star key"]);

        Assert.Equal([("torch", 2), ("chainmail hauberk", 1)], plan);
    }

    [Fact]
    public void Pickup_TakesNoMoreThanLiesThere_AndNothingFromAnEmptyFloor()
    {
        List<(string Name, int Count)> missing = [("torch", 3)];

        Assert.Equal([("torch", 1)], HangupItemPlan.Pickup(missing, ["torch"]));
        Assert.Empty(HangupItemPlan.Pickup(missing, []));
    }

    // Never more copies than the board can have dropped, in the plan's order.
    [Fact]
    public void Capped_CutsThePlanToTheMostTheBoardDrops()
    {
        List<(string Name, int Count)> plan = [("chainmail hauberk", 1), ("torch", 3), ("rope", 1)];

        Assert.Equal([("chainmail hauberk", 1), ("torch", 2)], HangupItemPlan.Capped(plan, 3));
        Assert.Equal(plan, HangupItemPlan.Capped(plan, 5));
        Assert.Equal(plan, HangupItemPlan.Capped(plan, 100));
        Assert.Empty(HangupItemPlan.Capped(plan, 0));
    }

    [Fact]
    public void Outstanding_DropsWhatCameBack()
    {
        List<(string Name, int Count)> left = HangupItemPlan.Outstanding(
            [("gold ring", 2), ("torch", 1)],
            new Dictionary<string, int> { ["gold ring"] = 1, ["torch"] = 1 });

        Assert.Equal([("gold ring", 1)], left);
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
                Realm = "Board/PvP",
                Room = new RoomRef(1, 3),
                PenaltiesSpanned = 2,
                Items = [new HeldItem("gold ring", 2), new HeldItem("torch", 3)],
            },
        };

        CharacterProfile back = JsonSerializer.Deserialize<CharacterProfile>(
            JsonSerializer.Serialize(profile, JsonStore.Options), JsonStore.Options)!;

        HeldAtDisconnect held = back.HeldAtDisconnect!;
        Assert.Equal(profile.HeldAtDisconnect.At, held.At);
        Assert.Equal("Board/PvP", held.Realm);
        Assert.Equal(3, held.Room!.Room);
        Assert.Equal(2, held.PenaltiesSpanned);
        Assert.Equal(2, held.Items.Count);
        Assert.Equal(3, held.Items[1].Count);
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
        public const string RealmKey = "Board/Main";

        public MovementCoordinator Coordinator { get; } = new();
        public CharacterProfile Profile { get; } = new();
        // False: the default profile is loaded, not a named character.
        public bool Named { get; set; } = true;
        public string? Realm { get; set; } = RealmKey;
        public int MaxItems { get; set; } = 10;
        public InventorySnapshot Inventory { get; set; } = InventorySnapshot.Empty;
        public RoomKey? Room { get; set; } = new RoomKey(1, 3);
        public List<string> Floor { get; } = new();
        public bool Fighting { get; set; }
        public bool Hostile { get; set; }
        public bool AutoAll { get; set; } = true;
        public List<(string Name, int Count)> Collected { get; } = new();
        // The names handed to the equipment manager each time gear came back, and
        // the set it says it applied.
        public List<string[]> GearAsked { get; } = new();
        public string? GearSetApplied { get; set; } = "Default";
        public List<string> Notices { get; } = new();
        public HangupItemRecheck Check { get; }

        public bool Held => Coordinator.IsGateAsserted(MovementCoordinator.HangupItemCheckGate);
        public List<string> Sent => Check.LastSentForTests.Select(b => Encoding.Latin1.GetString(b)).ToList();

        public Harness()
        {
            Check = new HangupItemRecheck(
                Coordinator,
                profile: () => Named ? Profile : null,
                // A profile save runs the saving hook, as ProfileService does.
                saveProfile: () => Check!.StampForSave(Profile),
                realmKey: () => Realm,
                maxItemsDropped: () => MaxItems,
                inventory: () => Inventory,
                confirmedRoom: () => Room,
                floor: () => Floor,
                fighting: () => Fighting,
                hostilePresent: () => Hostile || Fighting,
                isAutoEnabled: () => AutoAll,
                roomRedisplayFree: () => true,
                collect: (name, count) => Collected.Add((name, count)),
                reapplyGearSet: names => { GearAsked.Add(names.ToArray()); return GearSetApplied; },
                notice: Notices.Add);
            Check.SetWireSender(_ => { });
        }

        // The list a save left on disk before the link dropped, in room 1/3.
        public void Stored(InventorySnapshot before, RoomRef? room = null) =>
            Profile.HeldAtDisconnect = new HeldAtDisconnect
            {
                At = DateTimeOffset.Now.AddMinutes(-2),
                Realm = RealmKey,
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
        Harness h = new() { MaxItems = 0 };
        h.Stored(Snap(carried: ["2 torch"]));

        h.ConnectAndEnter("2 torch");
        h.ReadInventory(Snap());

        Assert.False(h.Held);
        Assert.Empty(h.Collected);
        Assert.Empty(h.Sent);
    }

    // No list, nothing said and nothing held: a dial with nothing to check
    // leaves no trace in the gate history.
    [Fact]
    public void NoStoredList_NothingToCheck_AndNoGateTouched()
    {
        Harness h = new();

        h.ConnectAndEnter("2 torch");
        h.ReadInventory(Snap());

        Assert.Empty(h.Collected);
        Assert.Empty(h.Coordinator.History);
        Assert.Equal("idle", h.Check.Status);
    }

    // A list taken on another board or realm says nothing about the pack here.
    [Fact]
    public void AListFromAnotherRealm_IsIgnored()
    {
        Harness h = new() { Realm = "Board/PvP" };
        h.Stored(Snap(carried: ["2 torch"]));

        h.ConnectAndEnter("2 torch");
        h.ReadInventory(Snap());

        Assert.Empty(h.Collected);
        Assert.Empty(h.Coordinator.History);
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
        Assert.Empty(h.Notices);
        Assert.Contains("nothing held before is missing", h.Check.LastOutcome);
    }

    // What comes back is handed to the equipment manager by name; which pieces go
    // on is its business.
    [Fact]
    public void MissingItemsOnTheFloor_ArePickedUp_AndTheEquipmentManagerIsAsked()
    {
        Harness h = new();
        h.Stored(Snap(worn: [("chainmail hauberk", "Torso")], carried: ["3 torch", "rope"]));

        h.ConnectAndEnter("2 torch", "chainmail hauberk", "a rusty dagger");
        h.ReadInventory(Snap(carried: ["torch"]));

        // Only what is ours and lying here: not the dagger, and no get for the rope.
        Assert.Equal([("chainmail hauberk", 1), ("torch", 2)], h.Collected);
        Assert.True(h.Held);
        Assert.Empty(h.GearAsked);

        h.Check.OnItemTaken("chainmail hauberk", 1);
        h.Check.OnItemTaken("torch", 2);

        Assert.False(h.Held);
        Assert.Equal(new[] { "chainmail hauberk", "torch" }, Assert.Single(h.GearAsked));
        Assert.Equal([("rope", 1)], h.Check.LastStillMissing);
        Assert.Equal(3, h.Check.LastMissing.Count);
        Assert.Contains("picked up chainmail hauberk, 2 torch", Assert.Single(h.Notices));
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

    // A get the game refuses prints no `You took`: movement isn't held on it, and
    // with nothing back there is nothing to ask the equipment manager for.
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
        Assert.Empty(h.GearAsked);
        Assert.Empty(h.Notices);
    }

    // ----- No more than the board drops ---------------------------------

    [Fact]
    public void NoMoreIsPickedUp_ThanTheRealmSaysAHangupDrops()
    {
        Harness h = new() { MaxItems = 2 };
        h.Stored(Snap(worn: [("chainmail hauberk", "Torso")], carried: ["5 torch", "rope"]));

        // Everything is gone and all of it lies here: a list this far out can't be
        // one hang-up's doing, so only two copies are the board's.
        h.ConnectAndEnter("chainmail hauberk", "5 torch", "rope");
        h.ReadInventory(Snap());

        Assert.Equal([("chainmail hauberk", 1), ("torch", 1)], h.Collected);

        h.Check.OnItemTaken("chainmail hauberk", 1);
        h.Check.OnItemTaken("torch", 1);

        Assert.False(h.Held);
        Assert.Equal([("torch", 4), ("rope", 1)], h.Check.LastStillMissing);
    }

    // ----- Where and when it holds back ---------------------------------

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
        Assert.Contains("2 torch missing", Assert.Single(h.Notices));
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

    // Auto-All can be switched off while a fight is waited out.
    [Fact]
    public void AutoAllSwitchedOffDuringTheFight_NothingIsPickedUpWhenItEnds()
    {
        Harness h = new() { Fighting = true };
        h.Stored(Snap(carried: ["2 torch"]));
        h.ConnectAndEnter("2 torch");
        h.ReadInventory(Snap());

        h.AutoAll = false;
        h.Fighting = false;
        h.Check.OnRoomObserved();

        Assert.Empty(h.Collected);
        Assert.False(h.Held);
        Assert.Contains("Auto-All is off", h.Check.LastOutcome);
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

    // Out to the board's menu and back in on the same link: no second check, and
    // the list goes on being written.
    [Fact]
    public void ASecondEntryOnOneLink_StartsNoCheck_ButTheListIsStillWritten()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["torch"]));
        h.ConnectAndEnter();
        h.ReadInventory(Snap(carried: ["torch"]));
        h.Check.OnInGameChanged(false);

        h.Check.OnInGameChanged(true);
        h.ShowRoom("torch");
        h.Inventory = Snap(carried: ["torch", "rope"]);
        h.Check.StampForSave(h.Profile);

        Assert.False(h.Held);
        Assert.Empty(h.Collected);
        Assert.Equal(2, h.Profile.HeldAtDisconnect!.Items.Count);
    }

    // ----- The hold never outlasts its reason ---------------------------

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

    // An entry made by hand sends no `i`: one is asked for, once. With still no
    // read the hold is given up, the list is kept, and the read that does come is
    // compared without the hold.
    [Fact]
    public void NoInventoryRead_OneIsAskedFor_ThenTheHoldIsGivenUp_AndALateReadIsStillCompared()
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
        Assert.Contains("without the hold", h.Check.LastOutcome);

        // A save in between can't write over the list: no inventory has been read.
        h.Check.StampForSave(h.Profile);
        Assert.Same(stored, h.Profile.HeldAtDisconnect);

        // Still standing in the room it came in at: the late read picks up.
        h.Heartbeats(30);
        h.ReadInventory(Snap());

        Assert.Equal(["i\r"], h.Sent);
        Assert.Equal([("torch", 1)], h.Collected);
        Assert.False(h.Held);
    }

    // The character walked on before the late read: what is missing is reported,
    // and the floor here, some other room's, is left alone.
    [Fact]
    public void ALateInventoryRead_AfterMovingOn_OnlyReports()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["torch"]));
        h.ConnectAndEnter("torch");
        h.Heartbeats(8);
        Assert.False(h.Held);

        h.Check.OnRoomChanged();
        h.ShowRoom("torch");
        h.ReadInventory(Snap());

        Assert.Empty(h.Collected);
        Assert.Equal([("torch", 1)], h.Check.LastMissing);
        Assert.Equal("idle", h.Check.Status);
    }

    // A statline the prompt scanner can't read gives no game prompt. Only the game
    // draws a room, so a room display is entry enough.
    [Fact]
    public void ARoomDisplayWithNoGamePrompt_CountsAsEnteringTheGame()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["torch"]));
        h.Check.NoteConnected();

        h.ShowRoom("torch");
        h.ReadInventory(Snap());

        Assert.Equal([("torch", 1)], h.Collected);

        // And leaving is seen at the disconnect, though no prompt ever said "in".
        h.Check.OnItemTaken("torch", 1);
        h.Inventory = Snap(carried: ["torch"]);
        h.Check.NoteDisconnected();
        Assert.Equal("torch", Assert.Single(h.Profile.HeldAtDisconnect!.Items).Name);
    }

    // A login that stops at the board's menu can wait as long as the user likes;
    // the hold can't.
    [Fact]
    public void TheGameNotEnteredInThreeMinutes_TheHoldIsGivenUp_AndTheListKept()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["torch"]));
        HeldAtDisconnect stored = h.Profile.HeldAtDisconnect!;
        h.Check.NoteConnected();

        h.Heartbeats(179);
        Assert.True(h.Held);
        h.Heartbeats(1);

        Assert.False(h.Held);
        Assert.Same(stored, h.Profile.HeldAtDisconnect);

        // Entered at last: compared all the same, without the hold or an `i` of its own.
        h.ShowRoom("torch");
        h.Check.OnInGameChanged(true);
        h.Heartbeats(10);
        Assert.Empty(h.Sent);
        h.ReadInventory(Snap());

        Assert.Equal([("torch", 1)], h.Collected);
        Assert.False(h.Held);
    }

    // Reset States frees the hold from wherever the check stands. Before the
    // comparison the list is kept and still compared; after it the check stops.
    [Fact]
    public void ResetStates_FreesTheHold_FromEveryPhase()
    {
        // Waiting for the game.
        Harness entry = new();
        entry.Stored(Snap(carried: ["torch"]));
        entry.Check.NoteConnected();
        entry.Check.Cancel("reset");
        Assert.False(entry.Held);

        // Waiting for the inventory: the late read is compared.
        Harness inventory = new();
        inventory.Stored(Snap(carried: ["torch"]));
        HeldAtDisconnect stored = inventory.Profile.HeldAtDisconnect!;
        inventory.ConnectAndEnter("torch");
        inventory.Check.Cancel("reset");
        Assert.False(inventory.Held);
        inventory.Heartbeats(10);
        Assert.Empty(inventory.Sent);
        Assert.Same(stored, inventory.Profile.HeldAtDisconnect);
        inventory.ReadInventory(Snap());
        Assert.Equal([("torch", 1)], inventory.Collected);

        // Waiting for a room display.
        Harness room = new();
        room.Stored(Snap(carried: ["torch"]));
        room.Check.NoteConnected();
        room.Check.OnInGameChanged(true);
        room.ReadInventory(Snap());
        Assert.True(room.Held);
        room.Check.Cancel("reset");
        Assert.False(room.Held);
        Assert.Equal("idle", room.Check.Status);

        // Waiting out a fight.
        Harness fight = new() { Fighting = true };
        fight.Stored(Snap(carried: ["torch"]));
        fight.ConnectAndEnter("torch");
        fight.ReadInventory(Snap());
        fight.Check.Cancel("reset");
        Assert.False(fight.Held);
        fight.Fighting = false;
        fight.Check.OnRoomObserved();
        Assert.Empty(fight.Collected);

        // Picking up.
        Harness picking = new();
        picking.Stored(Snap(carried: ["torch"]));
        picking.ConnectAndEnter("torch");
        picking.ReadInventory(Snap());
        Assert.True(picking.Held);
        picking.Check.Cancel("reset");
        Assert.False(picking.Held);
        Assert.Equal("idle", picking.Check.Status);
    }

    // Another character loaded: the check was the other one's, and so is what it found.
    [Fact]
    public void AnotherProfileLoaded_FreesTheHold_FromEveryPhase_AndForgetsTheLastCheck()
    {
        Harness entry = new();
        entry.Stored(Snap(carried: ["torch"]));
        entry.Check.NoteConnected();
        entry.Check.OnProfileLoaded();
        Assert.False(entry.Held);
        Assert.Equal("idle", entry.Check.Status);

        Harness inventory = new();
        inventory.Stored(Snap(carried: ["torch"]));
        inventory.ConnectAndEnter("torch");
        inventory.Check.OnProfileLoaded();
        Assert.False(inventory.Held);
        inventory.ReadInventory(Snap());
        Assert.Empty(inventory.Collected);

        Harness fight = new() { Fighting = true };
        fight.Stored(Snap(carried: ["torch"]));
        fight.ConnectAndEnter("torch");
        fight.ReadInventory(Snap());
        Assert.Single(fight.Check.LastMissing);
        fight.Check.OnProfileLoaded();
        Assert.False(fight.Held);
        Assert.Empty(fight.Check.LastMissing);
        Assert.Empty(fight.Check.LastStillMissing);
        Assert.Contains("none", fight.Check.LastOutcome);

        Harness picking = new();
        picking.Stored(Snap(carried: ["torch"]));
        picking.ConnectAndEnter("torch");
        picking.ReadInventory(Snap());
        picking.Check.OnProfileLoaded();
        Assert.False(picking.Held);
        // The new character's profile isn't stamped from the old one's inventory.
        picking.Profile.HeldAtDisconnect = null;
        picking.Check.StampForSave(picking.Profile);
        Assert.Null(picking.Profile.HeldAtDisconnect);
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
        Assert.Equal(Harness.RealmKey, held.Realm);
        Assert.Equal(3, held.Room!.Room);
        Assert.Equal(1, held.PenaltiesSpanned);
        Assert.Equal(3, held.Items.Count);
        Assert.Equal(2, held.Items.Single(i => i.Name == "torch").Count);
    }

    // The default profile is the template new characters are made from, and some
    // other profile being saved isn't the one the inventory belongs to.
    [Fact]
    public void TheList_IsWrittenOnlyToTheNamedCharacterThatIsLoaded()
    {
        Harness unnamed = new() { Named = false };
        unnamed.Check.NoteConnected();
        unnamed.Check.OnInGameChanged(true);
        unnamed.ReadInventory(Snap(carried: ["torch"]));
        unnamed.Check.StampForSave(unnamed.Profile);
        Assert.Null(unnamed.Profile.HeldAtDisconnect);

        Harness h = new();
        h.Check.NoteConnected();
        h.Check.OnInGameChanged(true);
        h.ReadInventory(Snap(carried: ["torch"]));
        CharacterProfile other = new();
        h.Check.StampForSave(other);
        Assert.Null(other.HeldAtDisconnect);
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

    // Nothing clears the inventory record on a death and no `i` follows one. A
    // list written from it would name the whole deathpile as held, and the next
    // entry would take it all for a hang-up's drop.
    [Fact]
    public void ADeath_StoresNoList_UntilAnInventoryIsReadAgain()
    {
        Harness h = new();
        h.Check.NoteConnected();
        h.Check.OnInGameChanged(true);
        h.ReadInventory(Snap(worn: [("chainmail hauberk", "Torso")], carried: ["2 torch"]));
        h.Check.StampForSave(h.Profile);
        Assert.NotNull(h.Profile.HeldAtDisconnect);

        h.Check.OnPlayerDied();
        Assert.Null(h.Profile.HeldAtDisconnect);

        // A save after the death, and the link dropping, still store none.
        h.Check.StampForSave(h.Profile);
        h.Check.OnInGameChanged(false);
        Assert.Null(h.Profile.HeldAtDisconnect);

        // Back in with a fresh read: the list is what is really held.
        h.Check.NoteConnected();
        h.Check.OnInGameChanged(true);
        h.ReadInventory(Snap(carried: ["torch"]));
        h.Check.StampForSave(h.Profile);
        Assert.Equal("torch", Assert.Single(h.Profile.HeldAtDisconnect!.Items).Name);
    }

    [Fact]
    public void ADeathMidCheck_EndsIt_AndStoresNoList()
    {
        Harness h = new() { Fighting = true };
        h.Stored(Snap(carried: ["2 torch"]));
        h.ConnectAndEnter("2 torch");
        h.ReadInventory(Snap());
        Assert.True(h.Held);

        h.Check.OnPlayerDied();

        Assert.False(h.Held);
        Assert.Null(h.Profile.HeldAtDisconnect);
        h.Fighting = false;
        h.Check.OnRoomObserved();
        Assert.Empty(h.Collected);
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
    // entry still knows about it, and two drops may have cost twice the count.
    [Fact]
    public void ADropMidCheck_CarriesTheUnfoundItemsToTheNextEntry()
    {
        Harness h = new() { Fighting = true, MaxItems = 2 };
        h.Stored(Snap(worn: [("chainmail hauberk", "Torso")], carried: ["2 torch"]));
        h.ConnectAndEnter("chainmail hauberk", "2 torch");
        h.ReadInventory(Snap(carried: ["rope"]));

        h.Check.OnInGameChanged(false);
        h.Check.NoteDisconnected();

        HeldAtDisconnect carriedOver = h.Profile.HeldAtDisconnect!;
        Assert.Equal(2, carriedOver.PenaltiesSpanned);
        Assert.Equal(2, carriedOver.Items.Single(i => i.Name == "torch").Count);
        Assert.Equal(1, carriedOver.Items.Single(i => i.Name == "chainmail hauberk").Count);

        // The second drop took the rope too. Four copies may be the board's now.
        h.Fighting = false;
        h.ConnectAndEnter("chainmail hauberk", "2 torch", "rope");
        h.ReadInventory(Snap());

        Assert.Equal([("rope", 1), ("chainmail hauberk", 1), ("torch", 2)], h.Collected);
    }

    // What a finished check didn't find is not carried: it isn't looked for again.
    [Fact]
    public void AFinishedCheck_LeavesOnlyWhatIsHeldOnTheList()
    {
        Harness h = new();
        h.Stored(Snap(carried: ["torch", "rope"]));
        h.ConnectAndEnter();

        h.ReadInventory(Snap(carried: ["rope"]));

        HeldAtDisconnect list = h.Profile.HeldAtDisconnect!;
        Assert.Equal("rope", Assert.Single(list.Items).Name);
        Assert.Equal(1, list.PenaltiesSpanned);
    }
}

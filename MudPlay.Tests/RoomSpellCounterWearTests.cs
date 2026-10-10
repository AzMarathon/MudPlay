using System.Collections.Generic;
using System.Linq;
using System.Text;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// RoomSpellCounterWear over a real EquipmentManager and MovementCoordinator: which
// carried item goes on for a room's spell, when (before an engine's step with the
// step held, ahead of a typed step, on arrival), what a refusal or silence does, and
// when the slot goes back (and to what). The items are the game data's, cut down to
// what is weighed: phoenix feather and magma amulet both negate magma heat #526 from
// the neck, 10 / 1 against 0 / 0.
public sealed class RoomSpellCounterWearTests
{
    private const int MagmaHeat = 526, FreezingCold = 5242;
    private const int Feather = 1000, Amulet = 487, SapphireRing = 433, WinterhideTunic = 3660;

    private sealed class World
    {
        public List<EquippedItem> Worn { get; } = new();
        public List<string> Pack { get; } = new();
        public Dictionary<RoomKey, Room> Rooms { get; } = new();
        public EquipmentSettings Sets { get; } = new();
        public MovementCoordinator Coordinator { get; } = new();
        public EquipmentManager Gear { get; }
        public RoomSpellCounterWear Wear { get; }
        public LogService Log { get; } = new();
        public List<string> Info { get; } = new();
        public List<Action> Timers { get; } = new();
        public bool Enabled { get; set; } = true;
        public bool MasterOff { get; set; }
        public bool Moving { get; set; } = true;
        public bool SneakKept { get; set; }
        public int GearNotes { get; private set; }
        public HashSet<int> Unwearable { get; } = new();

        // name → (id, slot label as the game prints it, slot, armour class, damage resist)
        private readonly Dictionary<string, (int Id, string Label, EquipmentSlot Slot, int Ac, int Dr)> _items =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["phoenix feather"] = (Feather, "Neck", EquipmentSlot.Neck, 10, 1),
                ["magma amulet"] = (Amulet, "Neck", EquipmentSlot.Neck, 0, 0),
                ["silver necklace"] = (9001, "Neck", EquipmentSlot.Neck, 5, 0),
                ["holy symbol"] = (9002, "Neck", EquipmentSlot.Neck, 8, 0),
                ["sapphire ring"] = (SapphireRing, "Finger", EquipmentSlot.Finger1, 10, 0),
                ["winterhide tunic"] = (WinterhideTunic, "Torso", EquipmentSlot.Torso, 270, 27),
                ["platemail"] = (9003, "Torso", EquipmentSlot.Torso, 400, 50),
            };

        private static readonly Dictionary<int, int[]> Negators = new()
        {
            [MagmaHeat] = new[] { Amulet, Feather },
            [FreezingCold] = new[] { SapphireRing, WinterhideTunic },
        };

        public World()
        {
            Log.EntryAdded += e => { if (e.Severity == LogSeverity.Info) Info.Add(e.Message); };
            Gear = new EquipmentManager(
                readEquipment: () => Sets,
                getSnapshot: Snapshot,
                readCombat: () => new CombatSettings(),
                writeCombat: _ => { },
                resolveItemSlot: name => _items.TryGetValue(name, out var item) ? item.Slot : null,
                log: Log);
            Gear.SetGearHold(() => SneakKept);
            Wear = new RoomSpellCounterWear(
                Gear, Coordinator,
                enabled: () => Enabled,
                roomOf: key => Rooms.GetValueOrDefault(key),
                negatorsOf: spell => Negators.TryGetValue(spell, out int[]? ids) ? ids : Array.Empty<int>(),
                describe: id => Describe(_items.FirstOrDefault(i => i.Value.Id == id).Key),
                isWorn: id => Worn.Any(w => _items[w.Name].Id == id),
                isCarried: id => Worn.Any(w => _items[w.Name].Id == id) || Pack.Any(p => _items[p].Id == id),
                wornIn: slot => Worn.Select(w => Describe(w.Name)).FirstOrDefault(d => d?.Slot == slot),
                spellName: spell => spell == MagmaHeat ? "magma heat" : spell == FreezingCold ? "freezing cold" : null,
                beingMoved: () => Moving,
                schedule: (_, action) => Timers.Add(action),
                gearCommandSent: () => GearNotes++,
                log: Log)
            {
                MasterSwitchOff = () => MasterOff,
            };
        }

        private RoomSpellCounterItem? Describe(string? name) =>
            name is not null && _items.TryGetValue(name, out var item)
                ? new RoomSpellCounterItem(item.Id, name, item.Slot, item.Ac, item.Dr, !Unwearable.Contains(item.Id))
                : null;

        private InventorySnapshot Snapshot() => new(
            CurrencyHoldings.Empty, EncumbranceReading.Empty, Worn.ToList(), Pack.ToList(), DateTimeOffset.UtcNow);

        public List<string> Sent => Gear.LastSentForTests
            .Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r')).ToList();

        public bool Held => Coordinator.IsGateAsserted(MovementCoordinator.CounterWearGate);

        // The game puts the piece on: what was in its slot goes back to the pack.
        public void GameWears(string name)
        {
            var item = _items[name];
            foreach (EquippedItem was in Worn.Where(w => _items[w.Name].Slot == item.Slot).ToList())
            {
                Worn.Remove(was);
                Pack.Add(was.Name);
            }
            Pack.Remove(name);
            Worn.Add(new EquippedItem(name, item.Label));
            Wear.NoteWorn(name);
        }

        // Rooms 1..count in a line on map 16, joined east and west; `lava` are the
        // room numbers that carry magma heat.
        public void Line(int count, params int[] lava)
        {
            for (int n = 1; n <= count; n++)
            {
                var exits = new Dictionary<Direction, RoomExit>();
                if (n > 1) exits[Direction.W] = new RoomExit(new RoomKey(16, n - 1), RoomExitHint.None, RawHint: null);
                if (n < count) exits[Direction.E] = new RoomExit(new RoomKey(16, n + 1), RoomExitHint.None, RawHint: null);
                Rooms[new RoomKey(16, n)] = new Room
                {
                    Key = new RoomKey(16, n), Name = lava.Contains(n) ? "Lava Tube" : "Tunnel",
                    Spell = lava.Contains(n) ? MagmaHeat : 0, Exits = exits,
                };
            }
        }
    }

    private static RoomKey At(int room) => new(16, room);

    // The usual start: a necklace on, the feather and the amulet in the pack, lava
    // in rooms 3 and 4 of a line of twelve.
    private static World Volcano()
    {
        World w = new();
        w.Worn.Add(new EquippedItem("silver necklace", "Neck"));
        w.Pack.AddRange(new[] { "phoenix feather", "magma amulet" });
        w.Line(12, 3, 4);
        return w;
    }

    // ----- putting it on ------------------------------------------------------

    [Fact]
    public void BeforeAnEnginesStep_TheFeatherGoesOn_AndTheStepWaitsForTheGamesAnswer()
    {
        World w = Volcano();

        Assert.False(w.Wear.ReadyToEnter(At(3)));
        Assert.Equal(new[] { "wear phoenix feather" }, w.Sent);
        Assert.True(w.Held);
        Assert.Equal(1, w.GearNotes);   // a wear mid-fight is picked up by the combat engine

        // Asked again while it waits: still not ready, and nothing more is sent.
        Assert.False(w.Wear.ReadyToEnter(At(3)));
        Assert.Single(w.Sent);

        w.GameWears("phoenix feather");
        Assert.False(w.Held);
        Assert.True(w.Wear.ReadyToEnter(At(3)));
        Assert.Single(w.Sent);
        Assert.Equal(("phoenix feather", "silver necklace", MagmaHeat), w.Wear.OwnedSnapshot().Single());
        Assert.Single(w.Info, l => l.Contains("wearing 'phoenix feather' for magma heat (#526) before entering 16/3, in place of 'silver necklace'"));
    }

    [Fact]
    public void AStepIntoAnOrdinaryRoom_IsNotHeld()
    {
        World w = Volcano();

        Assert.True(w.Wear.ReadyToEnter(At(2)));
        Assert.True(w.Wear.ReadyToEnter(null));
        Assert.Empty(w.Sent);
    }

    [Fact]
    public void WithNoFeather_TheAmuletGoesOn()
    {
        World w = Volcano();
        w.Pack.Remove("phoenix feather");

        Assert.False(w.Wear.ReadyToEnter(At(3)));
        Assert.Equal(new[] { "wear magma amulet" }, w.Sent);
    }

    [Fact]
    public void AnItemThisCharacterCannotWear_IsNoCandidate_AndIsSaidOnce()
    {
        World w = Volcano();
        w.Unwearable.Add(Feather);

        Assert.False(w.Wear.ReadyToEnter(At(3)));
        Assert.Equal(new[] { "wear magma amulet" }, w.Sent);
        Assert.False(w.Wear.WillWear(Feather));
        Assert.True(w.Wear.WillWear(Amulet));

        w.GameWears("magma amulet");
        w.Wear.OnArrived(At(3));
        w.Wear.OnArrived(At(4));
        Assert.Single(w.Info, l => l.Contains("'phoenix feather' negates magma heat (#526) but this character can't wear it"));
    }

    [Fact]
    public void AcrossSlots_ThePieceThatCostsTheLeastArmourGoesOn()
    {
        // Freezing cold: a ring for an empty finger, or a tunic that would push
        // platemail out of the torso slot.
        World w = new();
        w.Worn.Add(new EquippedItem("platemail", "Torso"));
        w.Pack.AddRange(new[] { "winterhide tunic", "sapphire ring" });
        w.Rooms[At(1)] = new Room { Key = At(1), Name = "Tundra", Spell = FreezingCold, Exits = new Dictionary<Direction, RoomExit>() };

        Assert.False(w.Wear.ReadyToEnter(At(1)));

        Assert.Equal(new[] { "eq sapphire ring" }, w.Sent);
    }

    [Fact]
    public void WithACounterAlreadyOn_NothingIsSent()
    {
        World w = Volcano();
        w.Pack.Remove("magma amulet");
        w.Worn.Clear();
        w.Worn.Add(new EquippedItem("magma amulet", "Neck"));   // on by the user's own hand: not swapped for the feather

        Assert.True(w.Wear.ReadyToEnter(At(3)));
        w.Wear.OnArrived(At(3));

        Assert.Empty(w.Sent);
        Assert.Empty(w.Wear.OwnedSnapshot());
    }

    [Fact]
    public void ARefusedWear_LetsTheStepGo_AndIsNotTriedAgain()
    {
        World w = Volcano();
        w.Pack.Remove("magma amulet");
        Assert.False(w.Wear.ReadyToEnter(At(3)));

        w.Wear.NoteWearRefused();

        Assert.False(w.Held);
        Assert.True(w.Wear.ReadyToEnter(At(3)));
        w.Wear.OnArrived(At(3));
        w.Wear.OnArrived(At(4));
        Assert.Equal(new[] { "wear phoenix feather" }, w.Sent);   // the one refused wear
        Assert.Empty(w.Wear.OwnedSnapshot());
        Assert.False(w.Wear.WillWear(Feather));                   // and no route is planned on it any more
        Assert.Single(w.Info, l => l.Contains("the game refused 'phoenix feather'"));
    }

    [Fact]
    public void ARefusalWithNothingOfOursWaiting_IsNotOurs()
    {
        World w = Volcano();

        w.Wear.NoteWearRefused();   // a gear set's piece was refused

        Assert.True(w.Wear.WillWear(Feather));
    }

    [Fact]
    public void AWearTheGameNeverAnswers_HoldsTheStepNoLongerThanTheCap()
    {
        World w = Volcano();
        w.Pack.Remove("magma amulet");
        Assert.False(w.Wear.ReadyToEnter(At(3)));
        Assert.True(w.Held);

        w.Timers.Single()();   // the cap runs out

        Assert.False(w.Held);
        Assert.True(w.Wear.ReadyToEnter(At(3)));   // the step goes, and the wear isn't sent over and over
        Assert.Single(w.Sent);
        Assert.Single(w.Info, l => l.Contains("no answer to the wear of 'phoenix feather' in 3s"));
    }

    [Fact]
    public void AnAnswerInTime_LeavesTheCapNothingToDo()
    {
        World w = Volcano();
        Assert.False(w.Wear.ReadyToEnter(At(3)));
        w.GameWears("phoenix feather");

        w.Timers.Single()();

        Assert.DoesNotContain(w.Info, l => l.Contains("no answer"));
        Assert.Single(w.Wear.OwnedSnapshot());
    }

    [Fact]
    public void OnArrival_AMoveTheClientCouldNotGetAheadOf_IsCounteredThere()
    {
        // Dragged in by a leader: no ready check ran, and nothing is held.
        World w = Volcano();

        w.Wear.OnArrived(At(3));

        Assert.Equal(new[] { "wear phoenix feather" }, w.Sent);
        Assert.False(w.Held);
        Assert.Single(w.Info, l => l.Contains("on arriving in 16/3"));
    }

    [Fact]
    public void AnEnginesNextStep_WaitsForAWearSentOnArrival()
    {
        World w = Volcano();
        w.Wear.OnArrived(At(3));

        Assert.False(w.Wear.ReadyToEnter(At(4)));   // the arrival wear is still unanswered
        Assert.True(w.Held);
        Assert.Single(w.Sent);

        w.GameWears("phoenix feather");
        Assert.False(w.Held);
        Assert.True(w.Wear.ReadyToEnter(At(4)));
    }

    [Fact]
    public void ATypedStep_GetsTheWearAheadOfIt_WithNoHold()
    {
        World w = Volcano();

        w.Wear.BeforeTypedStep(At(3));

        Assert.Equal(new[] { "wear phoenix feather" }, w.Sent);
        Assert.False(w.Held);
    }

    [Fact]
    public void AKeptSneak_DoesNotHoldTheWear_ButDoesHoldTheRestore()
    {
        // The room's damage on every cast is the worse loss; giving the slot back
        // can wait for the sneak.
        World w = Volcano();
        w.SneakKept = true;

        Assert.False(w.Wear.ReadyToEnter(At(3)));
        Assert.Equal(new[] { "wear phoenix feather" }, w.Sent);
        w.GameWears("phoenix feather");

        w.Wear.OnArrived(At(10));
        Assert.Single(w.Sent);

        w.SneakKept = false;
        w.Gear.RunHeldGear();
        Assert.Equal(new[] { "wear phoenix feather", "wear silver necklace" }, w.Sent);
    }

    // ----- giving the slot back -----------------------------------------------

    private static World WearingTheFeatherIn(int room)
    {
        World w = Volcano();
        Assert.False(w.Wear.ReadyToEnter(At(room)));
        w.GameWears("phoenix feather");
        w.Wear.OnArrived(At(room));
        return w;
    }

    [Fact]
    public void WhileMoving_ItStaysOnUntilNoSuchRoomIsWithinFiveSteps()
    {
        World w = WearingTheFeatherIn(3);

        // Through the lava and out the far side: rooms 5 to 9 are one to five steps
        // from room 4.
        for (int room = 4; room <= 9; room++) w.Wear.OnArrived(At(room));
        Assert.Equal(new[] { "wear phoenix feather" }, w.Sent);

        w.Wear.OnArrived(At(10));

        // No gear set dresses the neck, so the piece it pushed out goes back.
        Assert.Equal(new[] { "wear phoenix feather", "wear silver necklace" }, w.Sent);
        Assert.Empty(w.Wear.OwnedSnapshot());
        Assert.Equal(2, w.GearNotes);   // the wear, and the restore
        Assert.Single(w.Info, l => l.Contains("within reach of 16/10 — giving its slot back"));
    }

    [Fact]
    public void ARouteThatWeavesInAndOut_DoesNotSwapOnEveryStep()
    {
        World w = WearingTheFeatherIn(3);

        foreach (int room in new[] { 4, 5, 6, 5, 4, 3, 2, 1, 2, 3 }) w.Wear.OnArrived(At(room));

        Assert.Equal(new[] { "wear phoenix feather" }, w.Sent);
    }

    [Fact]
    public void WhenTheWalkEndsOutside_TheSlotGoesBackThen()
    {
        World w = WearingTheFeatherIn(3);
        w.Wear.OnArrived(At(4));
        w.Wear.OnArrived(At(5));
        w.Wear.OnArrived(At(6));
        Assert.Single(w.Sent);

        w.Moving = false;
        w.Wear.OnMovementEnded(At(6));   // two steps clear of the lava

        Assert.Equal(new[] { "wear phoenix feather", "wear silver necklace" }, w.Sent);
    }

    [Theory]
    [InlineData(3)]   // standing in the lava
    [InlineData(5)]   // and right beside it
    public void StoppedInOrBesideSuchARoom_ItStaysOn(int room)
    {
        World w = WearingTheFeatherIn(3);
        w.Moving = false;

        w.Wear.OnMovementEnded(At(room));

        Assert.Single(w.Sent);
        Assert.Single(w.Wear.OwnedSnapshot());
    }

    [Fact]
    public void TheSetInForceOnTheWayOut_IsWhatGoesBackOn()
    {
        // A gear set is applied while the feather is on: its neck piece is left
        // alone then, and is what the slot returns to.
        World w = WearingTheFeatherIn(3);
        w.Pack.Add("holy symbol");
        EquipmentSet resting = new()
        {
            Trigger = EquipTriggerType.PreRestHp, Enabled = true, Name = "Rest",
            Slots = { new EquipmentSlotEntry(EquipmentSlot.Neck, "holy symbol") },
        };
        w.Sets.Sets.Add(resting);
        w.Gear.ApplyBySetId(resting.Id);
        Assert.Equal(new[] { "wear phoenix feather" }, w.Sent);   // the set did not take the feather off

        w.Wear.OnArrived(At(10));

        Assert.Equal(new[] { "wear phoenix feather", "wear holy symbol" }, w.Sent);
    }

    [Fact]
    public void ASlotALocationRuleHoldsToo_IsWornOnce_AndRevertedOnlyWhenBothLetGo()
    {
        // A Location rule that wears the feather across the volcano, and the counter
        // wear for the lava inside it: one slot, two reasons to hold it.
        World w = Volcano();
        Assert.False(w.Wear.ReadyToEnter(At(3)));                 // the counter's wear, before the step
        Assert.True(w.Gear.SetSlotOverride("phoenix feather"));   // the rule's claim, on arrival
        Assert.Single(w.Sent);                                    // not sent a second time
        w.GameWears("phoenix feather");

        w.Wear.OnArrived(At(10));                                 // clear of the lava, still inside the rule's area
        Assert.Single(w.Sent);

        w.Gear.ClearSlotOverride("phoenix feather");              // the rule's area ends
        Assert.Equal(new[] { "wear phoenix feather", "rem phoenix feather" }, w.Sent);
    }

    [Fact]
    public void WithAnEmptySlotBefore_TheCounterSimplyComesOff()
    {
        World w = Volcano();
        w.Worn.Clear();
        Assert.False(w.Wear.ReadyToEnter(At(3)));
        w.GameWears("phoenix feather");

        w.Wear.OnArrived(At(10));

        Assert.Equal(new[] { "wear phoenix feather", "rem phoenix feather" }, w.Sent);
    }

    [Fact]
    public void TakenOffByHandMeanwhile_NothingIsSentForIt()
    {
        World w = WearingTheFeatherIn(3);
        w.Worn.Clear();
        w.Worn.Add(new EquippedItem("silver necklace", "Neck"));   // the user put the necklace back

        w.Wear.OnArrived(At(10));

        Assert.Single(w.Sent);
        Assert.Empty(w.Wear.OwnedSnapshot());
    }

    // ----- the setting and the master switch ------------------------------------

    [Fact]
    public void SwitchedOffInSettings_NothingIsPutOn_AndNoRouteCountsTheCarriedItem()
    {
        World w = Volcano();
        w.Enabled = false;

        Assert.True(w.Wear.ReadyToEnter(At(3)));
        w.Wear.OnArrived(At(3));
        w.Wear.BeforeTypedStep(At(4));

        Assert.Empty(w.Sent);
        Assert.False(w.Wear.WillWear(Feather));
    }

    [Fact]
    public void SwitchedOffInSettingsWhileOneIsOn_ItIsStillGivenBack()
    {
        World w = WearingTheFeatherIn(3);
        w.Enabled = false;

        w.Wear.OnArrived(At(10));

        Assert.Equal(new[] { "wear phoenix feather", "wear silver necklace" }, w.Sent);
    }

    [Fact]
    public void WithTheMasterSwitchOff_NothingIsPutOnOrGivenBack_UntilItIsOnAgain()
    {
        World w = WearingTheFeatherIn(3);
        w.MasterOff = true;

        w.Wear.OnArrived(At(10));          // walked out by hand: no gear is swapped
        Assert.Single(w.Sent);
        Assert.False(w.Wear.WillWear(Amulet));

        w.MasterOff = false;
        w.Wear.OnArrived(At(10));          // the switch-on settles the room stood in
        Assert.Equal(new[] { "wear phoenix feather", "wear silver necklace" }, w.Sent);
    }

    [Fact]
    public void AnotherCharacterLoaded_NothingOfTheOldOnesIsOwnedOrHeld()
    {
        World w = Volcano();
        Assert.False(w.Wear.ReadyToEnter(At(3)));
        Assert.True(w.Held);

        w.Wear.Reset();

        Assert.False(w.Held);
        Assert.Empty(w.Wear.OwnedSnapshot());
        Assert.Null(w.Wear.PendingItem);
    }
}

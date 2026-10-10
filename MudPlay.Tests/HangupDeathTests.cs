using System.IO;
using MudPlay.Game;
using MudPlay.Game.Health;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Game.Recovery;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A board that penalises a hang-up kills a dropped character, and a standing one
// its HP share takes under the death threshold, in the call that takes it out of
// the game. Nobody sees that death; the client works it out on the next entry.
// These pin the rule it goes by, the pile it builds, and the record it makes.
public sealed class HangupDeathTests
{
    private static readonly RoomRef Left = new(1, 3);
    private static readonly RoomKey Temple = new(1, 1);

    // ----- The realm's settings ----------------------------------------

    [Theory]
    // master, pvpFrom, pvpTo, pve ticked, pveFrom, pveTo, in a PvP fight → the top share
    [InlineData(false, 25, 50, true, 25, 50, true, null)]    // master switch off
    [InlineData(false, 25, 50, true, 25, 50, false, null)]
    [InlineData(true, 25, 50, false, 10, 20, true, 50)]      // PvP fight: the PvP side
    [InlineData(true, 25, 50, false, 10, 20, false, null)]   // not in PvP, PvE not ticked
    [InlineData(true, 25, 50, true, 10, 20, false, 20)]      // PvE ticked: its own figures
    [InlineData(true, 25, 50, true, 10, 20, true, 50)]       // a PvP fight outranks it
    [InlineData(true, 0, 0, true, 0, 0, false, 0)]           // penalised, no HP taken
    [InlineData(true, 60, 30, false, 0, 0, true, 60)]        // the top is never under the bottom
    public void HpShareTop_IsTheSideThatApplied_OrNothingWhenThatHangUpIsFree(
        bool master, int pvpFrom, int pvpTo, bool pve, int pveFrom, int pveTo, bool pvpFight, int? expected)
    {
        RealmProfile realm = new()
        {
            HangupPenaltyEnabled = master,
            HangupPvpHpFromPercent = pvpFrom,
            HangupPvpHpToPercent = pvpTo,
            HangupPvePenaltyEnabled = pve,
            HangupPveHpFromPercent = pveFrom,
            HangupPveHpToPercent = pveTo,
        };

        Assert.Equal(expected, HangupPenaltyNotice.HpShareTop(realm, pvpFight));
        Assert.Null(HangupPenaltyNotice.HpShareTop(null, pvpFight));
    }

    // ----- Whether to look ----------------------------------------------

    [Theory]
    [InlineData(-5, 200, 50, true)]      // dropped
    [InlineData(0, 200, 0, true)]        // dropped, and a side that takes no HP still kills it
    [InlineData(100, 200, 50, true)]     // the largest share reaches 0
    [InlineData(101, 200, 50, false)]
    [InlineData(40, null, 50, false)]    // standing, max HP not known: can't say
    [InlineData(-5, null, 50, true)]
    [InlineData(-5, 200, null, false)]   // that hang-up isn't penalised
    [InlineData(null, 200, 50, false)]   // HP not known when it left
    public void Suspected_OnlyWhenPenalisedAndDroppedOrLowEnoughToBeDropped(
        int? hp, int? maxHp, int? share, bool expected) =>
        Assert.Equal(expected, HangupDeath.Suspected(hp, maxHp, share));

    // ----- The verdict ---------------------------------------------------

    // Left dropped at -5 of 200 with 7 lives on a realm taking up to 50%; back at
    // full HP with 6, nothing worn, the board's hang-up lines printed.
    private static HangupDeathVerdict Verdict(
        int? hpAtDrop = -5, int? share = 50, int? hpAtEntry = 200, int? livesBefore = 7, int? livesNow = 6,
        bool? worn = false, bool? loginLines = true) =>
        HangupDeath.Judge(hpAtDrop, 200, share, hpAtEntry, livesBefore, livesNow, worn, loginLines).Verdict;

    // Only a life lost says "died": exactly one, both counts read from the game.
    [Fact]
    public void ExactlyOneLifeFewer_IsADeath()
    {
        Assert.Equal(HangupDeathVerdict.Died, Verdict());
        // A standing character the share finished.
        Assert.Equal(HangupDeathVerdict.Died, Verdict(hpAtDrop: 40));
        // HP not read at the first prompt: the lives still say it.
        Assert.Equal(HangupDeathVerdict.Died, Verdict(hpAtEntry: null));
        // Where a death isn't known to unequip, or the board to print its lines
        // (not Stock), neither is asked for.
        Assert.Equal(HangupDeathVerdict.Died, Verdict(worn: null, loginLines: null));
    }

    // Left at the top of its HP on a realm whose share goes to 100%: a death sets
    // HP to the maximum, which is where it already was.
    [Fact]
    public void ADeathFromFullHp_ShowsNoRiseInHp_AndIsStillADeath() =>
        Assert.Equal(HangupDeathVerdict.Died,
            HangupDeath.Judge(200, 200, 100, 200, 7, 6, worn: false, loginLines: true).Verdict);

    [Fact]
    public void NotLookedAt_WhenNotPenalised_NotLowEnough_OrHpWasNotKnown()
    {
        Assert.Equal(HangupDeathVerdict.NotSuspected, Verdict(share: null));
        Assert.Equal(HangupDeathVerdict.NotSuspected, Verdict(hpAtDrop: 150));
        Assert.Equal(HangupDeathVerdict.NotSuspected, Verdict(hpAtDrop: null));
    }

    [Fact]
    public void NoLifeLost_IsNoDeath_WhateverElseIsSeen()
    {
        Assert.Equal(HangupDeathVerdict.Alive, Verdict(livesNow: 7));
        // Trained since, with no `stat` read after it: no life was lost.
        Assert.Equal(HangupDeathVerdict.Alive, Verdict(livesNow: 9));
        // Two down is more than this hang-up cost: something else went unseen.
        Assert.Equal(HangupDeathVerdict.Unsure, Verdict(livesNow: 5));
    }

    // HP, the room and the pack never say "died". Without the lives now, a `stat`
    // is what is needed; without the lives from before, nothing will tell.
    [Fact]
    public void WithoutBothCounts_NothingSaysDied()
    {
        Assert.Equal(HangupDeathVerdict.NeedsLives, Verdict(livesNow: null));
        Assert.Equal(HangupDeathVerdict.Unsure, Verdict(livesBefore: null));
        Assert.Equal(HangupDeathVerdict.Unsure, Verdict(livesBefore: null, livesNow: null));
    }

    // A death sets HP to its maximum. Back dropped, or under what it left with,
    // it wasn't killed: no `stat` is needed to say so.
    [Theory]
    [InlineData(-5, -5)]     // a free room: still dropped
    [InlineData(-5, 0)]
    [InlineData(40, -20)]    // the share dropped it, short of the threshold
    [InlineData(40, 12)]     // the share was taken, and it stands
    public void HpAtEntryOfACharacterNotKilled_ClosesTheQuestion_WhileNoLifeIsKnownLost(int hpAtDrop, int hpAtEntry)
    {
        Assert.Equal(HangupDeathVerdict.Alive, Verdict(hpAtDrop: hpAtDrop, hpAtEntry: hpAtEntry, livesNow: null));
        Assert.Equal(HangupDeathVerdict.Alive, Verdict(hpAtDrop: hpAtDrop, hpAtEntry: hpAtEntry, livesBefore: null));
        Assert.Equal(HangupDeathVerdict.Alive, Verdict(hpAtDrop: hpAtDrop, hpAtEntry: hpAtEntry, livesNow: 7));
    }

    // A life can be lost on another client between two sessions of this one. What
    // the entry showed then takes the death away again: it is told, not recorded.
    [Fact]
    public void ALifeLost_ButNotToThisHangUp_IsNotADeathHere()
    {
        // Something is still worn: a death takes everything off.
        Assert.Equal(HangupDeathVerdict.Unsure, Verdict(worn: true));
        // The board didn't print its hang-up lines: another session came in between.
        Assert.Equal(HangupDeathVerdict.Unsure, Verdict(loginLines: false));
        // It came back dropped, or under the HP it left with.
        Assert.Equal(HangupDeathVerdict.Unsure, Verdict(hpAtEntry: -3));
        Assert.Equal(HangupDeathVerdict.Unsure, Verdict(hpAtDrop: 40, hpAtEntry: 12));
    }

    // Stock's word on the way in that the last exit was a hang-up it didn't let go
    // free, as the engine prints it.
    [Theory]
    [InlineData("Last time you were on, you disconnected while playing.", true)]
    [InlineData("The gods have punished you appropriately.", false)]
    [InlineData("Borric gossips: Last time you were on, you disconnected while playing.", false)]
    public void TheBoardsHangUpLine_IsRecognised(string line, bool recognised)
    {
        MessageRouter router = new();
        MudPlay.Services.Patterns.DefaultPatterns.Seed(router);
        int seen = 0;
        using IDisposable sub = router.Subscribe(MudPlay.Services.Patterns.KnownPatterns.HangupLoginNotice, _ => seen++);

        router.Dispatch(new MudPlay.Terminal.LineExtractor.EmittedLine(
            line, Array.Empty<MudPlay.Terminal.CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));

        Assert.Equal(recognised ? 1 : 0, seen);
    }

    // ----- The pile ------------------------------------------------------

    private static InventorySnapshot Snap((string Name, string Slot)[]? worn = null, params string[] carried) =>
        new(CurrencyHoldings.Empty, EncumbranceReading.Empty,
            (worn ?? []).Select(w => new EquippedItem(w.Name, w.Slot)).ToList(),
            carried, DateTimeOffset.Now, null, null);

    private static HeldAtDisconnect ListOf(InventorySnapshot held)
    {
        (List<DeathItem> worn, List<DeathItem> carried) = DeathLootCapture.FromSnapshot(held);
        return new HeldAtDisconnect
        {
            At = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero),
            Room = Left,
            Items = HangupItemPlan.Held(held),
            Worn = worn,
            Carried = carried,
        };
    }

    [Fact]
    public void Pile_IsWhatWasHeldAndIsNotNow_WornPiecesWithTheirSlots()
    {
        HeldAtDisconnect before = ListOf(Snap(
            worn: [("chainmail hauberk", "Torso"), ("gold ring", "Finger"), ("gold ring", "Finger"), ("soulbound amulet", "Neck")],
            "3 torch", "rope", "gold ring"));

        // A loyal piece stayed (in the pack now: a death unequips everything), and
        // one torch and one ring are still held.
        (List<DeathItem> equipped, List<DeathItem> lost) = HangupDeath.Pile(before,
            Snap(null, "soulbound amulet", "torch", "gold ring"));

        Assert.Equal(new[] { ("chainmail hauberk", "Torso"), ("gold ring", "Finger"), ("gold ring", "Finger") },
            equipped.Select(i => (i.Name, i.Slot!)).ToArray());
        // The carried stack as the inventory words it, which recovery expands per copy.
        Assert.Equal(new[] { "2 torch", "rope" }, lost.Select(i => i.Name).ToArray());
        Assert.All(lost, i => Assert.Null(i.Slot));
    }

    // A death line's record lists what was worn and what was carried, and neither
    // the key ring nor the lit light. This one is built the same way, though the
    // item list it starts from counts both (a hang-up's item penalty takes keys).
    [Fact]
    public void Pile_LeavesOutTheKeyRingAndTheLitLight_AsAWitnessedPileDoes()
    {
        InventorySnapshot held = new(CurrencyHoldings.Empty, EncumbranceReading.Empty,
            [new EquippedItem("chainmail hauberk", "Torso")], ["2 torch", "rope"], DateTimeOffset.Now,
            new ReadiedLight("torch", 40), ["2 black star key"]);
        HeldAtDisconnect before = ListOf(held);
        Assert.Contains(before.Items, i => i.Name == "black star key");

        (List<DeathItem> equipped, List<DeathItem> lost) = HangupDeath.Pile(before, Snap());
        (List<DeathItem> seenWorn, List<DeathItem> seenLost) = DeathLootCapture.FromSnapshot(held);

        Assert.Equal(seenWorn.Select(i => i.Name), equipped.Select(i => i.Name));
        Assert.Equal(seenLost.Select(i => i.Name), lost.Select(i => i.Name));
        Assert.Equal(new[] { "2 torch", "rope" }, lost.Select(i => i.Name).ToArray());
    }

    // ----- The record ----------------------------------------------------

    private sealed class TrackerHarness : IDisposable
    {
        private const string GraphJson = """
            [
              { "Map Number": 1, "Room Number": 1, "Name": "Temple, Halls of the Dead",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "1/3", "S": "0", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
              { "Map Number": 1, "Room Number": 3, "Name": "Dark Cave",
                "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
                "N": "0", "S": "1/1", "E": "0", "W": "0",
                "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
            ]
            """;

        private readonly string _root;
        public CharacterProfile Profile { get; } = new() { LastKnownRoom = new RoomRef(1, 1) };
        public RoomTracker Tracker { get; }
        public MovementCoordinator Coordinator { get; } = new();
        public PlayerDeathMovementHalt Halt { get; }
        public int EngineStops { get; private set; }
        public int Observed { get; private set; }
        public int Inferred { get; private set; }
        public List<byte[]> Sent { get; } = new();

        public TrackerHarness(bool profileLoaded = true)
        {
            _root = Path.Combine(Path.GetTempPath(), "mudplay-hangupdeath-" + Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(_root, "alpha"));
            File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), GraphJson);
            GameDataCache cache = new(_root);
            cache.SwitchSet("alpha");
            RoomGraphManager graph = new(cache);
            graph.OnActiveSetChanged("alpha");

            Tracker = new RoomTracker(graph);
            if (profileLoaded) Tracker.Hydrate(Profile);
            Tracker.PlayerDeathObserved += () => Observed++;
            Tracker.PlayerDeathInferred += () => Inferred++;
            Halt = new PlayerDeathMovementHalt(Tracker, Coordinator);
            Halt.SetEngineStopper(() => EngineStops++);
            Halt.SetWireSender(Sent.Add);
        }

        public void Dispose()
        {
            Halt.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    private static UnwitnessedDeath Unseen() => new(
        Left, new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero), LivesRemaining: 6,
        HangupItemRecheck.DeathMessage,
        Equipped: [new DeathItem("chainmail hauberk", "Torso")],
        Lost: [new DeathItem("2 torch"), new DeathItem("rope")],
        Coins: new CurrencyHoldings(0, 0, 25, 0, 0, 2500));

    [Fact]
    public void AnUnwitnessedDeath_GetsTheRecordADeathLineMakes()
    {
        using TrackerHarness h = new();

        DeathRecord? made = h.Tracker.NoteUnwitnessedDeath(Unseen());

        DeathRecord record = Assert.Single(h.Profile.DeathHistory!);
        Assert.Same(record, made);
        Assert.Equal(1, record.RecordNumber);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero), record.At);
        Assert.Equal("1/3", record.RoomKeyText);
        Assert.Equal("Dark Cave", record.RoomName);
        Assert.Equal(6, record.LivesRemaining);
        Assert.Equal(HangupItemRecheck.DeathMessage, record.MessageText);
        Assert.Equal(DeathRecoveryStatus.Active, record.Status);
        Assert.Equal("Torso", Assert.Single(record.EquippedAtDeath!).Slot);
        Assert.Equal(new[] { "2 torch", "rope" }, record.LostItems!.Select(i => i.Name).ToArray());
        Assert.Equal(25, record.CoinsAtDeath!.Value.Gold);
        // Nothing was on screen to keep.
        Assert.Null(record.DeathLogFile);
    }

    // The character is already in the room the game put it in and the map has
    // read it: the position is left alone, and nothing that belongs to the moment
    // of dying is replayed.
    [Fact]
    public void AnUnwitnessedDeath_LeavesThePositionAlone_AndRaisesItsOwnEvent()
    {
        using TrackerHarness h = new();
        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);

        h.Tracker.NoteUnwitnessedDeath(Unseen());

        Assert.Equal(1, h.Inferred);
        Assert.Equal(0, h.Observed);
        Assert.Equal(RoomConfidence.Confirmed, h.Tracker.State.Confidence);
        Assert.Equal(Temple, h.Tracker.State.CurrentRoom!.Key);
    }

    // A loop the reconnect restarted must not walk a stripped character out of
    // the temple; there is no respawn display to hurry along.
    [Fact]
    public void AnUnwitnessedDeath_StopsTheEngines_WithoutTheRespawnResync()
    {
        using TrackerHarness h = new();
        h.Coordinator.AssertGate(MovementCoordinator.UserGate, "user");

        h.Tracker.NoteUnwitnessedDeath(Unseen());
        h.Halt.FireGraveyardResyncForTests();

        Assert.Equal(1, h.EngineStops);
        Assert.False(h.Coordinator.IsGateAsserted(MovementCoordinator.UserGate));
        Assert.Empty(h.Sent);
    }

    // The witnessed path makes the same record through the same code, numbered on.
    [Fact]
    public void AWitnessedDeath_StillRecordsAsBefore_AndNumbersOn()
    {
        using TrackerHarness h = new();
        h.Tracker.AttachInventorySnapshot(() => Snap(worn: [("dagger", "Weapon Hand")], "rope"));

        h.Tracker.NoteUnwitnessedDeath(Unseen());
        h.Tracker.NoteDeath(5, "You have 5 lives left.");

        Assert.Equal(2, h.Profile.DeathHistory!.Count);
        DeathRecord seen = h.Profile.DeathHistory[1];
        Assert.Equal(2, seen.RecordNumber);
        Assert.Equal("1/1", seen.RoomKeyText);
        Assert.Equal("Temple, Halls of the Dead", seen.RoomName);
        Assert.Equal("Weapon Hand", Assert.Single(seen.EquippedAtDeath!).Slot);
        Assert.Equal("rope", Assert.Single(seen.LostItems!).Name);
        Assert.Equal(1, h.Observed);
        Assert.Equal(RoomConfidence.PendingRespawn, h.Tracker.State.Confidence);
    }

    [Fact]
    public void WithNoProfile_NothingIsRecorded()
    {
        using TrackerHarness h = new(profileLoaded: false);

        Assert.Null(h.Tracker.NoteUnwitnessedDeath(Unseen()));
        Assert.Null(h.Profile.DeathHistory);
        Assert.Equal(0, h.Inferred);
        Assert.Equal(0, h.EngineStops);
    }
}

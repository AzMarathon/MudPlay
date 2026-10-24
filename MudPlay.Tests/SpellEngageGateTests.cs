using System.Text;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// Whether a monster no weapon of ours can hurt is engaged by an attack spell: the
// engage gate (CombatManager.CanEngageMonster, the walker gate's delegate, and the
// retarget loop's verdict) asks the spell chooser, so the two agree. Weapons come
// from the Equipment Manager's Default gear set as the client reads them, game data
// from a real set on disk, the spell book from it, the party from the game's own
// follow line, and the game's output as bytes through a ServerFeed.
public sealed class SpellEngageGateTests
{
    private sealed class Harness : IDisposable
    {
        private readonly string _root;
        public MessageRouter Router { get; } = new();
        public ManualClock Clock { get; } = new();
        public ServerFeed Server { get; }
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new(new ProfileService(), () => null);
        public PartyState Party { get; } = new();
        public PartyManager PartyManager { get; }
        public LogService Log { get; } = new();
        public List<string> Logged { get; } = new();
        public RoomEntityClassifier Classifier { get; }
        public MovementCoordinator Coordinator { get; }
        public PlayerState State { get; } = new();
        public CombatStateTracker Tracker { get; }
        public CastCoordinator Cast { get; }
        public SpellbookState Spellbook { get; }
        public CombatManager Combat { get; }
        public List<byte[]> Sent { get; } = new();
        public CombatSettings Settings { get; } = new() { NormalAttackCommand = "a", TargetOrder = TargetOrder.Normal };
        public EquipmentSettings Equipment { get; } = new();
        public string? WornWeapon { get; set; }
        public int Ma { get; set; } = 30;
        public int MaxMa { get; set; } = 30;

        public Harness()
        {
            _root = Path.Combine(Path.GetTempPath(), "mudplay-spell-engage-" + Path.GetRandomFileName());
            WriteGameData(Path.Combine(_root, "alpha"));
            GameDataCache cache = new(_root);
            cache.SwitchSet("alpha");

            DefaultPatterns.Seed(Router);
            Server = new ServerFeed(Router, () => Clock.Now);
            Log.EntryAdded += e => Logged.Add(e.Message);
            PartyManager = new PartyManager(Router, Party, Log, now: () => Clock.Now);
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, Log, now: () => Clock.Now);
            // Built ahead of the combat manager, as AppServices builds it, so it reads
            // each room before the combat manager does.
            Coordinator = new MovementCoordinator(Log);
            Tracker = new CombatStateTracker(Router, Coordinator, Classifier, Monsters, State,
                () => true, resolveOverlay: _ => new MonsterOverlay(), log: Log, clock: () => Clock.Now);
            Cast = new CastCoordinator(Router, Log, now: () => Clock.Now);
            Cast.SetWireSender(b => Sent.Add(b));
            Spellbook = new SpellbookState(new KnownSpellCatalog(cache));
            Spellbook.Refresh(12, 20);
            Combat = new CombatManager(Router, Classifier, Monsters,
                resolveOverlay: _ => new MonsterOverlay(),
                party: Party,
                // The weapons are the Default gear set's, overlaid on each read.
                readSettings: () =>
                {
                    EquipmentWeaponSync.ApplyWeapons(Settings, Equipment);
                    return Settings;
                },
                isEnabled: () => true,
                readOwnGivenName: () => "Ermias",
                post: a => a(),
                log: Log,
                resolveSpellByCode: code => Spellbook.FindByCastCode(code),
                now: () => Clock.Now);
            Combat.SetWireSender(b => Sent.Add(b));
            Combat.SetSwitchDispatchScheduler((_, cb) => cb());
            Combat.SetArrivalSettleScheduler((_, _) => { });
            Combat.SetAutoNukeGate(() => true);
            Combat.SetCombatSpellCaster(Cast, () => (Ma, MaxMa));
            Combat.SetSpellManaCost(Spellbook.ManaCostOf);
            Combat.SetMagicEligibility(new MonsterMagicIndex(cache), new ItemMagicIndex(cache),
                new SpellReqLevelIndex(cache), new MonsterResistIndex(cache), new SpellAttackTypeIndex(cache));
            Combat.SetDrainEligibility(new MonsterLifeIndex(cache), new SpellTargetTypeIndex(cache));
            Combat.SetWeaponActuator((_, _, _) => { }, readWornWeapon: () => WornWeapon);
            Combat.SetCarriedCheck(_ => true);
            Tracker.SetActionabilityGate(n => Combat.CanEngageMonster(n));

            AddMonster(500, "bog wight");
            AddMonster(501, "marsh hag");
            AddMonster(502, "giant rat");
            AddMonster(503, "stone warden");
        }

        // A katana with hit magic 3; monsters needing 4 (the rat needs none, the
        // warden's spell immunity is 5); a Mage's magic missile (level 1, cost 3),
        // dancing blades (a room attack, cost 26) and turn undead (undead only).
        private static void WriteGameData(string dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Classes.json"), """
                [ { "Number": 12, "Name": "Mage", "MageryType": 1, "MageryLVL": 3, "ArmourType": 1, "CombatLVL": 3 } ]
                """);
            File.WriteAllText(Path.Combine(dir, "Items.json"), """
                [ { "Number": 1, "Name": "katana", "Abil-0": 28, "AbilVal-0": 3 } ]
                """);
            File.WriteAllText(Path.Combine(dir, "Monsters.json"), """
                [ { "Number": 500, "Name": "bog wight", "Abil-0": 28, "AbilVal-0": 4 },
                  { "Number": 501, "Name": "marsh hag", "Abil-0": 28, "AbilVal-0": 4 },
                  { "Number": 502, "Name": "giant rat" },
                  { "Number": 503, "Name": "stone warden", "Abil-0": 28, "AbilVal-0": 4, "Abil-1": 139, "AbilVal-1": 5 } ]
                """);
            File.WriteAllText(Path.Combine(dir, "Spells.json"), """
                [ { "Number": 1, "Name": "magic missile", "Short": "mmis", "ReqLevel": 1, "ManaCost": 3,
                    "Targets": 8, "AttType": 4, "Magery": 1, "MageryLVL": 1, "Learnable": 1, "Abil-0": 17 },
                  { "Number": 133, "Name": "dancing blades", "Short": "blad", "ReqLevel": 11, "ManaCost": 26,
                    "Targets": 12, "AttType": 4, "Magery": 1, "MageryLVL": 1, "Learnable": 1, "Abil-0": 17 },
                  { "Number": 40, "Name": "turn undead", "Short": "turn", "ReqLevel": 3, "ManaCost": 5,
                    "Targets": 8, "AttType": 4, "Magery": 1, "MageryLVL": 1, "Learnable": 1, "Abil-0": 23 } ]
                """);
        }

        private void AddMonster(int number, string name) => Monsters.Messages.Add(
            new MonsterMessageRecord($"M{number}", name, new[] { new GameDataLink("Monsters", number) }));

        // The weapon set in the Equipment Manager's Default gear set, and wielded.
        public void WieldFromGearSet(string weapon)
        {
            EquipmentSet def = new() { Trigger = EquipTriggerType.Default, Name = "Default" };
            def.Slots.Add(new EquipmentSlotEntry(EquipmentSlot.Weapon, weapon));
            Equipment.Sets.Add(def);
            WornWeapon = weapon;
        }

        public void JoinParty() => Server.Line("You are now following Borland.");

        public IEnumerable<string> AllSent => Sent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r'));
        public bool WalkerHeld => Coordinator.AssertedGates.Contains(MovementCoordinator.CombatGate);
        public string? CannotAttackLine => Logged.FirstOrDefault(l => l.StartsWith("cannot attack", StringComparison.Ordinal));

        // One combat round, in the order AppServices subscribes the tick.
        public void Tick()
        {
            Clock.Advance(TimeSpan.FromSeconds(5));
            Cast.OnCombatTick();
            Combat.OnCombatTick();
        }

        public void Dispose()
        {
            Combat.Dispose();
            Cast.Dispose();
            Tracker.Dispose();
            Classifier.Dispose();
            PartyManager.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup of the temp set */ }
        }
    }

    public static TheoryData<CombatActionOrder> EveryOrder => new()
    {
        CombatActionOrder.SpellsFirst,
        CombatActionOrder.PhysicalFirst,
        CombatActionOrder.AlternateSpellPhysical,
        CombatActionOrder.AlternatePhysicalSpell,
        CombatActionOrder.CustomRoundCycle,
    };

    // Ermias: magic missile typed as his attack command, the katana (hit magic 3) in
    // his Default gear set, a monster needing 4 and with no spell immunity. The
    // command was judged by the katana, the monster written off as one nothing could
    // hurt, and mmis never sent until he took the katana out of the gear set. The
    // command is a spell and is judged as one: it engages, under every order, with
    // the katana still set.
    [Theory]
    [MemberData(nameof(EveryOrder))]
    public void ErmiasCase_MmisAsTheAttackCommand_KatanaInTheGearSet_CastsAtTheMonster(CombatActionOrder order)
    {
        using Harness h = new();
        h.WieldFromGearSet("katana");
        h.Settings.ActionOrder = order;
        h.Settings.NormalAttackCommand = "mmis";

        h.Server.Line("Also here: bog wight.");

        Assert.Equal("katana", h.Settings.NormalWeapon);
        Assert.True(h.Combat.CanEngageMonster(500));
        Assert.True(h.WalkerHeld);
        Assert.Contains("mmis bog wight", h.AllSent);
        Assert.Null(h.CannotAttackLine);
    }

    // The same character with magic missile in the attack-spell slot instead: cast
    // under every order, the katana still in the gear set.
    [Theory]
    [MemberData(nameof(EveryOrder))]
    public void MmisInTheAttackSpellSlot_KatanaInTheGearSet_CastsAtTheMonster(CombatActionOrder order)
    {
        using Harness h = new();
        h.WieldFromGearSet("katana");
        h.Settings.ActionOrder = order;
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "mmis" };

        h.Server.Line("Also here: bog wight.");

        Assert.True(h.Combat.CanEngageMonster(500));
        Assert.True(h.WalkerHeld);
        Assert.Equal(new[] { "mmis bog wight" }, h.AllSent);
    }

    // A spell typed as the attack command is judged by its level against the
    // monster's spell immunity, so one under it is still out.
    [Fact]
    public void SpellAsTheAttackCommand_UnderTheMonstersSpellImmunity_IsWalkedPast()
    {
        using Harness h = new();
        h.WieldFromGearSet("katana");
        h.Settings.NormalAttackCommand = "mmis";

        h.Server.Line("Also here: stone warden.");

        Assert.False(h.Combat.CanEngageMonster(503));
        Assert.False(h.WalkerHeld);
        Assert.Empty(h.AllSent);
        Assert.Contains("its level 1 is under the monster's spell immunity 5", h.CannotAttackLine);
    }

    // A room attack is the only spell, and the room meets its Min enemies: cast bare.
    [Fact]
    public void RoomAttackOnly_RoomMeetsMinEnemies_CastsIt()
    {
        using Harness h = new();
        h.WieldFromGearSet("katana");
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blad", MinEnemies = 2 };

        h.Server.Line("Also here: bog wight, marsh hag.");

        Assert.True(h.Combat.CanEngageMonster(500));
        Assert.True(h.WalkerHeld);
        Assert.Equal(new[] { "blad" }, h.AllSent);
    }

    // Below its Min enemies the room attack isn't fired at a lone monster because
    // nothing else can hurt it: the room settings still hold, and it's walked past.
    [Fact]
    public void RoomAttackOnly_BelowMinEnemies_RespectsTheSettingAndWalksPast()
    {
        using Harness h = new();
        h.WieldFromGearSet("katana");
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blad", MinEnemies = 2 };

        h.Server.Line("Also here: bog wight.");

        Assert.False(h.Combat.CanEngageMonster(500));
        Assert.False(h.WalkerHeld);
        Assert.Empty(h.AllSent);
        Assert.Contains("room attack 1 blad: 1 enemy(ies) here, its Min enemies is 2", h.CannotAttackLine);
    }

    // A spell the chooser would never cast at this monster doesn't make it
    // engageable: a type it can't affect, or a cast cap of 0.
    [Theory]
    [InlineData("turn", null, "can't affect this kind of monster")]
    [InlineData("mmis", 0, "its 0 cast(s) at this target are spent")]
    public void AttackSpellTheChooserWouldNotCast_DoesNotCountAsEngaging(string spell, int? maxCasts, string why)
    {
        using Harness h = new();
        h.WieldFromGearSet("katana");
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = spell, MaxCastsPerRoom = maxCasts };

        h.Server.Line("Also here: bog wight.");

        Assert.False(h.Combat.CanEngageMonster(500));
        Assert.False(h.WalkerHeld);
        Assert.Empty(h.AllSent);
        Assert.Contains(why, h.CannotAttackLine);
    }

    // A weapon below the monster's magic can't engage it, and with no spell there's
    // nothing else: it's written off, walked past, and the log says why.
    [Fact]
    public void WeaponOut_NoSpell_IsUnkillable_AndWalkedPast()
    {
        using Harness h = new();
        h.WieldFromGearSet("katana");

        h.Server.Line("Also here: bog wight.");

        Assert.False(h.Combat.CanEngageMonster(500));
        Assert.False(h.WalkerHeld);
        Assert.Empty(h.AllSent);
        Assert.Contains("normal attack `a` with katana hits magic 3, it needs 4", h.CannotAttackLine);
        Assert.Contains("no attack spell set", h.CannotAttackLine);
        // The bug report's room assessment carries the same reasons.
        CombatManager.RoomEngageSnapshot row = Assert.Single(h.Combat.SnapshotRoomEngage());
        Assert.Equal(CombatManager.EngageAssessment.Unkillable, row.Assessment);
        Assert.Contains("katana hits magic 3", row.Why);
    }

    // "Fire regardless of current mana": in a party the slot's mana reserve is set
    // aside and the spell casts on its cost alone; solo, or with the box clear, the
    // reserve holds and the monster is left for now (a mana stall, not a write-off).
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void FireRegardlessOfMana_PartiedAndShortOfTheReserve_Casts_OnlyInAParty(
        bool partied, bool fireRegardless, bool casts)
    {
        using Harness h = new();
        h.WieldFromGearSet("katana");
        h.Settings.SpellManaThresholdMode = ThresholdMode.Absolute;
        h.Settings.NormalAttackSpell = new CombatSpellSlot
        {
            SpellName = "mmis", MinManaPerCast = 20, FireRegardlessOfMana = fireRegardless,
        };
        h.Ma = 10;
        if (partied) h.JoinParty();

        h.Server.Line("Also here: bog wight.");

        Assert.Equal(casts, h.Combat.CanEngageMonster(500));
        Assert.Equal(casts, h.WalkerHeld);
        Assert.Equal(casts, h.AllSent.Contains("mmis bog wight"));
        // Short of the reserve is a stall, retried as mana comes back, not a write-off.
        Assert.Null(h.CannotAttackLine);
        if (casts) return;
        CombatManager.RoomEngageSnapshot row = Assert.Single(h.Combat.SnapshotRoomEngage());
        Assert.Equal(CombatManager.EngageAssessment.StuckOnMana, row.Assessment);
        Assert.Contains("mana 10 is under its reserve of 20", row.Why);
    }

    // In a party with the box ticked but mana under the spell's own cost: hold on the
    // monster (the walker stays, nothing is swung for "no effect"), and cast the
    // round mana covers the cost.
    [Fact]
    public void FireRegardlessOfMana_BelowTheSpellsCost_HoldsOnTheMonster_ThenCasts()
    {
        using Harness h = new();
        h.WieldFromGearSet("katana");
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "mmis", FireRegardlessOfMana = true };
        h.Ma = 1;
        h.JoinParty();

        h.Server.Line("Also here: bog wight.");

        Assert.True(h.Combat.CanEngageMonster(500));
        Assert.True(h.WalkerHeld);
        Assert.Empty(h.AllSent);
        Assert.Equal("bog wight", h.Combat.CurrentTarget);
        Assert.Contains(h.Logged, l => l.StartsWith("holding on bog wight", StringComparison.Ordinal));

        h.Tick();
        Assert.Empty(h.AllSent);

        h.Ma = 5;
        h.Tick();

        Assert.Equal(new[] { "mmis bog wight" }, h.AllSent);
        Assert.Null(h.Combat.ManaHoldTarget);
    }

    // A monster held for mana waits behind one we can hurt this round.
    [Fact]
    public void FireRegardlessOfMana_HeldMonster_GoesAfterOneWeCanHurtNow()
    {
        using Harness h = new();
        h.WieldFromGearSet("katana");
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "mmis", FireRegardlessOfMana = true };
        h.Ma = 1;
        h.JoinParty();

        h.Server.Line("Also here: bog wight, giant rat.");

        Assert.Equal(new[] { "a giant rat" }, h.AllSent);
    }

    // After an attack spell, `*Combat Engaged*` is what says we're engaged (and in
    // line for the kill's exp); a spell that drew "no effect" instead leaves us not
    // engaged, and that spell is out for the monster.
    [Fact]
    public void AttackSpellCast_OnlyCombatEngagedCountsAsEngaged()
    {
        using Harness engaged = new();
        engaged.WieldFromGearSet("katana");
        engaged.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "mmis" };
        engaged.Server.Line("Also here: bog wight.");
        Assert.Contains("mmis bog wight", engaged.AllSent);
        Assert.False(engaged.Combat.EngagedSinceLastAttack);
        engaged.Server.Line("*Combat Engaged*");
        Assert.True(engaged.Combat.EngagedSinceLastAttack);

        using Harness refused = new();
        refused.WieldFromGearSet("katana");
        refused.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "mmis" };
        refused.Server.Line("Also here: bog wight.");
        refused.Server.Line("Your spell has no effect on bog wight.");
        Assert.False(refused.Combat.EngagedSinceLastAttack);
        Assert.False(refused.Combat.CanEngageMonster(500));
    }
}

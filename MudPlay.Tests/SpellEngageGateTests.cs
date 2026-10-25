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
        // Per-monster overlays (Game Data), by monster number.
        public Dictionary<int, MonsterOverlay> Overlays { get; } = new();
        private MonsterOverlay Overlay(int number) => Overlays.TryGetValue(number, out MonsterOverlay? o) ? o : new MonsterOverlay();

        // classNumber 12 is a Mage, 13 a Druid, 14 a Priest; 0 no class seen yet.
        public Harness(int classNumber = 12, int level = 20)
        {
            _root = Path.Combine(Path.GetTempPath(), "mudplay-spell-engage-" + Path.GetRandomFileName());
            WriteGameData(Path.Combine(_root, "alpha"));
            GameDataCache cache = new(_root);
            cache.SwitchSet("alpha");

            DefaultPatterns.Seed(Router);
            Server = new ServerFeed(Router, () => Clock.Now);
            Log.Diagnostics = new LogDiagnosticState { CombatDiagnostics = true };
            Log.EntryAdded += e => Logged.Add(e.Message);
            PartyManager = new PartyManager(Router, Party, Log, now: () => Clock.Now);
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, Log, now: () => Clock.Now);
            // Built ahead of the combat manager, as AppServices builds it, so it reads
            // each room before the combat manager does.
            Coordinator = new MovementCoordinator(Log);
            Tracker = new CombatStateTracker(Router, Coordinator, Classifier, Monsters, State,
                () => true, resolveOverlay: Overlay, log: Log, clock: () => Clock.Now);
            Cast = new CastCoordinator(Router, Log, now: () => Clock.Now);
            Cast.SetWireSender(b => Sent.Add(b));
            Spellbook = new SpellbookState(new KnownSpellCatalog(cache));
            Spellbook.Refresh(classNumber, level);
            Combat = new CombatManager(Router, Classifier, Monsters,
                resolveOverlay: Overlay,
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
            // The spell indexes judge a shared cast-code by the spell book's row, as
            // AppServices wires them.
            Func<string, int?> casterSpell = code => Spellbook.FindByCastCode(code)?.Number;
            SpellReqLevelIndex reqLevel = new(cache);
            SpellAttackTypeIndex attackType = new(cache);
            SpellTargetTypeIndex targetType = new(cache);
            reqLevel.SetCasterSpell(casterSpell);
            attackType.SetCasterSpell(casterSpell);
            targetType.SetCasterSpell(casterSpell);
            Combat.SetMagicEligibility(new MonsterMagicIndex(cache), new ItemMagicIndex(cache),
                reqLevel, new MonsterResistIndex(cache), attackType);
            Combat.SetDrainEligibility(new MonsterLifeIndex(cache), targetType);
            Combat.SetWeaponActuator((_, _, _) => { }, readWornWeapon: () => WornWeapon);
            Combat.SetCarriedCheck(_ => true);
            Tracker.SetActionabilityGate(n => Combat.CanEngageMonster(n));

            AddMonster(500, "bog wight");
            AddMonster(501, "marsh hag");
            AddMonster(502, "giant rat");
            AddMonster(503, "stone warden");
            AddMonster(504, "acid slime");
            AddMonster(505, "iron golem");
            AddMonster(506, "lich lord");
            AddMonster(507, "flame wraith");
            AddMonster(508, "manscorpion shaman");
            AddMonster(509, "risen corpse");
        }

        // A katana with hit magic 3; monsters needing 4 (the rat, slime and golem need
        // none; the warden's and golem's spell immunity is 5, the lich's 20, and the
        // wraith resists fire 100%); a Mage's magic missile (level 1, cost 3), dancing
        // blades (a room attack, level 11, cost 26), turn undead (undead only) and
        // fireball (fire, cost 4) and holy wave (an undead-only room attack). Anomaly's
        // shared `blaz`: a Druid's blaze (fire, level 20), a Priest's blazing light
        // (undead only, level 56) and an item-cast blazing inferno; stone bolt (level 15,
        // any class). The manscorpion shaman is living, the risen corpse undead; both need
        // magic 4 and have spell immunity 15.
        private static void WriteGameData(string dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Classes.json"), """
                [ { "Number": 12, "Name": "Mage", "MageryType": 1, "MageryLVL": 3, "ArmourType": 1, "CombatLVL": 3 },
                  { "Number": 13, "Name": "Druid", "MageryType": 3, "MageryLVL": 3, "ArmourType": 1, "CombatLVL": 3 },
                  { "Number": 14, "Name": "Priest", "MageryType": 2, "MageryLVL": 3, "ArmourType": 1, "CombatLVL": 3 } ]
                """);
            File.WriteAllText(Path.Combine(dir, "Items.json"), """
                [ { "Number": 1, "Name": "katana", "Abil-0": 28, "AbilVal-0": 3 } ]
                """);
            File.WriteAllText(Path.Combine(dir, "Monsters.json"), """
                [ { "Number": 500, "Name": "bog wight", "Abil-0": 28, "AbilVal-0": 4 },
                  { "Number": 501, "Name": "marsh hag", "Abil-0": 28, "AbilVal-0": 4 },
                  { "Number": 502, "Name": "giant rat" },
                  { "Number": 503, "Name": "stone warden", "Abil-0": 28, "AbilVal-0": 4, "Abil-1": 139, "AbilVal-1": 5 },
                  { "Number": 504, "Name": "acid slime" },
                  { "Number": 505, "Name": "iron golem", "Abil-0": 139, "AbilVal-0": 5 },
                  { "Number": 506, "Name": "lich lord", "Abil-0": 28, "AbilVal-0": 4, "Abil-1": 139, "AbilVal-1": 20 },
                  { "Number": 507, "Name": "flame wraith", "Abil-0": 28, "AbilVal-0": 4, "Abil-1": 5, "AbilVal-1": 100 },
                  { "Number": 508, "Name": "manscorpion shaman", "Abil-0": 28, "AbilVal-0": 4, "Abil-1": 139, "AbilVal-1": 15 },
                  { "Number": 509, "Name": "risen corpse", "Undead": 1, "Abil-0": 28, "AbilVal-0": 4, "Abil-1": 139, "AbilVal-1": 15 } ]
                """);
            File.WriteAllText(Path.Combine(dir, "Spells.json"), """
                [ { "Number": 1, "Name": "magic missile", "Short": "mmis", "ReqLevel": 1, "ManaCost": 3,
                    "Targets": 8, "AttType": 4, "Magery": 1, "MageryLVL": 1, "Learnable": 1, "Abil-0": 17 },
                  { "Number": 133, "Name": "dancing blades", "Short": "blad", "ReqLevel": 11, "ManaCost": 26,
                    "Targets": 12, "AttType": 4, "Magery": 1, "MageryLVL": 1, "Learnable": 1, "Abil-0": 17 },
                  { "Number": 40, "Name": "turn undead", "Short": "turn", "ReqLevel": 3, "ManaCost": 5,
                    "Targets": 8, "AttType": 4, "Magery": 1, "MageryLVL": 1, "Learnable": 1, "Abil-0": 23 },
                  { "Number": 3, "Name": "fireball", "Short": "fbal", "ReqLevel": 1, "ManaCost": 4,
                    "Targets": 8, "AttType": 1, "Magery": 1, "MageryLVL": 1, "Learnable": 1, "Abil-0": 17 },
                  { "Number": 41, "Name": "holy wave", "Short": "hwav", "ReqLevel": 3, "ManaCost": 10,
                    "Targets": 12, "AttType": 4, "Magery": 1, "MageryLVL": 1, "Learnable": 1, "Abil-0": 23 },
                  { "Number": 290, "Name": "blaze", "Short": "blaz", "ReqLevel": 20, "ManaCost": 8,
                    "Targets": 8, "AttType": 1, "Magery": 3, "MageryLVL": 1, "Learnable": 1, "Abil-0": 17 },
                  { "Number": 5585, "Name": "blazing light", "Short": "blaz", "ReqLevel": 56, "ManaCost": 20,
                    "Targets": 8, "AttType": 4, "Magery": 2, "MageryLVL": 1, "Learnable": 1, "Abil-0": 17, "Abil-1": 23 },
                  { "Number": 159, "Name": "blazing inferno", "Short": "blaz", "ReqLevel": 0, "ManaCost": 0,
                    "Targets": 8, "AttType": 1, "Magery": 0, "Learnable": 0, "Abil-0": 17 },
                  { "Number": 6, "Name": "stone bolt", "Short": "ston", "ReqLevel": 15, "ManaCost": 5,
                    "Targets": 8, "AttType": 2, "Magery": 0, "Learnable": 1, "Abil-0": 17 } ]
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

    // A spell attack command with a plain alternate (`mmis`, then `a`): the
    // alternate swings the weapon, a different attack. A monster the spell can't
    // touch but the weapon can is fought, never walked past. Where game data can't
    // tell (the slime), `mmis` goes first and `a` follows its "no effect" (report
    // paradigm-20260809-131642's fallback); where it can (the golem's spell
    // immunity 5 is over mmis's level 1), `a` goes straight out.
    [Fact]
    public void SpellCommandWithAPlainAlternate_MonsterOnlyTheWeaponHurts_IsFoughtWithTheAlternate()
    {
        using Harness slime = new();
        slime.WieldFromGearSet("katana");
        slime.Settings.NormalAttackCommand = "mmis";
        slime.Settings.AlternateAttackCommand = "a";
        slime.Server.Line("Also here: acid slime.");
        Assert.Equal(new[] { "mmis acid slime" }, slime.AllSent);

        slime.Server.Line("Your spell has no effect on acid slime.");

        Assert.Equal(new[] { "mmis acid slime", "a acid slime" }, slime.AllSent);
        Assert.True(slime.Combat.CanEngageMonster(504));
        Assert.True(slime.WalkerHeld);
        Assert.Null(slime.CannotAttackLine);

        using Harness golem = new();
        golem.WieldFromGearSet("katana");
        golem.Settings.NormalAttackCommand = "mmis";
        golem.Settings.AlternateAttackCommand = "a";
        golem.Server.Line("Also here: iron golem.");

        Assert.Equal(new[] { "a iron golem" }, golem.AllSent);
        Assert.True(golem.Combat.CanEngageMonster(505));
        Assert.True(golem.WalkerHeld);
        Assert.Null(golem.CannotAttackLine);
    }

    // A spell typed after `cast` or `c` is a spell command too.
    [Theory]
    [InlineData("cast mmis")]
    [InlineData("c mmis")]
    public void SpellCommandAfterCast_IsJudgedAsTheSpell(string command)
    {
        using Harness h = new();
        h.WieldFromGearSet("katana");
        h.Settings.NormalAttackCommand = command;

        h.Server.Line("Also here: bog wight.");

        Assert.True(h.Combat.CanEngageMonster(500));
        Assert.Equal(new[] { $"{command} bog wight" }, h.AllSent);
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

    // A room attack engages the caster with the whole room, whatever one monster's
    // spell immunity: the lich's 20 is over dancing blades' level 11, and it's
    // still cast and the lich still fought.
    [Fact]
    public void RoomAttack_MonsterSpellImmuneToIt_IsStillEngaged()
    {
        using Harness h = new();
        h.WieldFromGearSet("katana");
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blad", MinEnemies = 1 };

        h.Server.Line("Also here: lich lord.");

        Assert.True(h.Combat.CanEngageMonster(506));
        Assert.True(h.WalkerHeld);
        Assert.Equal(new[] { "blad" }, h.AllSent);
    }

    // A spell the monster resists outright still engages it, for no damage. In a
    // party that is what earns the kill's exp, so it's cast; solo nobody else would
    // kill it, so it's walked past; and a spell that does damage goes first.
    [Theory]
    [InlineData(true, null, "fbal flame wraith")]
    [InlineData(false, null, null)]
    [InlineData(true, "mmis", "mmis flame wraith")]
    public void SpellTheMonsterResistsOutright_EngagesInAParty(bool partied, string? alternate, string? sent)
    {
        using Harness h = new();
        h.WieldFromGearSet("katana");
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "fbal" };
        if (alternate is not null) h.Settings.AlternateAttackSpell = new CombatSpellSlot { SpellName = alternate };
        if (partied) h.JoinParty();

        h.Server.Line("Also here: flame wraith.");

        Assert.Equal(sent is not null, h.Combat.CanEngageMonster(507));
        Assert.Equal(sent is null ? Array.Empty<string>() : new[] { sent }, h.AllSent);
        if (sent is null) Assert.Contains("the monster resists its element", h.CannotAttackLine);
    }

    // The same for a spell typed as the attack command.
    [Fact]
    public void SpellCommandTheMonsterResistsOutright_EngagesInAParty()
    {
        using Harness h = new();
        h.WieldFromGearSet("katana");
        h.Settings.NormalAttackCommand = "fbal";
        h.JoinParty();

        h.Server.Line("Also here: flame wraith.");

        Assert.True(h.Combat.CanEngageMonster(507));
        Assert.Equal(new[] { "fbal flame wraith" }, h.AllSent);
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

    // "Fire regardless of current mana": in a party the slot asks nothing of mana
    // and casts; solo, or with the box clear, the reserve holds and the monster is
    // left for now (a mana stall, not a write-off).
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

    // Ticked, in a party, with mana under the spell's own cost (even none): the cast
    // still goes out, and the game's `*Combat Engaged*` engages us. A refusal for mana
    // latches no cast block, keeps the target and the rung, and the spell goes again
    // the next round, once a round.
    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void FireRegardlessOfMana_BelowTheSpellsCost_StillCasts_AndARefusalDoesntStopTheNext(int mana)
    {
        using Harness h = new();
        h.WieldFromGearSet("katana");
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "mmis", FireRegardlessOfMana = true };
        h.Ma = mana;
        h.JoinParty();

        h.Server.Line("Also here: bog wight.");
        Assert.True(h.Combat.CanEngageMonster(500));
        Assert.Equal(new[] { "mmis bog wight" }, h.AllSent);
        h.Server.Line("*Combat Engaged*");
        Assert.True(h.Combat.EngagedSinceLastAttack);

        h.Server.Line("You do not have enough mana to cast that spell.");
        Assert.Contains(h.Logged, l => l.Contains("no cast block latched", StringComparison.Ordinal));
        Assert.Equal("bog wight", h.Combat.CurrentTarget);
        Assert.Equal(new[] { "mmis bog wight" }, h.AllSent);   // nothing re-sent in the same round

        h.Tick();

        Assert.Equal(new[] { "mmis bog wight", "mmis bog wight" }, h.AllSent);
        Assert.True(h.WalkerHeld);
    }

    // Unticked, a refusal for mana still latches the round's cast block as before.
    [Fact]
    public void NoManaRefusal_OnAnUntickedSlot_LatchesAsBefore()
    {
        using Harness h = new();
        h.WieldFromGearSet("katana");
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "mmis" };
        h.JoinParty();
        h.Server.Line("Also here: bog wight.");

        h.Server.Line("You do not have enough mana to cast that spell.");

        Assert.Contains(h.Logged, l => l.StartsWith("cast failed reason=NotEnoughMana insufficient-mana", StringComparison.Ordinal));
    }

    // Ticked, the slot's Max casts still caps it: 0 is never.
    [Fact]
    public void FireRegardlessOfMana_IsStillCappedByMaxCasts()
    {
        using Harness h = new();
        h.WieldFromGearSet("katana");
        h.Settings.NormalAttackSpell = new CombatSpellSlot
        {
            SpellName = "mmis", FireRegardlessOfMana = true, MaxCastsPerRoom = 0,
        };
        h.Ma = 0;
        h.JoinParty();

        h.Server.Line("Also here: bog wight.");

        Assert.False(h.Combat.CanEngageMonster(500));
        Assert.Empty(h.AllSent);
    }

    // A target given up on once its cast cap ran out (nothing else hurts it) is not
    // picked again in the room, where its cap would read as a new target's and the
    // spell be cast again every cycle.
    [Fact]
    public void TargetGivenUpWhenItsCastCapRanOut_IsNotRePicked()
    {
        using Harness h = new();
        h.WieldFromGearSet("katana");
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "mmis", MaxCastsPerRoom = 1 };
        h.Server.Line("Also here: bog wight.");
        h.Server.Line("*Combat Engaged*");
        Assert.Equal(new[] { "mmis bog wight" }, h.AllSent);

        h.Tick();                                   // its one round tallies: the cap is spent
        Assert.Null(h.Combat.CurrentTarget);
        Assert.Contains("its 1 cast(s) at this target are spent", h.CannotAttackLine);

        h.Server.Line("Also here: bog wight.");     // the room re-read the give-up asked for

        Assert.Single(h.AllSent, s => s == "mmis bog wight");
        Assert.False(h.Combat.CanEngageMonster(500));
        Assert.False(h.WalkerHeld);
        Assert.Contains("bog wight", h.Combat.SnapshotGivenUp());
    }

    // A per-monster physical override is the normal side's command: with the spell
    // command under the monster's spell immunity, the override still hurts it, so it
    // isn't walked past.
    [Fact]
    public void PhysicalOverride_CountsAsTheNormalAttack_WhenTheSpellCommandCantLand()
    {
        using Harness h = new();
        h.WieldFromGearSet("katana");
        h.Settings.NormalAttackCommand = "mmis";
        h.Overlays[505] = new MonsterOverlay { OverridePhysicalCommand = "attack" };

        h.Server.Line("Also here: iron golem.");

        Assert.True(h.Combat.CanEngageMonster(505));
        Assert.True(h.WalkerHeld);
        Assert.Equal(new[] { "attack iron golem" }, h.AllSent);
    }

    // An undead-only room attack with no undead here damages nothing, but casting it
    // engages the caster with the room. In a party it's cast for that, the log says
    // so; solo nobody else would kill the monster, so it's walked past.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UndeadOnlyRoomAttack_NoUndeadHere_EngagesOnlyInAParty(bool partied)
    {
        using Harness h = new();
        h.WieldFromGearSet("katana");
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "hwav", MinEnemies = 1 };
        if (partied) h.JoinParty();

        h.Server.Line("Also here: bog wight.");

        Assert.Equal(partied, h.Combat.CanEngageMonster(500));
        Assert.Equal(partied ? new[] { "hwav" } : Array.Empty<string>(), h.AllSent);
        if (partied)
            Assert.Contains(h.Logged, l => l.StartsWith("casting hwav at bog wight for no damage", StringComparison.Ordinal));
        else
            Assert.Contains("it can damage nothing here", h.CannotAttackLine);
    }

    // After an attack spell, `*Combat Engaged*` is what says we're engaged (and in
    // line for the kill's exp); a spell that drew "no effect" instead leaves us not
    // engaged, and that spell is out for the monster. With the weapon unable to hurt
    // it either, nothing is swung at it: it's written off and the room re-read.
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
        // The cast, then only the room re-display: no swing of the katana.
        Assert.Equal(new[] { "mmis bog wight", "" }, refused.AllSent);
        Assert.Null(refused.Combat.CurrentTarget);
        Assert.Contains("attack spell mmis: drew \"no effect\" on this monster this room", refused.CannotAttackLine);
    }

    // Anomaly, 2026-10-24: a Druid with `blaz` as the attack spell and stone bolt as the
    // alternate. `blaz` is also a Priest's undead-only blazing light, and every row with
    // the code was read as one spell, so the Druid's blaze was skipped as unable to
    // affect a living manscorpion shaman and the alternate cast instead. The Druid casts
    // blaze; a Priest casts blazing light, which a living monster rules out and an undead
    // one doesn't; with no class seen yet the code is ambiguous and isn't pre-empted.
    [Theory]
    [InlineData(13, "manscorpion shaman", "blaz manscorpion shaman")]
    [InlineData(14, "manscorpion shaman", "ston manscorpion shaman")]
    [InlineData(14, "risen corpse", "blaz risen corpse")]
    [InlineData(0, "manscorpion shaman", "blaz manscorpion shaman")]
    public void SharedCastCode_IsJudgedByTheSpellTheCharactersClassCasts(int classNumber, string monster, string sent)
    {
        using Harness h = new(classNumber, level: 60);
        h.WieldFromGearSet("katana");
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "blaz" };
        h.Settings.AlternateAttackSpell = new CombatSpellSlot { SpellName = "ston" };

        h.Server.Line($"Also here: {monster}.");

        Assert.Equal(new[] { sent }, h.AllSent);
        Assert.True(h.WalkerHeld);
    }
}

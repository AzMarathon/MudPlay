using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Game.Simulation;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// Pins the character loop simulation: a lair refills only on its respawn clock —
// one per slot on Paradigm, one per room on Stock that every death in the room
// restarts — on entry, or on the spawn pass while standing in it (an unresolved
// clock only on entry). A fight the character can't survive ends the run and
// counts against the hours asked for, resting follows the Health-tab rest gates
// and the realm's rest-tick cycle, lawful evil spares an Outlaw-or-worse
// character, and a configured attack spell is cast by the chooser.
public sealed class LoopSimulatorTests
{
    private static MonsterCatalogEntry Mob(int number, int hp, int exp, int align = 3,
        params MonsterAttackSlot[] attacks) => new(
        number, $"mob{number}", Type: 0, Align: align, Undead: false, Exp: exp, ExpMulti: 1, RegenTime: 0,
        Hp: hp, HpRegen: 0, ArmourClass: 0, DamageResist: 0, MagicRes: 0, FollowPercent: 0, CharmLevel: 0,
        CashRunic: 0, CashPlatinum: 0, CashGold: 0, CashSilver: 0, CashCopper: 0, Weapon: 0, CreateSpell: 0,
        DeathSpell: 0, BsDefense: 0, Energy: 1000, AvgDamage: 0, Attacks: attacks,
        MidSpells: Array.Empty<MonsterMidSpellSlot>(), Drops: Array.Empty<MonsterDropSlot>(),
        Abilities: Array.Empty<MonsterAbilitySlot>(), ElementalResists: new Dictionary<int, int>(),
        Magical: 0, SpellImmunity: 0, Dodge: 0, NonLiving: false, CastsElements: Array.Empty<string>());

    private static MonsterAttackSlot Hit(int min, int max, int accuracy = 500, int energy = 1000) =>
        new("hit", Type: 1, Percent: 100, TruePercent: 100, MinDamage: min, MaxDamage: max,
            Accuracy: accuracy, Energy: energy, HitSpell: 0);

    private static SimCharacter Character(int maxHp = 200, int damage = 1000, double swings = 1,
        CombatSettings? combat = null, HealthSettings? health = null,
        IReadOnlyDictionary<string, SimSpell>? spells = null, int maxMana = 0,
        IReadOnlyList<SimBuff>? buffs = null, SpellsSettings? slots = null, SimRegen? regen = null,
        int alignment = 0, RealmType realm = RealmType.ParaMud) => new(
        realm, Level: 30, MaxHp: maxHp, MaxMana: maxMana,
        Melee: new PlayerMatchupProfile(RealmType.ParaMud, NormalAccuracy: 9999, AvgWeaponDamage: damage,
            SwingsPerRound: swings, HasWeapon: true, ArmourClass: 0, Dodge: 0, ProtEvil: 0, ProtGood: 0,
            DamageResist: 0),
        WeaponHitMagic: 10,
        Defense: new PlayerDefenseProfile(0, 0, 0, 0, false, 0, EvilLevel.Saint, 0, AcExact: 0),
        Regen: regen ?? new SimRegen(_ => 1, _ => 30, extra => extra / 10.0, 0, RealmRegenProfile.ParaMud, RestReplacesStanding: true),
        Spells: spells ?? new Dictionary<string, SimSpell>(),
        Combat: combat ?? new CombatSettings(),
        Health: health ?? new HealthSettings { UseMeditateAbility = false },
        SpellSlots: slots ?? new SpellsSettings(),
        Overlay: _ => new MonsterOverlay(),
        SpellShortByNumber: _ => null,
        AlignmentValue: alignment,
        Buffs: buffs);

    private static SimWorld World(params MonsterCatalogEntry[] mobs) => World(null, mobs);

    private static SimWorld World(Dictionary<int, IReadOnlyList<int>>? deathSummons, params MonsterCatalogEntry[] mobs)
    {
        var byNumber = mobs.ToDictionary(m => m.Number);
        return new SimWorld(n => byNumber.GetValueOrDefault(n),
            DeathSummons: n => deathSummons?.GetValueOrDefault(n));
    }

    private static SimSpell Spell(string code, int mana, long dmg = 0, double duration = 0,
        int manaRegenMin = 0, int manaRegenMax = 0, MonsterDebuffEffect debuff = default) =>
        new(code, 1, mana, FiresPerRound: 1, CastChance: 100, MinDamagePerRound: dmg, MaxDamagePerRound: dmg,
            UsesMagicResist: false, TypeOfResists: 0, AttType: 0, MinHeal: 0, MaxHeal: 0,
            DurationSeconds: duration, ManaRegenMin: manaRegenMin, ManaRegenMax: manaRegenMax, Debuff: debuff);

    private static SimRoom Lair(int room, int max, int respawn, params int[] monsters) =>
        new(new RoomKey(1, room), 0, max, monsters, respawn);

    private static SimRoom Empty(int room) => new(new RoomKey(1, room), 0, 0, Array.Empty<int>(), 0);

    // A sneaking backstabber: a stab that always lands and kills in one blow.
    private static SimCharacter Stabber(int stab = 500) => Character(maxHp: 100, damage: 1) with
    {
        Backstab = new PlayerMatchupProfile(RealmType.ParaMud, NormalAccuracy: 9999, AvgWeaponDamage: 1,
            SwingsPerRound: 1, HasWeapon: true, ArmourClass: 0, Dodge: 0, ProtEvil: 0, ProtGood: 0,
            DamageResist: 0, BackstabMin: stab, BackstabMax: stab),
        BackstabHitMagic = 10,
        Combat = new CombatSettings { DoBackstab = true },
    };

    // Sneaking in, the surprise stab kills a hostile before it swings; the same
    // character without the opener slugs it out and takes its hits (report
    // paradigm-20260930-114254).
    [Fact]
    public void SneakOpenerKillsBeforeTheMonsterSwings()
    {
        SimRoom[] lap = { Lair(1, 1, 30, 3), Empty(2), Empty(3) };
        SimWorld world = World(Mob(3, hp: 100, exp: 50, align: 1, Hit(10, 10)));

        LoopSimRun stab = LoopSimulator.Run(Stabber(), lap, world, secondsPerStep: 1, hours: 1, seed: 1);
        LoopSimRun slug = LoopSimulator.Run(Stabber() with { Backstab = null }, lap, world, secondsPerStep: 1, hours: 1, seed: 1);

        Assert.True(stab.Kills > 0);
        Assert.Equal(stab.Kills, stab.Casts.GetValueOrDefault("bs"));
        Assert.Equal(0, stab.DamageTaken);
        Assert.True(slug.DamageTaken > 0);
        Assert.True(stab.Kills > slug.Kills);
    }

    // A see-hidden monster spots the sneak: no surprise stab on it.
    [Fact]
    public void SeeHiddenMonsterGetsNoStab()
    {
        MonsterCatalogEntry sees = Mob(3, hp: 100, exp: 50, align: 1, Hit(1, 1)) with
        {
            Abilities = new[] { new MonsterAbilitySlot(57, 1) },
        };
        LoopSimRun run = LoopSimulator.Run(Stabber(), new[] { Lair(1, 1, 30, 3), Empty(2) }, World(sees),
            secondsPerStep: 1, hours: 1, seed: 1);

        Assert.True(run.Kills > 0);
        Assert.Equal(0, run.Casts.GetValueOrDefault("bs"));
    }

    // A stab that doesn't kill is the whole surprise round; the fight goes on with
    // the usual attack, and the next stab waits for the next sneaked entry.
    [Fact]
    public void StabThatDoesNotKillIsFollowedByTheUsualAttack()
    {
        LoopSimRun run = LoopSimulator.Run(Stabber(stab: 60) with { Melee = Stabber().Melee with { AvgWeaponDamage = 50 } },
            new[] { Lair(1, 1, 30, 3), Empty(2) }, World(Mob(3, hp: 100, exp: 50)), secondsPerStep: 1, hours: 1, seed: 1);

        Assert.True(run.Kills > 0);
        Assert.Equal(run.Kills, run.Casts.GetValueOrDefault("bs"));
        Assert.True(run.Casts.GetValueOrDefault(new CombatSettings().NormalAttackCommand) >= run.Kills);
    }

    [Fact]
    public void LairRefillsOnlyOnItsRespawnClock()
    {
        // One 2-mob lair on a 60 s clock, a tiny lap and a one-shot kill: the lap is
        // far faster than the respawn, so kills are capped near 2 × 60 per hour. A
        // respawned mob still dies only on the next 5 s round, so each cycle runs a
        // few seconds past 60 — never over the cap, never far under it.
        LoopSimRun run = LoopSimulator.Run(Character(), new[] { Lair(1, 2, 60, 7), Empty(2) },
            World(Mob(7, hp: 10, exp: 100)), secondsPerStep: 1, hours: 1, seed: 1);

        Assert.InRange(run.Kills, 2 * 3600 / 65, 2 + 2 * 60);
        Assert.Equal(run.Kills * 100L, run.Exp);
    }

    [Fact]
    public void FightTheCharacterCannotSurviveEndsTheRun()
    {
        // A hostile monster that can't be killed fast and hits for more than the
        // character's whole pool in a round: the first round it swings is a death.
        SimCharacter weak = Character(maxHp: 50, damage: 1);
        LoopSimRun run = LoopSimulator.Run(weak, new[] { Lair(1, 1, 60, 9), Empty(2) },
            World(Mob(9, hp: 100000, exp: 1, align: 1, Hit(500, 500))), secondsPerStep: 1, hours: 1, seed: 1);

        Assert.NotNull(run.DiedAtSeconds);
        Assert.True(run.Seconds < 60);
    }

    [Fact]
    public void RestsBelowTheRestTriggerUntilTheRestTarget()
    {
        // A hostile monster that lands 40 a swing and dies in one round: every kill
        // leaves the character well under the 60% rest trigger, so it rests.
        var health = new HealthSettings { RestIfBelowHp = 60, RestMaxHp = 95, UseMeditateAbility = false };
        LoopSimRun run = LoopSimulator.Run(Character(maxHp: 100, damage: 5, health: health),
            new[] { Lair(1, 1, 30, 3), Empty(2) },
            World(Mob(3, hp: 10, exp: 50, align: 1, Hit(40, 40))), secondsPerStep: 1, hours: 1, seed: 1);

        Assert.Null(run.DiedAtSeconds);
        Assert.True(run.RestingSeconds > 0);
        Assert.True(run.Kills > 0);
    }

    [Fact]
    public void ConfiguredAttackSpellIsCastInsteadOfTheWeapon()
    {
        var combat = new CombatSettings();
        combat.NormalAttackSpell.SpellName = "blst";
        var spells = new Dictionary<string, SimSpell>(StringComparer.OrdinalIgnoreCase)
        {
            ["blst"] = new SimSpell("blst", 1, ManaPerCast: 5, FiresPerRound: 1, CastChance: 100,
                MinDamagePerRound: 500, MaxDamagePerRound: 500, UsesMagicResist: false, TypeOfResists: 0,
                AttType: 0, MinHeal: 0, MaxHeal: 0),
        };
        LoopSimRun run = LoopSimulator.Run(Character(damage: 0, combat: combat, spells: spells, maxMana: 1000),
            new[] { Lair(1, 1, 60, 5), Empty(2) }, World(Mob(5, hp: 100, exp: 10)),
            secondsPerStep: 1, hours: 0.5, seed: 1);

        Assert.True(run.Casts.GetValueOrDefault("blst") > 0);
        Assert.Equal(run.Casts["blst"], run.Kills);
    }

    [Fact]
    public void RollBuffIsKeptUpAndRerolledBelowItsThreshold()
    {
        // A mana-regen roll spell (0–100% rolled) kept up solo, rerolled while it lands
        // under 50: it gets cast more often than its 60 s duration alone would need.
        var spells = new Dictionary<string, SimSpell>(StringComparer.OrdinalIgnoreCase)
        {
            ["prfl"] = Spell("prfl", mana: 1, duration: 60, manaRegenMin: 0, manaRegenMax: 100),
        };
        var buffs = new[] { new SimBuff("prfl", 0, false, false, false, RerollBelow: 50, RerollCount: 0, RerollInfinite: true) };
        var health = new HealthSettings { BlessIfAboveMa = 0, RestIfBelowMa = 0, UseMeditateAbility = false };
        LoopSimRun run = LoopSimulator.Run(
            Character(spells: spells, buffs: buffs, maxMana: 1000, health: health),
            new[] { Empty(1), Empty(2) }, World(), secondsPerStep: 1, hours: 1, seed: 1);

        int casts = run.Casts.GetValueOrDefault("prfl");
        Assert.True(casts > 60, $"cast {casts} times");
    }

    [Fact]
    public void DeathSpellSummonsTheNextTier()
    {
        // Killing the stitched parent summons two parts; the kills and exp include them.
        var summons = new Dictionary<int, IReadOnlyList<int>> { [20] = new[] { 21, 21 } };
        LoopSimRun run = LoopSimulator.Run(Character(), new[] { Lair(1, 1, 3600, 20), Empty(2) },
            World(summons, Mob(20, hp: 10, exp: 100), Mob(21, hp: 10, exp: 10)), secondsPerStep: 1, hours: 0.5, seed: 1);

        Assert.Equal(3, run.Kills);
        Assert.Equal(120, run.Exp);
    }

    [Fact]
    public void SingleTargetDebuffLandsBeforeTheKill()
    {
        var combat = new CombatSettings();
        combat.SingleTargetDebuffSpell.SpellName = "curs";
        var spells = new Dictionary<string, SimSpell>(StringComparer.OrdinalIgnoreCase)
        {
            ["curs"] = Spell("curs", mana: 5, duration: 60, debuff: new MonsterDebuffEffect(20, 0, 0, 0, false)),
        };
        LoopSimRun run = LoopSimulator.Run(Character(damage: 1, combat: combat, spells: spells, maxMana: 500),
            new[] { Lair(1, 1, 3600, 6), Empty(2) }, World(Mob(6, hp: 30, exp: 10)), secondsPerStep: 1, hours: 0.5, seed: 1);

        Assert.Equal(1, run.Casts.GetValueOrDefault("curs"));
    }

    [Fact]
    public void RunTriggerFleesRestsAndComesBack()
    {
        // Under 50% HP the character runs, rests to full away from the fight, and
        // walks back to finish it — no death.
        var health = new HealthSettings { RunIfBelowHp = 50, RestIfBelowHp = 60, RestMaxHp = 100, UseMeditateAbility = false };
        var combat = new CombatSettings { RunDistance = 1 };
        LoopSimRun run = LoopSimulator.Run(Character(maxHp: 100, damage: 20, health: health, combat: combat),
            new[] { Lair(1, 1, 3600, 8), Empty(2) }, World(Mob(8, hp: 200, exp: 10, align: 1, Hit(30, 30))),
            secondsPerStep: 1, hours: 1, seed: 1);

        Assert.True(run.Flees > 0);
        Assert.Null(run.DiedAtSeconds);
        Assert.True(run.Kills >= 1);
    }

    [Fact]
    public void LandedHitFiresItsHitSpellAndHoldsTheCharacter()
    {
        // A hostile that always lands a 20-damage hit carrying a 60 s hold proc: the
        // first landing holds the character, the later ones (no higher roll) don't
        // extend it, and the hold keeps the character in the room after the ~50 s
        // fight until it wears off.
        var slot = new MonsterAttackSlot("tramples", Type: 1, Percent: 100, TruePercent: 100, MinDamage: 20, MaxDamage: 20,
            Accuracy: 9999, Energy: 1000, HitSpell: 318);
        var mobs = new Dictionary<int, MonsterCatalogEntry> { [4] = Mob(4, hp: 2000, exp: 10, align: 1, slot) };
        var proc = new SimProc(0, 0, 60, AcDelta: -10, DodgeDelta: -20, AccuracyDelta: -5, Holds: true);
        var world = new SimWorld(n => mobs.GetValueOrDefault(n), HitSpell: n => n == 318 ? proc : null);
        LoopSimRun run = LoopSimulator.Run(Character(maxHp: 5000, damage: 200), new[] { Lair(1, 1, 3600, 4), Empty(2) },
            world, secondsPerStep: 1, hours: 0.25, seed: 1);

        Assert.Equal(1, run.Kills);
        Assert.True(run.DamageTaken >= 20 * 9, $"took {run.DamageTaken}");   // ≥ 9 rounds of hits before the kill
        Assert.True(run.WaitingSeconds >= 5, $"waited {run.WaitingSeconds}");  // held after the kill
    }

    [Fact]
    public void BurningHitSpellTicksEvery3SecondsForItsDuration()
    {
        // One landed hit sets a 30 s burn of 7 every 3 s: no damage on the hit, then
        // ~10 ticks. The monster survives one round (one chomp lands) and dies on the
        // next before it swings again, so that one burn is all the damage there is.
        var slot = new MonsterAttackSlot("chomps", Type: 1, Percent: 100, TruePercent: 100, MinDamage: 0, MaxDamage: 0,
            Accuracy: 9999, Energy: 1000, HitSpell: 884);
        var mobs = new Dictionary<int, MonsterCatalogEntry> { [5] = Mob(5, hp: 1500, exp: 10, align: 1, slot) };
        var burn = new SimProc(7, 7, 30, 0, 0, 0, Holds: false);
        var world = new SimWorld(n => mobs.GetValueOrDefault(n), HitSpell: n => n == 884 ? burn : null);
        LoopSimRun run = LoopSimulator.Run(Character(maxHp: 5000), new[] { Lair(1, 1, 3600, 5), Empty(2) },
            world, secondsPerStep: 1, hours: 0.1, seed: 1);

        Assert.Equal(1, run.Kills);
        Assert.InRange(run.DamageTaken, 7 * 9, 7 * 10);
    }

    [Fact]
    public void BurnIsNotRefreshedByAnEqualRoll()
    {
        // A 30 s burn of a fixed 7 lands on the first round and again on the next two
        // before the kill. An equal roll doesn't refresh a slot, so the burn still
        // dies down 30 s after the FIRST landing: ~10 ticks, not the ~13 a refresh
        // on the last landing would give.
        var slot = new MonsterAttackSlot("chomps", Type: 1, Percent: 100, TruePercent: 100, MinDamage: 0, MaxDamage: 0,
            Accuracy: 9999, Energy: 1000, HitSpell: 884);
        var mobs = new Dictionary<int, MonsterCatalogEntry> { [5] = Mob(5, hp: 3500, exp: 10, align: 1, slot) };
        var burn = new SimProc(7, 7, 30, 0, 0, 0, Holds: false);
        var world = new SimWorld(n => mobs.GetValueOrDefault(n), HitSpell: n => n == 884 ? burn : null);
        LoopSimRun run = LoopSimulator.Run(Character(maxHp: 5000), new[] { Lair(1, 1, 3600, 5), Empty(2) },
            world, secondsPerStep: 1, hours: 0.1, seed: 1);

        Assert.Equal(1, run.Kills);
        Assert.InRange(run.DamageTaken, 7 * 9, 7 * 10);
    }

    [Fact]
    public void DifferentBurnsStack()
    {
        // Two attacks, each carrying its own 30 s burn of 7: both slots tick side by side.
        var bite = new MonsterAttackSlot("bites", Type: 1, Percent: 50, TruePercent: 50, MinDamage: 0, MaxDamage: 0,
            Accuracy: 9999, Energy: 500, HitSpell: 884);
        var claw = bite with { Name = "claws", HitSpell = 885 };
        var mobs = new Dictionary<int, MonsterCatalogEntry> { [5] = Mob(5, hp: 3500, exp: 10, align: 1, bite, claw) };
        var burn = new SimProc(7, 7, 30, 0, 0, 0, Holds: false);
        var world = new SimWorld(n => mobs.GetValueOrDefault(n), HitSpell: n => n is 884 or 885 ? burn : null);
        LoopSimRun run = LoopSimulator.Run(Character(maxHp: 5000), new[] { Lair(1, 1, 3600, 5), Empty(2) },
            world, secondsPerStep: 1, hours: 0.1, seed: 1);

        Assert.True(run.DamageTaken > 7 * 10, $"took {run.DamageTaken}");
    }

    [Fact]
    public void DeathSummonThatDoesNotFitUnderTheCapFailsWhole()
    {
        // A full room of 20 summoners, each summoning three parts on death. A cast
        // lands only when all three fit under the 20-monster cap (every third kill),
        // never one or two of it: the parts come in whole threes, fewer than 20.
        var summons = new Dictionary<int, IReadOnlyList<int>> { [30] = new[] { 31, 31, 31 } };
        LoopSimRun run = LoopSimulator.Run(Character(), new[] { Lair(1, 20, 3600, 30), Empty(2) },
            World(summons, Mob(30, hp: 10, exp: 100), Mob(31, hp: 10, exp: 1)), secondsPerStep: 1, hours: 0.25, seed: 1);

        long parts = run.Exp - 20 * 100;
        Assert.Equal(20 + parts, run.Kills);
        Assert.True(parts > 0 && parts % 3 == 0 && parts < 20, $"{parts} parts");
    }

    [Fact]
    public void HeldCharacterCannotFlee()
    {
        // Every landed hit holds the character for the whole run: however low HP gets,
        // the run trigger can't walk it out.
        var slot = new MonsterAttackSlot("tramples", Type: 1, Percent: 100, TruePercent: 100, MinDamage: 10, MaxDamage: 10,
            Accuracy: 9999, Energy: 1000, HitSpell: 318);
        var mobs = new Dictionary<int, MonsterCatalogEntry> { [4] = Mob(4, hp: 100000, exp: 10, align: 1, slot) };
        var hold = new SimProc(0, 0, 100000, 0, 0, 0, Holds: true);
        var world = new SimWorld(n => mobs.GetValueOrDefault(n), HitSpell: n => n == 318 ? hold : null);
        var health = new HealthSettings { RunIfBelowHp = 50, UseMeditateAbility = false };
        LoopSimRun run = LoopSimulator.Run(Character(maxHp: 100, damage: 1, health: health, combat: new CombatSettings { RunDistance = 1 }),
            new[] { Lair(1, 1, 3600, 4), Empty(2) }, world, secondsPerStep: 1, hours: 0.25, seed: 1);

        Assert.Equal(0, run.Flees);
    }

    [Fact]
    public void RunTriggerFiresAtTheThreshold()
    {
        // HealthManager runs at or below the trigger: one 50-damage hit on a 100-HP
        // character sits exactly on a 50% run trigger and flees rather than taking a
        // second hit to 0.
        var health = new HealthSettings { RunIfBelowHp = 50, RestIfBelowHp = 60, RestMaxHp = 100, UseMeditateAbility = false };
        LoopSimRun run = LoopSimulator.Run(Character(maxHp: 100, damage: 1, health: health, combat: new CombatSettings { RunDistance = 1 }),
            new[] { Lair(1, 1, 3600, 8), Empty(2) }, World(Mob(8, hp: 100000, exp: 10, align: 1, Hit(50, 50, accuracy: 9999))),
            secondsPerStep: 1, hours: 0.25, seed: 1);

        Assert.True(run.Flees > 0);
    }

    [Fact]
    public void RecastDebuffDoesNotStackOnTheSameMonster()
    {
        // A 50-DR monster wears down a 100-HP character in two rounds, so it flees and
        // comes back again and again; each return re-casts the single-target debuff
        // (the chooser starts the room afresh). The debuff strips 25 DR once: 50-damage
        // hits deal 25 each, a slow kill with many flees. Stacked, the second cast
        // would strip the DR to 0 and end the fight in a few more hits.
        var combat = new CombatSettings { RunDistance = 1 };
        combat.SingleTargetDebuffSpell.SpellName = "curs";
        var spells = new Dictionary<string, SimSpell>(StringComparer.OrdinalIgnoreCase)
        {
            ["curs"] = Spell("curs", mana: 1, duration: 600, debuff: new MonsterDebuffEffect(0, 25, 0, 0, false)),
        };
        var health = new HealthSettings { RunIfBelowHp = 50, RestIfBelowHp = 60, RestMaxHp = 100, UseMeditateAbility = false };
        MonsterCatalogEntry tank = Mob(8, hp: 500, exp: 10, align: 1, Hit(30, 30, accuracy: 9999)) with { DamageResist = 50 };
        LoopSimRun run = LoopSimulator.Run(
            Character(maxHp: 100, damage: 50, health: health, combat: combat, spells: spells, maxMana: 1000),
            new[] { Lair(1, 1, 3600, 8), Empty(2) }, World(tank), secondsPerStep: 1, hours: 0.5, seed: 1);

        Assert.True(run.Casts.GetValueOrDefault("curs") >= 2, $"cast {run.Casts.GetValueOrDefault("curs")} times");
        Assert.True(run.Flees >= 8, $"fled {run.Flees} times");
    }

    [Fact]
    public void LairWithAnUnresolvedTimerRefillsOnlyOnEntry()
    {
        // RespawnSeconds 0 (no timer resolved): checked while standing it would refill
        // the instant each kill freed the slot and pin the character in the room.
        // Refilled on entry only, the loop keeps walking — one kill per visit.
        LoopSimRun run = LoopSimulator.Run(Character(), new[] { Lair(1, 1, 0, 7), Empty(2) },
            World(Mob(7, hp: 10, exp: 100)), secondsPerStep: 1, hours: 1, seed: 1);

        Assert.True(run.Laps > 100);
        Assert.InRange(run.Kills, 1, run.Laps + 1);
    }

    [Fact]
    public void LairRefillsUnderACharacterStandingInIt()
    {
        // A character that rests forever (no regen, rest target never reached) stays
        // in the lair room; the lair still comes back on the spawn pass after its 60 s
        // clock, a few seconds late — never faster than the clock.
        var still = new SimRegen(_ => 0, _ => 0, _ => 0, 0, RealmRegenProfile.ParaMud, RestReplacesStanding: true);
        var health = new HealthSettings { RestIfBelowHp = 100, RestMaxHp = 100, UseMeditateAbility = false };
        LoopSimRun run = LoopSimulator.Run(Character(maxHp: 100000, health: health, regen: still),
            new[] { Lair(1, 1, 60, 7), Empty(2) },
            World(Mob(7, hp: 1500, exp: 100, align: 1, Hit(1, 1))), secondsPerStep: 1, hours: 1, seed: 1);

        Assert.Equal(0, run.Laps);
        Assert.InRange(run.Kills, 3600 / 76, 3600 / 60 + 1);
    }

    [Fact]
    public void ParadigmRestPaysFullOnlyOnEveryThirdTick()
    {
        // Counted from lying down, each cycle pays a third, a third, then in full.
        var cycle = new SimRegen(_ => 0, _ => 30, _ => 0, 0, RealmRegenProfile.ParaMud, RestReplacesStanding: true,
            RestFullEvery: 3, RestReducedShare: 1.0 / 3);
        Assert.Equal(new[] { 10.0, 10.0, 30.0, 10.0, 10.0, 30.0 },
            Enumerable.Range(1, 6).Select(t => Math.Round(cycle.RestTickHp(t), 6)));

        // Played out — same seed, same fight, same damage taken — that cycle takes
        // longer to rest back up than one paying in full every tick.
        var health = new HealthSettings { RestIfBelowHp = 99, RestMaxHp = 100, UseMeditateAbility = false };
        LoopSimRun Rest(SimRegen regen) => LoopSimulator.Run(Character(maxHp: 200, health: health, regen: regen),
            new[] { Lair(1, 1, 7200, 7), Empty(2) },
            World(Mob(7, hp: 1500, exp: 100, align: 1, Hit(25, 25))), secondsPerStep: 1, hours: 0.5, seed: 1);

        LoopSimRun cycled = Rest(cycle);
        LoopSimRun full = Rest(new SimRegen(_ => 0, _ => 30, _ => 0, 0, RealmRegenProfile.ParaMud, RestReplacesStanding: true));

        Assert.True(full.RestingSeconds > 0);
        Assert.True(cycled.RestingSeconds > full.RestingSeconds);
    }

    [Fact]
    public void DyingRunCountsAgainstTheHoursAskedFor()
    {
        // 1,000 exp then death half-way through a 1 h run is 1,000 exp/hr, not the
        // 2,000/hr pace it held while alive; the summary's minimum shows it.
        var none = new Dictionary<string, int>();
        var died = new LoopSimRun(3600, 1800, 1000, 10, 5, 0, 0, 0, 0, 0, 0, 100, 1800, none);
        var lived = new LoopSimRun(3600, 3600, 3000, 30, 10, 0, 0, 0, 0, 0, 50, 100, null, none);
        var summary = new LoopSimSummary(new[] { died, lived });

        Assert.Equal(1000, died.ExpPerHour);
        Assert.Equal(2000, summary.ExpPerHour);
        Assert.Equal(1000, summary.MinExpPerHour);
        Assert.Equal(3000, summary.MaxExpPerHour);
    }

    [Theory]
    [InlineData(55.0, "Neutral", 55)]     // exact evil points win when Paradigm's `pro` gave them
    [InlineData(null, "Outlaw", 40)]      // Stock: the who title's band
    [InlineData(null, "Saint", -201)]
    [InlineData(null, null, 0)]           // nothing known: Neutral
    public void AlignmentValueFallsBackToTheTitle(double? evilPoints, string? title, int expected)
        => Assert.Equal(expected, SimCharacterBuilder.AlignmentValue(evilPoints, title));

    [Fact]
    public void LawfulEvilSparesAnOutlawCharacter()
    {
        // An Align-6 monster the character's weapon can't touch (so it's never engaged)
        // and that out-hits its pool opens on a Neutral character and kills it; an
        // Outlaw's title (no evil points known) keeps it passive, and the character
        // walks the lap unharmed.
        var world = World(Mob(6, hp: 100000, exp: 1, align: 6, Hit(500, 500)) with { Magical = 99 });
        var lap = new[] { Lair(1, 1, 60, 6), Empty(2) };
        LoopSimRun neutral = LoopSimulator.Run(Character(maxHp: 50,
            alignment: SimCharacterBuilder.AlignmentValue(null, "Neutral")), lap, world, secondsPerStep: 1, hours: 0.2, seed: 1);
        LoopSimRun outlaw = LoopSimulator.Run(Character(maxHp: 50,
            alignment: SimCharacterBuilder.AlignmentValue(null, "Outlaw")), lap, world, secondsPerStep: 1, hours: 0.2, seed: 1);

        Assert.NotNull(neutral.DiedAtSeconds);
        Assert.Null(outlaw.DiedAtSeconds);
    }

    // A one-cast-a-round attack spell that never misses, so every fight lasts a
    // fixed number of rounds and a run's timing doesn't hang on the seed.
    private static SimCharacter Caster(RealmType realm, int damagePerRound)
    {
        var combat = new CombatSettings();
        combat.NormalAttackSpell.SpellName = "blst";
        var spells = new Dictionary<string, SimSpell>(StringComparer.OrdinalIgnoreCase)
        {
            ["blst"] = new SimSpell("blst", 1, ManaPerCast: 1, FiresPerRound: 1, CastChance: 100,
                MinDamagePerRound: damagePerRound, MaxDamagePerRound: damagePerRound, UsesMagicResist: false,
                TypeOfResists: 0, AttType: 0, MinHeal: 0, MaxHeal: 0),
        };
        return Character(damage: 0, combat: combat, spells: spells, maxMana: 100000, realm: realm);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void StockLairRunsOneRoomClockThatEveryKillRestarts(int seed)
    {
        // A 2-mob lair on a 62 s clock; each mob takes exactly 12 rounds (dies at
        // 55 s and 115 s). Per slot (Paradigm) the first slot is ready at 117 s, back
        // on the next entry and dead at 175 s: three kills in 200 s. On Stock the
        // 115 s kill pushes the room's one clock to 177 s for both slots: two kills.
        SimRoom[] lap = { Lair(1, 2, 62, 7), Empty(2) };
        var world = World(Mob(7, hp: 1200, exp: 100));
        const double hours = 200.0 / 3600;
        LoopSimRun stock = LoopSimulator.Run(Caster(RealmType.Stock, 100), lap, world, 1, hours, seed);
        LoopSimRun paradigm = LoopSimulator.Run(Caster(RealmType.ParaMud, 100), lap, world, 1, hours, seed);

        Assert.Equal(2, stock.Kills);
        Assert.Equal(3, paradigm.Kills);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void StockFixtureKillRestartsTheLairRoomClock(int seed)
    {
        // A lair room whose placed fixture is back on every entry and dies each visit,
        // well inside the lair's 62 s clock. On Stock each fixture death restarts the
        // room clock, so the lair mob never returns after its first kill; Paradigm's
        // per-slot clock ignores the fixture and the lair keeps refilling.
        SimRoom[] lap = { new(new RoomKey(1, 1), 8, 1, new[] { 7 }, 62), Empty(2) };
        var world = World(Mob(7, hp: 10, exp: 1000), Mob(8, hp: 10, exp: 1));
        LoopSimRun stock = LoopSimulator.Run(Caster(RealmType.Stock, 100), lap, world, 1, hours: 1, seed);
        LoopSimRun paradigm = LoopSimulator.Run(Caster(RealmType.ParaMud, 100), lap, world, 1, hours: 1, seed);

        static long LairKills(LoopSimRun r) => (r.Exp - r.Kills) / 999;   // 1,000 exp a lair kill, 1 a fixture
        Assert.True(stock.Kills > 100);
        Assert.Equal(1, LairKills(stock));
        Assert.True(LairKills(paradigm) > 40);
    }

    [Fact]
    public void MonsterWeCannotHurtStillTriggersTheHangUp()
    {
        // A hostile above the weapon's magic level, with no attack spell: nothing to
        // fight, but it hits 20 a round while the character rests beside it. Its hits
        // put the live client in combat, so the 50% hang-up fires before a death.
        var health = new HealthSettings { RestIfBelowHp = 90, RestMaxHp = 100, HangIfBelowHp = 50, UseMeditateAbility = false };
        MonsterCatalogEntry ghost = Mob(9, hp: 1000, exp: 10, align: 1, Hit(20, 20, accuracy: 9999)) with { Magical = 20 };
        LoopSimRun run = LoopSimulator.Run(Character(maxHp: 100, health: health), new[] { Lair(1, 1, 3600, 9), Empty(2) },
            World(ghost), secondsPerStep: 1, hours: 0.25, seed: 1);

        Assert.NotNull(run.HungUpAtSeconds);
        Assert.Null(run.DiedAtSeconds);
    }

    [Fact]
    public void MonsterWeCannotHurtStillTriggersTheRun()
    {
        var health = new HealthSettings { RestIfBelowHp = 90, RestMaxHp = 100, RunIfBelowHp = 50, HangIfBelowHp = 0,
            UseMeditateAbility = false };
        MonsterCatalogEntry ghost = Mob(9, hp: 1000, exp: 10, align: 1, Hit(20, 20, accuracy: 9999)) with { Magical = 20 };
        LoopSimRun run = LoopSimulator.Run(Character(maxHp: 100, health: health, combat: new CombatSettings { RunDistance = 1 }),
            new[] { Lair(1, 1, 3600, 9), Empty(2) }, World(ghost), secondsPerStep: 1, hours: 0.25, seed: 1);

        Assert.True(run.Flees > 0);
    }

    [Fact]
    public void FledCharacterReturnsOnlyAboveItsRunTrigger()
    {
        // No rest trigger, so nothing holds the character away but the run trigger:
        // walking back at once at 40% HP would take two more 30-damage hits to a
        // death; waiting until HP climbs back over 50% never does.
        var health = new HealthSettings { RunIfBelowHp = 50, RestIfBelowHp = 0, HangIfBelowHp = 0, UseMeditateAbility = false };
        LoopSimRun run = LoopSimulator.Run(Character(maxHp: 100, damage: 1, health: health, combat: new CombatSettings { RunDistance = 1 }),
            new[] { Lair(1, 1, 3600, 8), Empty(2) }, World(Mob(8, hp: 100000, exp: 10, align: 1, Hit(30, 30, accuracy: 9999))),
            secondsPerStep: 1, hours: 0.5, seed: 1);

        Assert.True(run.Flees > 0);
        Assert.Null(run.DiedAtSeconds);
    }

    [Fact]
    public void FailedRerollCastKeepsTheCycleGoing()
    {
        // An endless reroll cycle no roll satisfies, on a spell that lands half the
        // time and outlasts the run: every round's recast goes out whether the last
        // one landed or not. Ending the cycle on a failed cast would leave the buff
        // sitting there after a handful of casts.
        var spells = new Dictionary<string, SimSpell>(StringComparer.OrdinalIgnoreCase)
        {
            ["prfl"] = Spell("prfl", mana: 1, duration: 36000, manaRegenMin: 0, manaRegenMax: 100) with { CastChance = 50 },
        };
        var buffs = new[] { new SimBuff("prfl", 0, false, false, false, RerollBelow: 101, RerollCount: 0, RerollInfinite: true) };
        var health = new HealthSettings { BlessIfAboveMa = 0, RestIfBelowMa = 0, UseMeditateAbility = false };
        LoopSimRun run = LoopSimulator.Run(
            Character(spells: spells, buffs: buffs, maxMana: 1000, health: health),
            new[] { Empty(1), Empty(2) }, World(), secondsPerStep: 1, hours: 0.5, seed: 1);

        int casts = run.Casts.GetValueOrDefault("prfl");
        Assert.True(casts > 300, $"cast {casts} times");
    }
}

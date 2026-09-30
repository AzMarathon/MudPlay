using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Game.Simulation;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// Pins the character loop simulation: a lair refills only on its per-slot respawn
// clock (on entry, or on the spawn pass while standing in it; an unresolved clock
// only on entry), a fight the character can't survive ends the run and counts
// against the hours asked for, resting follows the Health-tab rest gates and the
// realm's rest-tick cycle, lawful evil spares an Outlaw-or-worse character, and a
// configured attack spell is cast by the chooser.
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
        IReadOnlyDictionary<string, SimSpell>? spells = null, int maxMana = 0, SimRegen? regen = null,
        int alignment = 0) => new(
        RealmType.ParaMud, Level: 30, MaxHp: maxHp, MaxMana: maxMana,
        Melee: new PlayerMatchupProfile(RealmType.ParaMud, NormalAccuracy: 9999, AvgWeaponDamage: damage,
            SwingsPerRound: swings, HasWeapon: true, ArmourClass: 0, Dodge: 0, ProtEvil: 0, ProtGood: 0,
            DamageResist: 0),
        WeaponHitMagic: 10,
        Defense: new PlayerDefenseProfile(0, 0, 0, 0, false, 0, EvilLevel.Saint, 0, AcExact: 0),
        Regen: regen ?? new SimRegen(1, 30, 0, 0, RealmRegenProfile.ParaMud, RestReplacesStanding: true),
        Spells: spells ?? new Dictionary<string, SimSpell>(),
        Combat: combat ?? new CombatSettings(),
        Health: health ?? new HealthSettings { UseMeditateAbility = false },
        SpellSlots: new SpellsSettings(),
        Overlay: _ => new MonsterOverlay(),
        SpellShortByNumber: _ => null,
        AlignmentValue: alignment);

    private static SimWorld World(params MonsterCatalogEntry[] mobs)
    {
        var byNumber = mobs.ToDictionary(m => m.Number);
        return new SimWorld(n => byNumber.GetValueOrDefault(n));
    }

    private static SimRoom Lair(int room, int max, int respawn, params int[] monsters) =>
        new(new RoomKey(1, room), 0, max, monsters, respawn);

    private static SimRoom Empty(int room) => new(new RoomKey(1, room), 0, 0, Array.Empty<int>(), 0);

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
        var still = new SimRegen(0, 0, 0, 0, RealmRegenProfile.ParaMud, RestReplacesStanding: true);
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
        // Same seed, same fight, same damage taken: the rest cycle whose first two
        // ticks are reduced takes longer to rest back up than one paying in full.
        var health = new HealthSettings { RestIfBelowHp = 99, RestMaxHp = 100, UseMeditateAbility = false };
        LoopSimRun Rest(SimRegen regen) => LoopSimulator.Run(Character(maxHp: 200, health: health, regen: regen),
            new[] { Lair(1, 1, 7200, 7), Empty(2) },
            World(Mob(7, hp: 1500, exp: 100, align: 1, Hit(25, 25))), secondsPerStep: 1, hours: 0.5, seed: 1);

        LoopSimRun cycled = Rest(new SimRegen(0, 30, 0, 0, RealmRegenProfile.ParaMud, RestReplacesStanding: true,
            RestFullEvery: 3, RestReducedShare: 1.0 / 3));
        LoopSimRun full = Rest(new SimRegen(0, 30, 0, 0, RealmRegenProfile.ParaMud, RestReplacesStanding: true));

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
}

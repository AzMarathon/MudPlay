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
using Xunit.Abstractions;

namespace MudPlay.Tests;

// Area rankings saved to disk are trusted only while LoopSimulator.SimulatorVersion
// is the number they were made with. Nothing forces that number to be bumped when
// the simulation changes, so this pins the result of one small fixed run that leans
// on most of what a simulated hour does (melee both ways, an attack spell and its
// mana, a lair on a respawn clock, resting, the regen ticks): a change to any of it
// moves a figure here, and the failure says what to do.
public sealed class SimulatorVersionPinTests(ITestOutputHelper output)
{
    // The SimulatorVersion the figures below were taken at.
    private const int PinnedAt = 1;

    private const string Bump =
        "The simulation gives a different result for the same character, lap and game data. If that is intended, " +
        "bump LoopSimulator.SimulatorVersion (saved area rankings made by the old simulation are then dropped " +
        "instead of being listed as current), then set PinnedAt and the figures in this test to the new ones.";

    private static MonsterCatalogEntry Mob(int number, int hp, int exp, params MonsterAttackSlot[] attacks) => new(
        number, $"mob{number}", Type: 0, Align: 1, Undead: false, Exp: exp, ExpMulti: 1, RegenTime: 0,
        Hp: hp, HpRegen: 0, ArmourClass: 20, DamageResist: 2, MagicRes: 10, FollowPercent: 0, CharmLevel: 0,
        CashRunic: 0, CashPlatinum: 0, CashGold: 0, CashSilver: 0, CashCopper: 0, Weapon: 0, CreateSpell: 0,
        DeathSpell: 0, BsDefense: 0, Energy: 1000, AvgDamage: 0, Attacks: attacks,
        MidSpells: Array.Empty<MonsterMidSpellSlot>(), Drops: Array.Empty<MonsterDropSlot>(),
        Abilities: Array.Empty<MonsterAbilitySlot>(), ElementalResists: new Dictionary<int, int>(),
        Magical: 0, SpellImmunity: 0, Dodge: 5, NonLiving: false, CastsElements: Array.Empty<string>());

    private static CombatSettings Combat()
    {
        var combat = new CombatSettings();
        combat.NormalAttackSpell.SpellName = "bolt";
        return combat;
    }

    private static SimCharacter Character() => new(
        RealmType.ParaMud, Level: 30, MaxHp: 180, MaxMana: 120,
        Melee: new PlayerMatchupProfile(RealmType.ParaMud, NormalAccuracy: 120, AvgWeaponDamage: 22,
            SwingsPerRound: 2.5, HasWeapon: true, ArmourClass: 40, Dodge: 12, ProtEvil: 0, ProtGood: 0,
            DamageResist: 3, CritChancePercent: 8, AvgCritDamage: 60, MinDamage: 12, MaxDamage: 32),
        WeaponHitMagic: 2,
        Defense: new PlayerDefenseProfile(40, 12, 0, 0, false, 0, EvilLevel.Saint, 0, AcExact: 40),
        Regen: new SimRegen(_ => 2, _ => 14, extra => 3 + extra * 0.1, 6, RealmRegenProfile.ParaMud, RestReplacesStanding: true),
        Spells: new Dictionary<string, SimSpell>(StringComparer.OrdinalIgnoreCase)
        {
            ["bolt"] = new("bolt", 1, ManaPerCast: 9, FiresPerRound: 1, CastChance: 85, MinDamagePerRound: 14,
                MaxDamagePerRound: 30, UsesMagicResist: true, TypeOfResists: 1, AttType: 2, MinHeal: 0, MaxHeal: 0),
        },
        Combat: Combat(),
        Health: new HealthSettings { UseMeditateAbility = false },
        SpellSlots: new SpellsSettings(),
        Overlay: _ => new MonsterOverlay(),
        SpellShortByNumber: _ => null,
        AlignmentValue: 0);

    [Fact]
    public void ASmallFixedRunStillGivesTheFiguresPinnedForThisSimulatorVersion()
    {
        SimRoom[] lap =
        {
            new(new RoomKey(1, 1), 0, 2, new[] { 3, 4 }, 45),
            new(new RoomKey(1, 2), 0, 0, Array.Empty<int>(), 0),
            new(new RoomKey(1, 3), 4, 0, Array.Empty<int>(), 0),
            new(new RoomKey(1, 4), 0, 0, Array.Empty<int>(), 0),
        };
        var mobs = new[]
        {
            Mob(3, hp: 90, exp: 400, new MonsterAttackSlot("claw", Type: 1, Percent: 100, TruePercent: 100,
                MinDamage: 6, MaxDamage: 18, Accuracy: 90, Energy: 500, HitSpell: 0)),
            Mob(4, hp: 140, exp: 700, new MonsterAttackSlot("bite", Type: 1, Percent: 100, TruePercent: 100,
                MinDamage: 10, MaxDamage: 26, Accuracy: 110, Energy: 1000, HitSpell: 0)),
        }.ToDictionary(m => m.Number);
        var world = new SimWorld(n => mobs.GetValueOrDefault(n));

        LoopSimSummary got = LoopSimulator.RunMany(Character(), lap, world, secondsPerStep: 1.2, hours: 1, runs: 2);

        string figures = string.Join(" | ", got.Runs.Select(r =>
            FormattableString.Invariant($"exp {r.Exp} kills {r.Kills} laps {r.Laps} dmg {r.DamageTaken} lowHp {r.LowestHpPercent} lowMa {r.LowestManaPercent} ") +
            FormattableString.Invariant($"attack {r.AttackingSeconds:0.0} rest {r.RestingSeconds:0.0} move {r.MovingSeconds:0.0} died {r.DiedAtSeconds?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) ?? "no"} ") +
            "casts " + string.Join(",", r.Casts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key}={c.Value}"))));
        output.WriteLine(figures);

        Assert.True(LoopSimulator.SimulatorVersion == PinnedAt,
            $"LoopSimulator.SimulatorVersion is {LoopSimulator.SimulatorVersion} and the figures here were pinned at {PinnedAt}: " +
            "run this test, copy the figures it prints into Pinned, and set PinnedAt.");
        Assert.True(Pinned == figures, $"{Bump}\nPinned: {Pinned}\nNow:    {figures}");
    }

    private const string Pinned =
        "exp 4700 kills 8 laps 2 dmg 577 lowHp 19 lowMa 7 attack 210.8 rest 3362.8 move 26.5 died no casts bolt=47 | " +
        "exp 5500 kills 10 laps 1 dmg 601 lowHp 22 lowMa 7 attack 210.0 rest 3373.2 move 16.8 died no casts bolt=48";
}

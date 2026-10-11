using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Map;
using MudPlay.Game.Simulation;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// What a kept area ranking is compared by when it is listed again. The world it was
// made in: any difference drops it. The character it was made for, by groups: a
// difference marks it and names the group, and only the group that changed.
public sealed class RankingStampTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mudplay-stamps-" + Path.GetRandomFileName());

    public RankingStampTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    // ----- The world ---------

    private static RankingWorld World(int version = 1, string rooms = "10:1", string seed = "s") => new(
        version, RankingWorld.TableNames.ToDictionary(t => t, t => t == "Rooms" ? rooms : "5:5"), seed);

    [Fact]
    public void TheSameWorldDiffersInNothing() => Assert.Null(World().DiffersFrom(World()));

    [Fact]
    public void AnotherSimulatorVersionATableOrTheSeed_EachMakesItAnotherWorld()
    {
        Assert.Contains("simulator version 1 and this is version 2", World(version: 2).DiffersFrom(World()));
        Assert.Contains("Rooms table has changed", World(rooms: "11:1").DiffersFrom(World()));
        Assert.Contains("monster seed", World(seed: "t").DiffersFrom(World()));
        // A table the ranking was made without, or with and now gone.
        var without = new RankingWorld(1, new Dictionary<string, string>(), "s");
        Assert.NotNull(World().DiffersFrom(without));
        Assert.NotNull(without.DiffersFrom(World()));
    }

    [Fact]
    public void TheWorldIsReadOffTheSetsTablesAndTheSeed()
    {
        var reader = new RankingStampReader();
        string seed = Path.Combine(_dir, "seed.json");
        File.WriteAllText(seed, "[1]");
        foreach (string table in RankingWorld.TableNames.Where(t => t != "Lairs"))
            File.WriteAllText(Path.Combine(_dir, table + ".json"), "[]");
        File.WriteAllText(Path.Combine(_dir, "Shops.json"), "[]");

        RankingWorld made = reader.World(_dir, seed);
        Assert.Equal(LoopSimulator.SimulatorVersion, made.SimulatorVersion);
        Assert.Equal("none", made.Tables["Lairs"]);
        Assert.Null(reader.World(_dir, seed).DiffersFrom(made));

        // A table the simulator doesn't read, and the seed written again as it was.
        File.WriteAllText(Path.Combine(_dir, "Shops.json"), "[1, 2, 3]");
        File.WriteAllText(seed, "[1]");
        File.SetLastWriteTimeUtc(seed, DateTime.UtcNow.AddMinutes(5));
        Assert.Null(reader.World(_dir, seed).DiffersFrom(made));

        // A table imported again, a table that arrives, another seed, no seed.
        File.WriteAllText(Path.Combine(_dir, "Monsters.json"), "[ ]");
        Assert.Contains("Monsters", reader.World(_dir, seed).DiffersFrom(made));
        made = reader.World(_dir, seed);
        File.WriteAllText(Path.Combine(_dir, "Lairs.json"), "[]");
        Assert.Contains("Lairs", reader.World(_dir, seed).DiffersFrom(made));
        made = reader.World(_dir, seed);
        File.WriteAllText(seed, "[2]");
        Assert.Contains("seed", reader.World(_dir, seed).DiffersFrom(made));
        Assert.Contains("seed", reader.World(_dir, null).DiffersFrom(made));
    }

    [Fact]
    public void GameDataEditsCountByTierNameAndContent_AndAnEmptiedFileIsNoEdit()
    {
        var reader = new RankingStampReader();
        string global = Path.Combine(_dir, "global"), realm = Path.Combine(_dir, "realm"), own = Path.Combine(_dir, "own");
        foreach (string folder in new[] { global, realm, own }) Directory.CreateDirectory(folder);
        string[] tiers = { global, realm, own };
        string none = reader.GameDataEdits(tiers, "alpha");

        // Another set's edits, and a tier that isn't there, are nothing to this one.
        File.WriteAllText(Path.Combine(global, "monsters_overrides.beta.json"), """{"1":{"Hp":5}}""");
        Assert.Equal(none, reader.GameDataEdits(tiers.Append(Path.Combine(_dir, "missing")), "alpha"));

        string edit = Path.Combine(realm, "monsters_overrides.alpha.json");
        File.WriteAllText(edit, """{"1":{"Hp":5}}""");
        string edited = reader.GameDataEdits(tiers, "alpha");
        Assert.NotEqual(none, edited);

        // Saved again unchanged; then changed; then the same edit at another tier.
        File.WriteAllText(edit, """{"1":{"Hp":5}}""");
        File.SetLastWriteTimeUtc(edit, DateTime.UtcNow.AddMinutes(5));
        Assert.Equal(edited, reader.GameDataEdits(tiers, "alpha"));
        File.WriteAllText(edit, """{"1":{"Hp":6}}""");
        Assert.NotEqual(edited, reader.GameDataEdits(tiers, "alpha"));
        File.Move(edit, Path.Combine(own, "monsters_overrides.alpha.json"));
        File.WriteAllText(Path.Combine(own, "monsters_overrides.alpha.json"), """{"1":{"Hp":5}}""");
        Assert.NotEqual(edited, reader.GameDataEdits(tiers, "alpha"));

        // The last edit undone leaves an empty file behind.
        File.WriteAllText(Path.Combine(own, "monsters_overrides.alpha.json"), "{}");
        Assert.Equal(none, reader.GameDataEdits(tiers, "alpha"));
    }

    // ----- The character ---------

    private static SimCharacter Character(int damage = 20, int maxHp = 200, string? spell = null,
        CombatSettings? combat = null, Func<int, double>? hpResting = null) => new(
        RealmType.ParaMud, Level: 30, MaxHp: maxHp, MaxMana: 100,
        Melee: new PlayerMatchupProfile(RealmType.ParaMud, NormalAccuracy: 100, AvgWeaponDamage: damage,
            SwingsPerRound: 2, HasWeapon: true, ArmourClass: 30, Dodge: 10, ProtEvil: 0, ProtGood: 0, DamageResist: 2),
        WeaponHitMagic: 1,
        Defense: new PlayerDefenseProfile(30, 10, 0, 0, false, 0, EvilLevel.Saint, 0, AcExact: 30),
        Regen: new SimRegen(_ => 2, hpResting ?? (_ => 12), extra => extra * 0.2, 5, RealmRegenProfile.ParaMud, RestReplacesStanding: true),
        Spells: spell is null
            ? new Dictionary<string, SimSpell>()
            : new Dictionary<string, SimSpell>
            {
                [spell] = new(spell, 7, ManaPerCast: 5, FiresPerRound: 1, CastChance: 90, MinDamagePerRound: 10,
                    MaxDamagePerRound: 20, UsesMagicResist: true, TypeOfResists: 0, AttType: 0, MinHeal: 0, MaxHeal: 0),
            },
        Combat: combat ?? new CombatSettings(),
        Health: new HealthSettings(),
        SpellSlots: new SpellsSettings(),
        Overlay: _ => new MonsterOverlay(),
        SpellShortByNumber: _ => null,
        AlignmentValue: 0);

    private static RankingCharacter Of(SimCharacter c, string edits = "e", string reach = "r") =>
        RankingCharacter.Of(c, edits, reach);

    [Fact]
    public void TheSameCharacterReadTwiceHasChangedInNothing()
    {
        Assert.Empty(Of(Character(spell: "bolt")).ChangedSince(Of(Character(spell: "bolt"))));
        Assert.True(RankingCheck.Of(Of(Character()), Of(Character())).IsCurrent);
    }

    [Fact]
    public void EachGroupChangesByItselfAndIsNamed()
    {
        RankingCharacter made = Of(Character(spell: "bolt"));

        Assert.Equal(new[] { RankingCharacter.StatsOrGear }, Of(Character(damage: 25, spell: "bolt")).ChangedSince(made));
        Assert.Equal(new[] { RankingCharacter.StatsOrGear }, Of(Character(maxHp: 210, spell: "bolt")).ChangedSince(made));
        // Regen is functions: a different rate is a different character.
        Assert.Equal(new[] { RankingCharacter.StatsOrGear }, Of(Character(spell: "bolt", hpResting: _ => 13)).ChangedSince(made));
        Assert.Equal(new[] { RankingCharacter.Spells }, Of(Character(spell: "blst")).ChangedSince(made));
        Assert.Equal(new[] { RankingCharacter.Spells }, Of(Character()).ChangedSince(made));
        Assert.Equal(new[] { RankingCharacter.Settings },
            Of(Character(spell: "bolt", combat: new CombatSettings { RunDistance = 3 })).ChangedSince(made));
        Assert.Equal(new[] { RankingCharacter.GameDataEdits }, Of(Character(spell: "bolt"), edits: "other").ChangedSince(made));
        Assert.Equal(new[] { RankingCharacter.Reach }, Of(Character(spell: "bolt"), reach: "other").ChangedSince(made));

        // Several at once, in the order they are always named in.
        Assert.Equal(new[] { RankingCharacter.StatsOrGear, RankingCharacter.Settings, RankingCharacter.Reach },
            Of(Character(damage: 25, spell: "bolt", combat: new CombatSettings { RunDistance = 3 }), reach: "other").ChangedSince(made));
    }

    [Fact]
    public void AGroupEitherSideLacksCountsAsChanged_AndWithNoRecordNothingIsKnown()
    {
        RankingCharacter made = Of(Character());
        var partial = new RankingCharacter(made.Groups.Where(g => g.Key != RankingCharacter.Reach).ToDictionary(g => g.Key, g => g.Value));

        Assert.Equal(new[] { RankingCharacter.Reach }, made.ChangedSince(partial));
        Assert.Equal(new[] { RankingCharacter.Reach }, partial.ChangedSince(made));
        Assert.True(RankingCheck.Of(null, made).Unchecked);
        Assert.True(RankingCheck.Of(made, null).Unchecked);
        Assert.False(RankingCheck.NotChecked.IsCurrent);
    }

    // ----- Reach ---------

    [Fact]
    public void ReachChangesWithAvoidedRoomsClassRaceAndTheGateItemsCarried_NotWithTheirOrder()
    {
        RoomKey[] avoided = { new(1, 5), new(2, 9) };
        string made = RankingReach.Hash(avoided, 3, 2, new[] { 807, 12 });

        Assert.Equal(made, RankingReach.Hash(avoided.Reverse(), 3, 2, new[] { 12, 807, 12 }));
        Assert.NotEqual(made, RankingReach.Hash(avoided.Take(1), 3, 2, new[] { 807, 12 }));
        Assert.NotEqual(made, RankingReach.Hash(avoided, 4, 2, new[] { 807, 12 }));
        Assert.NotEqual(made, RankingReach.Hash(avoided, 3, null, new[] { 807, 12 }));
        Assert.NotEqual(made, RankingReach.Hash(avoided, 3, 2, new[] { 807 }));
    }
}

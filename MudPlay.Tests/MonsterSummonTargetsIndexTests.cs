using System;
using System.IO;
using MudPlay.Game.Combat;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins MonsterSummonTargetsIndex — the monster→summoned-child map that widens the
// room-aware resolver's candidate set with a summoner's minions. Mirrors the
// AbilVal-wins / MinBase-fallback encoding of the Game-Data browser's summon read.
public sealed class MonsterSummonTargetsIndexTests : IDisposable
{
    private readonly string _root;

    public MonsterSummonTargetsIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-summontargets-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // 500 death-summons 303 (AbilVal); 600 spawn-summons 601 + 602 (two AbilVals);
    // 700's summon spell names no target so it falls back to MinBase 42; 800's
    // DeathSpell is a damage spell (no summon); 900 has no summon spell at all.
    private const string MonstersJson = """
        [
          { "Number": 500, "Name": "necromancer",  "DeathSpell": 900 },
          { "Number": 600, "Name": "hive queen",   "CreateSpell": 910 },
          { "Number": 700, "Name": "raptor egg",   "DeathSpell": 920 },
          { "Number": 800, "Name": "orc",          "DeathSpell": 930 },
          { "Number": 900, "Name": "rat",          "DeathSpell": 0 }
        ]
        """;

    private const string SpellsJson = """
        [
          { "Number": 900, "Name": "raise dead",   "Abil-0": 12, "AbilVal-0": 303 },
          { "Number": 910, "Name": "hatch swarm",  "Abil-0": 12, "AbilVal-0": 601, "Abil-1": 12, "AbilVal-1": 602 },
          { "Number": 920, "Name": "raptor summon","Abil-0": 12, "AbilVal-0": 0, "MinBase": 42 },
          { "Number": 930, "Name": "fireball",     "Abil-0": 1,  "AbilVal-0": 50 }
        ]
        """;

    private MonsterSummonTargetsIndex NewIndex()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Monsters.json"), MonstersJson);
        File.WriteAllText(Path.Combine(_root, "alpha", "Spells.json"), SpellsJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        return new MonsterSummonTargetsIndex(cache);
    }

    [Fact]
    public void DeathSummon_YieldsTheAbilValTarget()
        => Assert.Equal(new[] { 303 }, NewIndex().SummonedBy(500));

    [Fact]
    public void SpawnSummon_YieldsAllAbilValTargets()
        => Assert.Equal(new[] { 601, 602 }, NewIndex().SummonedBy(600));

    [Fact]
    public void NoAbilValTarget_FallsBackToMinBase()
        => Assert.Equal(new[] { 42 }, NewIndex().SummonedBy(700));

    [Fact]
    public void NonSummonDeathSpell_YieldsNothing()
        => Assert.Empty(NewIndex().SummonedBy(800));

    [Fact]
    public void NoSummonSpell_YieldsNothing()
        => Assert.Empty(NewIndex().SummonedBy(900));

    [Fact]
    public void UnknownMonster_YieldsNothing()
        => Assert.Empty(NewIndex().SummonedBy(12345));
}

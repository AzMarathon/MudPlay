using System;
using System.IO;
using MudPlay.Game.Combat;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins MonsterDeathSummonIndex — a monster summons on death when its
// Monsters.DeathSpell points to a Spells row carrying the Summon ability (code 12).
// Drives the summon-on-death CR recheck (report paradigm-20260729-211336).
public sealed class MonsterDeathSummonIndexTests : IDisposable
{
    private readonly string _root;

    public MonsterDeathSummonIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-summon-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // 457/458 summon on death (DeathSpell → a Summon spell — the real Paradigm
    // pair); 500's DeathSpell is a damage spell (no summon); 999 has no DeathSpell.
    private const string MonstersJson = """
        [
          { "Number": 457, "Name": "dwarf warrior", "DeathSpell": 550 },
          { "Number": 458, "Name": "dwarf warrior", "DeathSpell": 551 },
          { "Number": 500, "Name": "orc",           "DeathSpell": 700 },
          { "Number": 999, "Name": "rat",           "DeathSpell": 0 }
        ]
        """;

    private const string SpellsJson = """
        [
          { "Number": 550, "Name": "summon brain eater",  "Abil-0": 12, "AbilVal-0": 459 },
          { "Number": 551, "Name": "summon shapeshifter", "Abil-0": 12, "AbilVal-0": 460 },
          { "Number": 700, "Name": "fireball",            "Abil-0": 1,  "AbilVal-0": 50 }
        ]
        """;

    private MonsterDeathSummonIndex NewIndex()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Monsters.json"), MonstersJson);
        File.WriteAllText(Path.Combine(_root, "alpha", "Spells.json"), SpellsJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        return new MonsterDeathSummonIndex(cache);
    }

    [Theory]
    [InlineData(457, true)]
    [InlineData(458, true)]
    [InlineData(500, false)]    // DeathSpell is a damage spell, not a summon
    [InlineData(999, false)]    // no DeathSpell
    [InlineData(12345, false)]  // unknown monster
    public void SummonsOnDeath_TrueOnlyForDeathSpellThatSummons(int monster, bool expected)
        => Assert.Equal(expected, NewIndex().SummonsOnDeath(monster));
}

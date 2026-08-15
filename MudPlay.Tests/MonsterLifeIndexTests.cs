using System;
using System.IO;
using MudPlay.Game.Combat;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins MonsterLifeIndex — the drain-eligibility lookup. A drain spell can only
// affect a target that is LIVING (no NonLiving ability 109) AND NOT undead (the
// Undead column != 0, which also catches the 255 = MDB "-1" byte). Only mobs that
// are nonliving or undead are stored; everything else (and an unknown number)
// reads drain-eligible (fail-open).
public sealed class MonsterLifeIndexTests : IDisposable
{
    private readonly string _root;

    public MonsterLifeIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-life-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // #10 thug — plain living. #2 lashworm — Animal (78), still living. #5 acid
    // slime — NonLiving (109), not undead. #11 skeleton — NonLiving AND Undead 1.
    // #20 banshee — Undead 255 (the -1 byte), no NonLiving. #21 wight — Undead 1,
    // NOT nonliving (a living-only gate would wrongly allow it).
    private const string MonstersJson = """
        [
          { "Number": 10, "Name": "thug" },
          { "Number": 2,  "Name": "lashworm",   "Abil-0": 78,  "AbilVal-0": 1 },
          { "Number": 5,  "Name": "acid slime", "Abil-0": 109, "AbilVal-0": 0 },
          { "Number": 11, "Name": "skeleton",   "Abil-0": 109, "AbilVal-0": 0, "Undead": 1 },
          { "Number": 20, "Name": "banshee",    "Undead": 255 },
          { "Number": 21, "Name": "wight",      "Undead": 1 }
        ]
        """;

    private MonsterLifeIndex NewIndex(string set = "alpha", string json = MonstersJson)
    {
        Directory.CreateDirectory(Path.Combine(_root, set));
        File.WriteAllText(Path.Combine(_root, set, "Monsters.json"), json);
        GameDataCache cache = new(_root);
        cache.SwitchSet(set);
        return new MonsterLifeIndex(cache);
    }

    [Theory]
    [InlineData(10, true)]    // thug — living
    [InlineData(2,  true)]    // lashworm — living animal
    [InlineData(5,  false)]   // acid slime — nonliving
    [InlineData(11, false)]   // skeleton — nonliving + undead
    [InlineData(20, false)]   // banshee — undead via the 255 byte (tests != 0, not == 1)
    [InlineData(21, false)]   // wight — undead but living (not caught by NonLiving alone)
    [InlineData(999, true)]   // unknown number — fail-open
    [InlineData(-1, true)]    // unresolved sentinel — fail-open
    public void CanDrain_LivingNonUndeadOnly(int number, bool expected)
    {
        MonsterLifeIndex sut = NewIndex();
        Assert.Equal(expected, sut.CanDrain(number));
    }

    [Theory]
    [InlineData(11, "nonliving+undead")]
    [InlineData(5,  "nonliving")]
    [InlineData(20, "undead")]
    [InlineData(21, "undead")]
    [InlineData(10, null)]
    [InlineData(999, null)]
    public void DrainBlockReason_NamesTheReason(int number, string? expected)
    {
        MonsterLifeIndex sut = NewIndex();
        Assert.Equal(expected, sut.DrainBlockReason(number));
    }
}

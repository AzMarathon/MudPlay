using System.IO;
using MudPlay.Game.Combat;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins SpellAttackTypeIndex — the cast-code → AttType (damage element) lookup
// the resist guard reads. Keyed by Short (the cast-code the combat slots store),
// case-insensitive, fail-open (-1) on an unknown or missing cast-code.
public sealed class SpellAttackTypeIndexTests : IDisposable
{
    private readonly string _root;

    public SpellAttackTypeIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-atttype-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // lbol lightning bolt (AttType 3), mmis magic missile (4 Normal), pois a
    // poison spell (6), noatt a row with no AttType column (skipped), and a
    // null-Short row (skipped — no cast-code to key on).
    private const string SpellsJson = """
        [
          { "Number": 8,  "Name": "lightning bolt", "Short": "lbol", "AttType": 3 },
          { "Number": 1,  "Name": "magic missile",  "Short": "mmis", "AttType": 4 },
          { "Number": 90, "Name": "venom",          "Short": "pois", "AttType": 6 },
          { "Number": 50, "Name": "no att type",    "Short": "noatt" },
          { "Number": 60, "Name": "nameless",       "Short": null,   "AttType": 1 }
        ]
        """;

    private SpellAttackTypeIndex NewIndex(string set = "alpha", string json = SpellsJson)
    {
        Directory.CreateDirectory(Path.Combine(_root, set));
        File.WriteAllText(Path.Combine(_root, set, "Spells.json"), json);
        GameDataCache cache = new(_root);
        cache.SwitchSet(set);
        return new SpellAttackTypeIndex(cache);
    }

    [Fact]
    public void AttackType_KnownCastCode_ReturnsAttType()
    {
        SpellAttackTypeIndex s = NewIndex();
        Assert.Equal(3, s.AttackType("lbol"));
        Assert.Equal(4, s.AttackType("mmis"));
        Assert.Equal(6, s.AttackType("pois"));
    }

    [Fact]
    public void AttackType_IsCaseInsensitive()
    {
        SpellAttackTypeIndex s = NewIndex();
        Assert.Equal(3, s.AttackType("LBOL"));
    }

    [Fact]
    public void AttackType_UnknownCastCode_IsMinusOne()
    {
        SpellAttackTypeIndex s = NewIndex();
        Assert.Equal(-1, s.AttackType("nope"));
    }

    [Fact]
    public void AttackType_MissingAttTypeColumn_IsMinusOne()
    {
        // A row without an AttType column is never indexed → fail-open -1.
        SpellAttackTypeIndex s = NewIndex();
        Assert.Equal(-1, s.AttackType("noatt"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AttackType_NullOrBlank_IsMinusOne(string? castCode)
    {
        SpellAttackTypeIndex s = NewIndex();
        Assert.Equal(-1, s.AttackType(castCode));
    }

    // `blaz` is the Druid's blaze (#290: level 20, fire, any target) and the Priest's
    // blazing light (#5585: level 56, magic, undead only), as Paradigm 1.9.1 has them.
    // Asked with the character's own spell, that record answers; the code-wide answer
    // (the Priest's record, read for a Druid in report paradigm-20261024-195420) is
    // only the fallback for a code the character has no spell for.
    [Fact]
    public void SharedCastCode_TheCharactersOwnSpellAnswers()
    {
        const string json = """
            [
              { "Number": 290,  "Name": "blaze",         "Short": "blaz", "ReqLevel": 20, "AttType": 1, "Abil-0": 17, "Learnable": 1 },
              { "Number": 5567, "Name": "blazing comet", "Short": "blaz", "ReqLevel": 50, "AttType": 4, "Abil-0": 1,  "Learnable": 0 },
              { "Number": 5585, "Name": "blazing light", "Short": "blaz", "ReqLevel": 56, "AttType": 4, "Abil-0": 17, "Abil-1": 23, "Learnable": 1 }
            ]
            """;
        SpellAttackTypeIndex s = NewIndex(json: json);
        Assert.Equal(1, s.AttackType("blaz", 290));
        Assert.Equal(4, s.AttackType("blaz", 5585));
        Assert.Equal(4, s.AttackType("blaz"));
    }
}

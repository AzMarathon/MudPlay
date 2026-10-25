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

    // Anomaly's `blaz`: a Druid's blaze (#290, fire, level 20), a Priest's blazing light
    // (#5585, no element, undead only, level 56) and an item-cast blazing inferno (#159).
    private const string SharedBlazJson = """
        [
          { "Number": 290,  "Name": "blaze",           "Short": "blaz", "ReqLevel": 20, "Learnable": 1, "Magery": 3, "AttType": 1, "Abil-0": 17 },
          { "Number": 5585, "Name": "blazing light",   "Short": "blaz", "ReqLevel": 56, "Learnable": 1, "Magery": 2, "AttType": 4, "Abil-0": 17, "Abil-1": 23 },
          { "Number": 159,  "Name": "blazing inferno", "Short": "blaz", "ReqLevel": 0,  "Learnable": 0, "Magery": 0, "AttType": 1, "Abil-0": 17 }
        ]
        """;

    // A code two classes share is judged by the caster's own spell's element: the
    // Druid's blaze is fire, the Priest's blazing light has none (4). Unresolved, the
    // learnable rows disagree and the element is unknown (fail-open).
    [Theory]
    [InlineData(290, 1)]
    [InlineData(5585, 4)]
    [InlineData(null, -1)]
    public void SharedCastCode_ResolvesToTheCastersSpell(int? casterSpell, int expected)
    {
        SpellAttackTypeIndex s = NewIndex(json: SharedBlazJson);
        s.SetCasterSpell(_ => casterSpell);
        Assert.Equal(expected, s.AttackType("blaz"));
    }
}

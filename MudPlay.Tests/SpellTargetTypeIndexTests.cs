using System;
using System.IO;
using MudPlay.Game.Combat;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins SpellTargetTypeIndex — the cast-code → target-class restriction lookup the
// attack-spell cascade reads to skip a spell the target's type makes ineffective. The
// restriction is a MajorMUD ability slot on the Spells row: 23 AffectsUndeadOnly, 80
// AffectsAnimalsOnly, 108 AffectsLivingOnly. A spell with none affects all → Any.
public sealed class SpellTargetTypeIndexTests : IDisposable
{
    private readonly string _root;

    public SpellTargetTypeIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-targettype-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // harm — AffectsLivingOnly (108). turn — AffectsUndeadOnly (23). bane — AffectsAnimalsOnly
    // (80). mmis — a plain damage spell (no target tag). tag-in-later-slot puts the code in
    // Abil-3 to prove all slots are scanned. null-Short row is skipped.
    private const string SpellsJson = """
        [
          { "Number": 12, "Name": "harm",         "Short": "harm", "Abil-0": 17,  "Abil-1": 108 },
          { "Number": 18, "Name": "turn undead",  "Short": "turn", "Abil-0": 23 },
          { "Number": 40, "Name": "bane",         "Short": "bane", "Abil-3": 80 },
          { "Number": 1,  "Name": "magic missile","Short": "mmis", "Abil-0": 5 },
          { "Number": 60, "Name": "nameless",     "Short": null,   "Abil-0": 23 }
        ]
        """;

    private SpellTargetTypeIndex NewIndex(string set = "alpha", string json = SpellsJson)
    {
        Directory.CreateDirectory(Path.Combine(_root, set));
        File.WriteAllText(Path.Combine(_root, set, "Spells.json"), json);
        GameDataCache cache = new(_root);
        cache.SwitchSet(set);
        return new SpellTargetTypeIndex(cache);
    }

    [Theory]
    [InlineData("harm", SpellTargetType.LivingOnly)]
    [InlineData("turn", SpellTargetType.UndeadOnly)]
    [InlineData("bane", SpellTargetType.AnimalsOnly)]   // tag in a later Abil slot still found
    [InlineData("HARM", SpellTargetType.LivingOnly)]    // case-insensitive
    [InlineData("mmis", SpellTargetType.Any)]           // no target tag → affects all
    [InlineData("nope", SpellTargetType.Any)]           // unknown cast-code → fail-open
    public void TargetType_ResolvesRestriction(string castCode, SpellTargetType expected)
        => Assert.Equal(expected, NewIndex().TargetType(castCode));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TargetType_NullOrBlank_IsAny(string? castCode)
        => Assert.Equal(SpellTargetType.Any, NewIndex().TargetType(castCode));

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
        SpellTargetTypeIndex s = NewIndex(json: json);
        Assert.Equal(SpellTargetType.Any, s.TargetType("blaz", 290));
        Assert.Equal(SpellTargetType.UndeadOnly, s.TargetType("blaz", 5585));
        Assert.Equal(SpellTargetType.UndeadOnly, s.TargetType("blaz"));
    }
}

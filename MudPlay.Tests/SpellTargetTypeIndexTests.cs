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

    // Anomaly's `blaz`: a Druid's blaze (#290, fire, level 20), a Priest's blazing light
    // (#5585, no element, undead only, level 56) and an item-cast blazing inferno (#159).
    private const string SharedBlazJson = """
        [
          { "Number": 290,  "Name": "blaze",           "Short": "blaz", "ReqLevel": 20, "Learnable": 1, "Magery": 3, "AttType": 1, "Abil-0": 17 },
          { "Number": 5585, "Name": "blazing light",   "Short": "blaz", "ReqLevel": 56, "Learnable": 1, "Magery": 2, "AttType": 4, "Abil-0": 17, "Abil-1": 23 },
          { "Number": 159,  "Name": "blazing inferno", "Short": "blaz", "ReqLevel": 0,  "Learnable": 0, "Magery": 0, "AttType": 1, "Abil-0": 17 }
        ]
        """;

    // A code two classes share is the caster's own spell: the Druid's blaze affects all,
    // the Priest's blazing light only undead. With no spell book row to pick by, the
    // learnable rows disagree and the code isn't pre-empted.
    [Theory]
    [InlineData(290, SpellTargetType.Any)]
    [InlineData(5585, SpellTargetType.UndeadOnly)]
    [InlineData(null, SpellTargetType.Any)]
    public void SharedCastCode_ResolvesToTheCastersSpell(int? casterSpell, SpellTargetType expected)
    {
        SpellTargetTypeIndex s = NewIndex(json: SharedBlazJson);
        s.SetCasterSpell(_ => casterSpell);
        Assert.Equal(expected, s.TargetType("blaz"));
    }

    // A learnable spell's restriction isn't lifted by an item variant that affects all.
    [Fact]
    public void LearnableRestriction_BeatsAnUnrestrictedItemVariant()
    {
        const string json = """
            [
              { "Number": 18,  "Name": "turn undead", "Short": "turn", "Learnable": 1, "Abil-0": 23 },
              { "Number": 900, "Name": "turn (item)", "Short": "turn", "Learnable": 0, "Abil-0": 17 }
            ]
            """;
        Assert.Equal(SpellTargetType.UndeadOnly, NewIndex(json: json).TargetType("turn"));
    }
}

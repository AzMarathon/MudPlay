using System.IO;
using MudPlay.Game.Combat;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins CombatSpellIndex — the cast-code → is-combat-spell test, keyed on the round
// energy cost (Spells.EnergyCost). Combat/attack spells cost 1–1000; in-between
// (utility) spells cost 0. Keyed by Short, case-insensitive, false for unknown.
public sealed class CombatSpellIndexTests : IDisposable
{
    private readonly string _root;

    public CombatSpellIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-combatspell-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private const string SpellsJson = """
        [
          { "Number": 1,  "Name": "magic missile",  "Short": "mmis", "EnergyCost": 500 },
          { "Number": 2,  "Name": "vampiric touch",  "Short": "vamp", "EnergyCost": 1000 },
          { "Number": 3,  "Name": "mend",            "Short": "mend", "EnergyCost": 0 },
          { "Number": 4,  "Name": "bless",           "Short": "bles", "EnergyCost": 0 },
          { "Number": 5,  "Name": "min combat",      "Short": "min1", "EnergyCost": 1 },
          { "Number": 6,  "Name": "no energy col",   "Short": "noen" },
          { "Number": 7,  "Name": "nameless",        "Short": null,   "EnergyCost": 500 }
        ]
        """;

    private CombatSpellIndex NewIndex(string set = "alpha", string json = SpellsJson)
    {
        Directory.CreateDirectory(Path.Combine(_root, set));
        File.WriteAllText(Path.Combine(_root, set, "Spells.json"), json);
        GameDataCache cache = new(_root);
        cache.SwitchSet(set);
        return new CombatSpellIndex(cache);
    }

    [Fact]
    public void CombatSpells_EnergyOneToThousand_AreCombat()
    {
        CombatSpellIndex s = NewIndex();
        Assert.True(s.IsCombatSpell("mmis"));   // 500
        Assert.True(s.IsCombatSpell("vamp"));   // 1000
        Assert.True(s.IsCombatSpell("min1"));   // 1 (lower boundary)
        Assert.True(s.IsCombatSpell("MMIS"));   // case-insensitive
    }

    [Fact]
    public void InBetweenSpells_ZeroEnergy_AreNotCombat()
    {
        CombatSpellIndex s = NewIndex();
        Assert.False(s.IsCombatSpell("mend"));  // 0 — a heal
        Assert.False(s.IsCombatSpell("bles"));  // 0 — a buff
    }

    [Fact]
    public void UnknownOrMissing_IsNotCombat()
    {
        CombatSpellIndex s = NewIndex();
        Assert.False(s.IsCombatSpell("noen"));  // no EnergyCost column
        Assert.False(s.IsCombatSpell("zzz"));   // unknown cast-code
        Assert.False(s.IsCombatSpell(""));
        Assert.False(s.IsCombatSpell(null));
    }

    [Fact]
    public void AmbiguousCastCode_AnyCombatEntry_IsCombat()
    {
        // `vamp` = the player's vampiric touch (1000) plus monster vampiric-* dupes at
        // 0. The 0-energy ones are LAST, so a last-writer-wins map would misfile `vamp`
        // as in-between (the reported bug). Any-combat-wins keeps it a combat spell.
        const string json = """
            [
              { "Number": 10, "Name": "vampiric touch",  "Short": "vamp", "EnergyCost": 1000 },
              { "Number": 11, "Name": "vampiric hits",   "Short": "vamp", "EnergyCost": 0 },
              { "Number": 12, "Name": "vampiric bite",   "Short": "vamp", "EnergyCost": 0 },
              { "Number": 13, "Name": "lesser vamp ench", "Short": "lven", "EnergyCost": 0 }
            ]
            """;
        CombatSpellIndex s = NewIndex(json: json);
        Assert.True(s.IsCombatSpell("vamp"));    // ANY combat entry → combat
        Assert.False(s.IsCombatSpell("lven"));   // only a 0-energy entry → not combat
    }
}

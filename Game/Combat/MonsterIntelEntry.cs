using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MudPlay.Game.Combat;

// One row of the Monster Intel master list — a MonsterCatalogEntry plus the
// grid's display-formatted text, mirroring ItemFinderEntry's shape (a thin
// display projection over an already-typed catalog record, not a re-parse of
// raw game data).
public sealed record MonsterIntelEntry
{
    public required MonsterCatalogEntry Source { get; init; }

    public int Number => Source.Number;
    public string Name => Source.Name;
    public int Hp => Source.Hp;
    public int Exp => Source.Exp;
    public bool Undead => Source.Undead;

    // ----- grid display -----

    // Plain numeric properties for the master list's per-column sort — the
    // grid sorts on these (SortMemberPath), not on the formatted *Text
    // strings below, so "10" doesn't lexically sort ahead of "9".
    public int ArmourClass => Source.ArmourClass;
    public int DamageResist => Source.DamageResist;
    public int Dodge => Source.Dodge;
    public int MagicRes => Source.MagicRes;
    public int Magical => Source.Magical;
    public int Accuracy => Source.PhysicalAccuracy?.Majority ?? 0;

    public string AcDrText => $"{Source.ArmourClass}/{Source.DamageResist}";
    public string DodgeText => Source.Dodge > 0 ? Source.Dodge.ToString(Inv) : string.Empty;
    public string MagicResText => Source.MagicRes.ToString(Inv);
    public string AccuracyText => Source.PhysicalAccuracy is { } acc
        ? (acc.Majority == acc.Max ? acc.Majority.ToString(Inv) : $"{acc.Majority}/{acc.Max}")
        : string.Empty;
    public string ExpText => Exp > 0 ? Exp.ToString("N0", Inv) : string.Empty;
    public string HpText => Hp > 0 ? Hp.ToString("N0", Inv) : string.Empty;
    public string UndeadText => Undead ? "✗" : string.Empty;
    public string MagicalText => Source.Magical > 0 ? Source.Magical.ToString(Inv) : string.Empty;
    public string SpellImmuneText => Source.SpellImmunity > 0 ? Source.SpellImmunity.ToString(Inv) : string.Empty;

    // "Fire 80, Cold -20" — blank when the monster carries no elemental resist
    // ability. Negative values (vulnerabilities) render with their sign so they
    // read unambiguously next to the positive (resist) entries.
    public string ResistsText => Source.ElementalResists.Count == 0
        ? string.Empty
        : string.Join(", ", Source.ElementalResists
            .Select(kv => $"{ElementalResistIndex.ElementName(kv.Key)} {kv.Value:+0;-0;0}"));

    public string CastsText => Source.CastsElements.Count == 0
        ? string.Empty
        : string.Join(", ", Source.CastsElements);

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static IReadOnlyList<MonsterIntelEntry> BuildCatalog(MonsterCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.All
            .Select(static e => new MonsterIntelEntry { Source = e })
            .OrderBy(static e => e.Name, System.StringComparer.OrdinalIgnoreCase)
            .ThenBy(static e => e.Number)
            .ToList();
    }
}

// Ability-code → element-name map for the five elemental resists, shared by
// ResistsText above and the Elemental Defenses detail panel — the same codes
// MonsterResistIndex/MonsterCatalog already key their resist dictionary on.
internal static class ElementalResistIndex
{
    public static string ElementName(int resistCode) => resistCode switch
    {
        3 => "Cold",
        5 => "Fire",
        65 => "Stone",
        66 => "Lightning",
        147 => "Water",
        _ => $"Ability {resistCode}",
    };
}

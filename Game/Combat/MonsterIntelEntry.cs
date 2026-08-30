using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MudPlay.Game.Combat;

// One row of the Monster Intel master list — a MonsterCatalogEntry plus the
// grid's display-formatted text, mirroring ItemFinderEntry's shape (a thin
// display projection over an already-typed catalog record, not a re-parse of
// raw game data). Deliberately narrow: Monster Intel is a fast pre-fight
// check, not a monster database browser, so this carries only what the
// master list and its filters need — not the full record (that's the Game
// Data Browser's Monsters tab).
public sealed record MonsterIntelEntry
{
    public required MonsterCatalogEntry Source { get; init; }

    public int Number => Source.Number;
    public string Name => Source.Name;
    public int Hp => Source.Hp;
    public string HpText => Hp > 0 ? Hp.ToString("N0", Inv) : string.Empty;

    // Sole gate for the Hittable filter: keep only if the equipped weapon's
    // HitMagic meets or exceeds this. Plain int (not Source.Magical) so the
    // grid sorts on the number, not lexically on a formatted string.
    public int Magical => Source.Magical;

    // Chance this monster's own attack lands on the current character, given
    // their live AC/Dodge/wards — the one field on this record that ISN'T a
    // pure projection of Source, since it depends on live player state rather
    // than just this monster's own data. Set (for every entry at once) by
    // MonsterIntelViewModel.RebuildCharacterCapabilities whenever gear
    // changes. -1 = no character context, or the monster has no catalogued
    // physical attack to compute against.
    public int IncomingHitPercent { get; set; } = -1;
    public string IncomingHitPercentText => IncomingHitPercent >= 0 ? $"{IncomingHitPercent}%" : string.Empty;

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

// Ability-code -> element-name map for the five elemental resists — Your
// Matchup's incoming-threat lookup uses CodeForName to match a monster's
// CastsElements name back to the resist code its own gear tracks under.
internal static class ElementalResistIndex
{
    private static readonly (int Code, string Name)[] Elements =
    {
        (3, "Cold"), (5, "Fire"), (65, "Stone"), (66, "Lightning"), (147, "Water"),
    };

    // A display name back to its resist ability code, or -1 for a
    // non-elemental name (Normal, Poison — never resist-indexed, see
    // MonsterResistIndex's own comment).
    public static int CodeForName(string element)
    {
        foreach ((int code, string name) in Elements)
            if (string.Equals(name, element, System.StringComparison.Ordinal)) return code;
        return -1;
    }
}

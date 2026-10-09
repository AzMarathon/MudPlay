using System.Text.Json;
using MudPlay.Game.Calculators;
using MudPlay.Models.GameData;

namespace MudPlay.Game.Spells;

// Turns the effect lines a `stat` screen listed into what each could be doing to
// the six trainable stats. The screen lists every effect on the character whoever
// cast it, so this is also how a buff from another player is recognised: its line
// is matched to the spells whose applied text it is, and each spell's Strength /
// Intellect / Willpower / Agility / Health / Charm abilities are read off the
// Spells table.
public static class ListedEffectReader
{
    // Ability code of each stat, in STR/INT/WIL/AGL/HEA/CHM order.
    private static readonly int[] StatAbility = { 46, 44, 45, 48, 47, 49 };

    public static IReadOnlyList<ListedEffect> Read(
        IReadOnlyList<StatusEffectLine> lines, RealmType realm,
        IEnumerable<MessageRecord> messages, Func<int, JsonElement?> spellRow)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(spellRow);

        List<(string Text, string Key, List<int> Spells)> wanted = new();
        foreach (StatusEffectLine line in lines)
        {
            // Paradigm puts a countdown on every effect; a line without one came from
            // somewhere else and landed inside the screen.
            if (realm == RealmType.ParaMud && !line.Timed) continue;
            wanted.Add((line.Text, Normalize(line.Text), new List<int>()));
        }
        if (wanted.Count == 0) return Array.Empty<ListedEffect>();

        foreach (MessageRecord record in messages)
        {
            if (record.Flags.HasFlag(MessageFlags.Disabled) || record.Links is null) continue;
            if (MessageRecord.IsBlankOrAbsent(record.AppliedMessage)) continue;
            string applied = Normalize(record.AppliedMessage);
            foreach ((_, string key, List<int> spells) in wanted)
            {
                if (!string.Equals(key, applied, StringComparison.Ordinal)) continue;
                foreach (GameDataLink link in record.Links)
                    if (string.Equals(link.Table, "Spells", StringComparison.OrdinalIgnoreCase)
                        && !spells.Contains(link.Number))
                        spells.Add(link.Number);
            }
        }

        List<ListedEffect> effects = new(wanted.Count);
        foreach ((string text, _, List<int> spells) in wanted)
        {
            List<EffectStatReading> readings = new();
            foreach (int number in spells)
                if (spellRow(number) is { } row) readings.Add(StatModifiers(row));
            effects.Add(new ListedEffect(text, readings));
        }
        return effects;
    }

    // A spell's stat abilities. A non-zero ability value is the modifier itself. A
    // zero takes the spell's own magnitude, which is only a known number when the
    // spell has a single fixed one: a min-max range is rolled at the cast, and one
    // that grows per level depends on who cast it and when.
    public static EffectStatReading StatModifiers(JsonElement spell)
    {
        SpellFormulaInput formula = SpellFormulaReader.Read(spell);
        bool fixedMagnitude = formula.MinBase == formula.MaxBase && formula.MinInc == 0 && formula.MaxInc == 0;
        int[] modifiers = new int[StatAbility.Length];
        StatSet unknown = StatSet.None;
        foreach (SpellAbility ability in formula.Abilities)
        {
            int stat = Array.IndexOf(StatAbility, ability.Code);
            if (stat < 0) continue;
            if (ability.Value != 0) modifiers[stat] += ability.Value;
            else if (fixedMagnitude) modifiers[stat] += formula.MinBase;
            else unknown |= (StatSet)(1 << stat);
        }
        string name = spell.TryGetProperty("Name", out JsonElement n) && n.ValueKind == JsonValueKind.String
            ? n.GetString() ?? string.Empty
            : string.Empty;
        return new EffectStatReading(name.Length > 0 ? name : $"spell {formula.Number}", modifiers, unknown);
    }

    // The readout prints the applied text whole, but the catalogue's copy may or
    // may not carry the closing punctuation.
    private static string Normalize(string text) => text.Trim().TrimEnd('.', '!').TrimEnd();
}

using System.Collections.Generic;
using System.Text.Json;
using MudPlay.Services;

namespace MudPlay.Game.Spells;

// Projects a Spells-table row into a SpellFormulaInput — the level-scaling
// fields (Min/Max base + slope, duration, cap, energy, mana) plus the 10
// Abil-N / AbilVal-N slots. Shared so the class spell-book catalog and the
// monster catalog read a spell's formula the same way instead of each
// hand-rolling the identical field list.
public static class SpellFormulaReader
{
    private const int AbilitySlots = 10;
    private static readonly string[] AbilFields = NumberedFields.Of("Abil-", AbilitySlots);
    private static readonly string[] AbilValFields = NumberedFields.Of("AbilVal-", AbilitySlots);

    public static SpellFormulaInput Read(GameRow row)
    {
        List<SpellAbility> abilities = new();
        for (int x = 0; x < AbilitySlots; x++)
        {
            int code = ReadInt(row, AbilFields[x]);
            if (code == 0) continue; // empty slot — the calculator ignores code 0 anyway
            abilities.Add(new SpellAbility(code, ReadInt(row, AbilValFields[x])));
        }

        return new SpellFormulaInput
        {
            Number = ReadInt(row, "Number"),
            MinBase = ReadInt(row, "MinBase"),
            MinInc = ReadInt(row, "MinInc"),
            MinIncLVLs = ReadInt(row, "MinIncLVLs"),
            MaxBase = ReadInt(row, "MaxBase"),
            MaxInc = ReadInt(row, "MaxInc"),
            MaxIncLVLs = ReadInt(row, "MaxIncLVLs"),
            Dur = ReadInt(row, "Dur"),
            DurInc = ReadInt(row, "DurInc"),
            DurIncLVLs = ReadInt(row, "DurIncLVLs"),
            Cap = ReadInt(row, "Cap"),
            ReqLevel = ReadInt(row, "ReqLevel"),
            EnergyCost = ReadInt(row, "EnergyCost"),
            ManaCost = ReadInt(row, "ManaCost"),
            Diff = ReadInt(row, "Diff"),
            AttType = ReadInt(row, "AttType"),
            TypeOfResists = ReadInt(row, "TypeOfResists"),
            Abilities = abilities,
        };
    }

    private static int ReadInt(GameRow row, string property)
    {
        if (!row.TryGetProperty(property, out GameValue el)) return 0;
        if (el.ValueKind != JsonValueKind.Number) return 0;
        return el.TryGetInt32(out int n) ? n : 0;
    }
}

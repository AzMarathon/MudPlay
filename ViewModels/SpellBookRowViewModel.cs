using MudPlay.Game.Spells;

namespace MudPlay.ViewModels;

// One immutable row in the SpellBookViewModel table: a single KnownSpell
// from the class's learnable list, paired with whether the character has
// obtained it and the level-scaled effect / mana figures (via
// SpellCalculator) for the book's current level.
//
// Rows are throwaway — the window rebuilds the whole collection whenever
// SpellbookState.Changed fires (class swap, level change, or obtained-set
// update), so there's no per-row change notification.
public sealed class SpellBookRowViewModel
{
    public SpellBookRowViewModel(
        KnownSpell spell,
        bool isObtained,
        int level,
        Func<int, SpellFormulaInput?> resolveChain,
        Func<int, string?>? resolveSpellName = null,
        Func<int, IReadOnlyList<KnownSpell>>? resolveTextblockCasts = null,
        int teachLevel = 0,
        int spellcasting = 0)
    {
        Number = spell.Number;
        Short = spell.Short;
        Name = spell.Name;
        ReqLevel = spell.ReqLevel;
        EffectiveLevel = System.Math.Max(spell.ReqLevel, teachLevel);
        IsObtained = isObtained;

        // Cast-success chance for THIS character (Spellcasting stat + the spell's
        // Diff), null when we can't state one — a non-caster class, or the stat
        // line hasn't been read yet (Spellcasting 0). Level plays no part.
        bool isKai = spell.Magery == SpellCastChance.KaiMagery;
        int? success = SpellCastChance.Compute(spellcasting, spell.Formula.Diff, isKai);
        DifficultySort = success ?? -1;
        DifficultyText = success is { } pct
            ? $"{pct.ToString(System.Globalization.CultureInfo.InvariantCulture)}%"
            : "—";
        DifficultyTooltip = BuildDifficultyTooltip(spellcasting, spell.Formula.Diff, isKai, success);

        Mana = SpellCalculator.ManaCost(spell.Formula);
        ManaText = Mana.ToString();
        EffectText = SpellEffectFormatter.Format(
            spell.Formula, level, resolveChain, resolveSpellName, resolveTextblockCasts);
        FormulaText = BuildFormula(spell.Formula);
    }

    // The spell's Spells.Number — used to look up the item that teaches it.
    public int Number { get; }

    // The verbatim Spells.Short cast-code the player types.
    public string Short { get; }

    // The full Spells.Name.
    public string Name { get; }

    // Level the spell unlocks at (Spells.ReqLevel) — the raw value, not necessarily
    // when THIS class can learn it (see EffectiveLevel).
    public int ReqLevel { get; }

    // The level THIS class can actually learn the spell — the higher of the spell's
    // ReqLevel and any trainer `minlevel` gate for the class (from TBInfo). For most
    // spells this equals ReqLevel; for trainer-gated ones (e.g. a Paladin's divine
    // disfavour) it's the real, higher unlock level. Drives the Lvl column + the
    // level filter so the book doesn't show a spell as available before you can learn it.
    public int EffectiveLevel { get; }

    // True when the character has learned this spell.
    public bool IsObtained { get; }

    // Checkmark glyph for the obtained column ("✓" or empty).
    public string ObtainedGlyph => IsObtained ? "✓" : string.Empty;

    // The effective unlock level as a bare number — the Lvl cell.
    public string ReqLevelText => EffectiveLevel.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // Cast-success chance for this character as "95%", or "—" when none can be
    // stated (non-caster, or Spellcasting not yet parsed). Column header is
    // "Difficulty" — the spell's static Diff turned into the caster's real
    // landing chance. See SpellCastChance.
    public string DifficultyText { get; }

    // Success percent for column sorting; -1 for the "—" rows so they group.
    public int DifficultySort { get; }

    // Hover breakdown for the Difficulty cell: the equation with THIS spell's
    // actual numbers (Spellcasting + its difficulty), including any clamp.
    public string DifficultyTooltip { get; }

    // "Spellcasting 94 + spell difficulty (-5) = 89%" with the clamp spelled out,
    // or the reason there's no chance to state.
    private static string BuildDifficultyTooltip(int spellcasting, int diff, bool isKai, int? success)
    {
        if (success is null)
            return "No cast chance yet — not a caster class, or your stats haven't been read "
                + "(type `stat` in the game, then reopen the book).";
        if (diff >= SpellCastChance.AlwaysSucceedsDiff)
            return "Always succeeds — a utility spell that never fizzles.";

        var c = System.Globalization.CultureInfo.InvariantCulture;
        int cap = isKai ? SpellCastChance.KaiCap : SpellCastChance.StockCap;
        int raw = spellcasting + diff;
        string signedDiff = diff >= 0 ? $"+{diff.ToString(c)}" : diff.ToString(c);
        string line = $"Spellcasting {spellcasting.ToString(c)} + spell difficulty ({signedDiff})"
            + $" = {raw.ToString(c)}%";
        if (raw > cap) line += $"  →  capped at {cap.ToString(c)}%";
        else if (raw < 0) line += "  →  floored at 0%";
        return line;
    }

    // Per-round mana cost — numeric, for column sorting.
    public long Mana { get; }

    // Per-round mana cost at the spell's energy multiplier.
    public string ManaText { get; }

    // Level-scaled effect at the book's current level: "Dmg 14–22", "Heal
    // 30–45", "Dur 8", plus any decoded stat-affect abilities the spell
    // grants ("AC +10", "Strength +3"), joined by " · ". "—" when the spell
    // produces no figure at all. See SpellEffectFormatter.
    public string EffectText { get; }

    // The raw scaling formula (base value + per-level slope) shown as a
    // tooltip so the player can see how the effect grows, independent of the
    // current level. Empty when the spell has no scaling magnitude.
    public string FormulaText { get; }

    private static string BuildFormula(in SpellFormulaInput formula)
    {
        List<string> parts = new();
        if (formula.MinBase != 0 || formula.MaxBase != 0)
            parts.Add($"base {formula.MinBase}–{formula.MaxBase}");
        if (formula.MinIncLVLs > 0 && formula.MinInc != 0)
            parts.Add($"min +{formula.MinInc}/{formula.MinIncLVLs}lv");
        if (formula.MaxIncLVLs > 0 && formula.MaxInc != 0)
            parts.Add($"max +{formula.MaxInc}/{formula.MaxIncLVLs}lv");
        if (formula.Dur != 0 || (formula.DurIncLVLs > 0 && formula.DurInc != 0))
        {
            string slope = formula.DurIncLVLs > 0 && formula.DurInc != 0
                ? $" +{formula.DurInc}/{formula.DurIncLVLs}lv"
                : string.Empty;
            parts.Add($"dur {formula.Dur}{slope}");
        }
        return string.Join(", ", parts);
    }
}

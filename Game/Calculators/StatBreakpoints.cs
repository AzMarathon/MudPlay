namespace MudPlay.Game.Calculators;

// The per-stat breakpoint tables behind Character Info's clickable stat labels: for
// one base stat, every sub-stat it feeds and the stat values where its share of that
// sub-stat changes. The terms are each stat's own share of the formulas in
// CharacterCalculator / CombatCalculator / StatEffects (GAME_MECHANICS "Character
// stats & progression"). They're written out per stat rather than read back from
// those calculators because the calculators floor, cap and round the SUM, which hides
// a single stat's steps; StatBreakpointsTests pins each term against the calculator
// it comes from so the two can't drift.
public static class StatBreakpoints
{
    public const int MinStat = 30;
    public const int DefaultMaxStat = 200;

    // Columns for one stat, in the loaded realm's formulas. level only feeds HP regen.
    // ctx gates the class-specific columns the same way the CP tooltips do: a thief
    // skill shows only when the class or race has it, spellcasting only under the
    // class's casting stats. maxStat stretches the table past 200 for a stat above it.
    public static IReadOnlyList<StatBreakpointColumn> For(BaseStat stat, StatContext ctx, int level,
                                                          int maxStat = DefaultMaxStat)
    {
        bool para = ctx.Realm == RealmType.ParaMud;
        int hi = Math.Max(maxStat, DefaultMaxStat);
        var cols = new List<StatBreakpointColumn>();

        void Add(string label, string formula, Func<int, int> term, bool approx = false,
                 bool stockOnParadigm = false, string unit = "", bool signed = true)
            => cols.Add(new StatBreakpointColumn(label, formula, Steps(term, hi), approx,
                para && stockOnParadigm, unit, signed));

        switch (stat)
        {
            case BaseStat.Agility:
                Add("Dodge", "(AGI − 50) ÷ 3", v => (v - 50) / 3);
                if (para) Add("Accuracy", "(AGI − 50) ÷ 3", v => (v - 50) / 3);
                Add(para ? "Bash accuracy" : "Accuracy", "(AGI − 50) ÷ 6", v => (v - 50) / 6);
                Add("Crit", "(AGI − 50) ÷ 20", v => (v - 50) / 20);
                if (para) Add("Stealth", "AGI ÷ 4, rounded with INT and CHA", v => v / 4, approx: true);
                else Add("Stealth", "AGI ÷ 4", v => v / 4);
                if (para) Add("BS accuracy", "(AGI − 50 + level) ÷ 2", v => (v - 50) / 2, approx: true);
                else Add("BS accuracy", "(stealth + AGI) ÷ 2", v => v / 2, approx: true);
                if (ctx.HasThievery) Add("Thievery", "AGI ÷ 6", v => v / 6, approx: true, stockOnParadigm: true);
                if (ctx.HasTraps) Add("Traps", "AGI ÷ 7", v => v / 7, approx: true, stockOnParadigm: true);
                if (ctx.HasPicklocks)
                {
                    if (para) Add("Picklocks", "AGI ÷ 7", v => v / 7, approx: true);
                    else Add("Picklocks", "AGI × 2 ÷ 7", v => v * 2 / 7, approx: true);
                }
                break;

            case BaseStat.Charm:
                Add("Dodge", "(CHA − 50) ÷ 5", v => (v - 50) / 5);
                if (para) Add("Accuracy", "(CHA − 50) ÷ 10", v => (v - 50) / 10);
                Add("Crit", "(CHA − 50) ÷ 30", v => (v - 50) / 30);
                if (para) Add("Stealth", "CHA ÷ 6, rounded with AGI and INT", v => v / 6, approx: true);
                else Add("Stealth", "CHA ÷ 6", v => v / 6);
                Add("Perception", "CHA ÷ 8", v => v / 8, approx: true, stockOnParadigm: true);
                if (ctx.HasTraps) Add("Traps", "CHA × 2 ÷ 7", v => v * 2 / 7, approx: true, stockOnParadigm: true);
                if (ctx.HasPicklocks && para) Add("Picklocks", "CHA × 2 ÷ 7", v => v * 2 / 7, approx: true);
                if (ctx.HasThievery) Add("Thievery", "CHA ÷ 6", v => v / 6, approx: true, stockOnParadigm: true);
                if (ctx.HasTracking) Add("Tracking", "CHA ÷ 8", v => v / 8, approx: true, stockOnParadigm: true);
                if (ctx.MageryType == 4) Add("Spellcasting", "CHA × 3 ÷ 6", v => v / 2, approx: true);
                if (para) Add("Aggro weight", "10 − CHA ÷ 5 (lower: picked less)", v => 10 - v / 5, signed: false);
                Add("Buy price", "−(CHA ÷ 5 − 10)%", v => -(v / 5 - 10), unit: "%");
                if (para) Add("Sell price", "half value, ± (CHA − 50) ÷ 5 %", v => (v - 50) / 5, unit: "%");
                else Add("Sell price", "(CHA ÷ 2 + 25)% of value", v => v / 2 + 25, unit: "%", signed: false);
                break;

            case BaseStat.Intellect:
                if (para) Add("Accuracy", "(INT − 50) ÷ 6", v => (v - 50) / 6);
                Add("Crit", "(INT − 50) ÷ 10", v => (v - 50) / 10);
                if (para) Add("Stealth", "INT ÷ 8, rounded with AGI and CHA", v => v / 8, approx: true);
                else Add("Stealth", "INT ÷ 8", v => v / 8);
                Add("Magic res", "INT ÷ 4", v => v / 4, approx: true);
                Add("Perception", "INT × 5 ÷ 8", v => v * 5 / 8, approx: true, stockOnParadigm: true);
                if (ctx.HasThievery) Add("Thievery", "INT ÷ 6", v => v / 6, approx: true, stockOnParadigm: true);
                if (ctx.HasTraps) Add("Traps", "INT ÷ 7", v => v / 7, approx: true, stockOnParadigm: true);
                if (ctx.HasPicklocks)
                {
                    if (para) Add("Picklocks", "INT ÷ 7", v => v / 7, approx: true);
                    else Add("Picklocks", "INT × 2 ÷ 7", v => v * 2 / 7, approx: true);
                }
                if (ctx.HasTracking) Add("Tracking", "INT ÷ 4", v => v / 4, approx: true, stockOnParadigm: true);
                switch (ctx.MageryType)
                {
                    case 1: Add("Spellcasting", "INT × 3 ÷ 6", v => v / 2, approx: true); break;
                    case 2: Add("Spellcasting", "INT ÷ 6", v => v / 6, approx: true); break;
                    case 3: Add("Spellcasting", "INT ÷ 3", v => v / 3, approx: true); break;
                }
                break;

            case BaseStat.Strength:
                if (!para) Add("Accuracy", "(STR − 50) ÷ 3", v => (v - 50) / 3);
                if (para)
                {
                    Add("Min damage", "(STR − 100) ÷ 10, never below 0", v => Math.Max(0, (v - 100) / 10));
                    Add("Max damage", "(STR − 50) ÷ 10, never below 0", v => Math.Max(0, (v - 50) / 10));
                    Add("Bash accuracy", "(STR − 50) ÷ 3", v => (v - 50) / 3);
                }
                else
                {
                    Add("Min damage", "2 × ((STR − 100) ÷ 10), never below 0", v => Math.Max(0, (v - 100) / 10 * 2));
                    Add("Max damage", "(STR − 50) ÷ 10", v => (v - 50) / 10);
                }
                break;

            case BaseStat.Health:
                int divisor = para ? 500 : 750;
                Add("HP / tick", $"({level} + 20) × HEA ÷ {divisor}, at least 1",
                    v => Math.Max(1, (level + 20) * v / divisor), signed: false);
                break;

            case BaseStat.Willpower:
                Add("Magic res", "WIL × 3 ÷ 4", v => v * 3 / 4, approx: true);
                Add("Perception", "WIL ÷ 4", v => v / 4, approx: true, stockOnParadigm: true);
                if (ctx.HasTracking) Add("Tracking", "WIL ÷ 8", v => v / 8, approx: true, stockOnParadigm: true);
                switch (ctx.MageryType)
                {
                    case 1: case 4: Add("Spellcasting", "WIL ÷ 6", v => v / 6, approx: true); break;
                    case 2: Add("Spellcasting", "WIL × 3 ÷ 6", v => v / 2, approx: true); break;
                    case 3: Add("Spellcasting", "WIL ÷ 3", v => v / 3, approx: true); break;
                }
                break;
        }
        return cols;
    }

    // What each stat feeds that changes on every point, so it has no table.
    public static string EveryPointNote(BaseStat stat, RealmType realm) => stat switch
    {
        BaseStat.Strength => "Also: carry weight, +48 a point up to 100 STR and +84 a point above.",
        BaseStat.Health => "Also: max HP, which rises nearly every point, faster the higher your level.",
        BaseStat.Intellect => "Also: mana regen for mages and druids.",
        BaseStat.Willpower => "Also: mana regen for priests and druids. WIL adds nothing to combat.",
        BaseStat.Charm => "Also: mana regen for bards.",
        _ => "",
    };

    private static List<StatBreakpointColumn.Step> Steps(Func<int, int> term, int hi)
    {
        var steps = new List<StatBreakpointColumn.Step>();
        int prev = 0;
        for (int v = MinStat; v <= hi; v++)
        {
            int y = term(v);
            if (v == MinStat || y != prev) steps.Add(new StatBreakpointColumn.Step(v, y));
            prev = y;
        }
        return steps;
    }
}

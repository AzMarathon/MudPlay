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

    // Columns for one stat, in the loaded realm's formulas. level feeds HP, HP regen,
    // mana regen and swing energy. ctx gates the class-specific columns the same way
    // the CP tooltips do: a thief skill shows only when the class or race has it,
    // spellcasting and mana regen only under the class's casting stats. maxStat
    // stretches the table past 200 for a stat above it. stats is the character's own
    // six, for the columns that hold the other stats still (a Druid's second mana
    // stat, the strength under a swing); swing is what the character swings with, and
    // without it Agility has no swing-energy column.
    public static IReadOnlyList<StatBreakpointColumn> For(BaseStat stat, StatContext ctx, int level,
                                                          int maxStat = DefaultMaxStat,
                                                          StatBlock stats = default, SwingBasis? swing = null)
    {
        bool para = ctx.Realm == RealmType.ParaMud;
        int hi = Math.Max(maxStat, DefaultMaxStat);
        var cols = new List<StatBreakpointColumn>();
        int manaFactor = ctx.MageryLevel + 2;
        bool hasMana = ctx.MageryLevel > 0;

        // What a 30 s mana tick pays before ManaRgn%, with this one stat stepped.
        int Mana(int intellect, int willpower, int charm) => CharacterCalculator.CalcManaRegen(
            level, intellect, willpower, charm, ctx.MageryType, ctx.MageryLevel, 0, false, ctx.Realm);

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
                if (swing is { Speed: > 0 } sw && ctx.ClassCombatLvl > 0)
                    Add("Energy / swing",
                        $"{sw.Speed} × 1000 ÷ (({level} × {ctx.ClassCombatLvl} + 45) × (AGI + 150) ÷ 6), "
                        + $"{sw.Source}, at {sw.EncumPercent}% load; 1000 ÷ energy = swings a round",
                        v => CombatCalculator.CalcEnergyUsed(ctx.ClassCombatLvl, level, sw.Speed, v,
                            stats.Strength, sw.StrReq, sw.EncumPercent),
                        signed: false);
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
                if (ctx.MageryType == 4 && hasMana)
                    Add("Mana / tick", $"({level} + 20) × CHA × {manaFactor} ÷ 1650", v => Mana(0, 0, v), signed: false);
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
                if (ctx.MageryType == 1 && hasMana)
                    Add("Mana / tick", $"({level} + 20) × INT × {manaFactor} ÷ 1650", v => Mana(v, 0, 0), signed: false);
                if (ctx.MageryType == 3 && hasMana) AddDruidMana("INT", "WIL", stats.Willpower, v => Mana(v, stats.Willpower, 0));
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
                Add("Carry weight", "STR × 48, plus STR × 36 − 3600 above 100",
                    CharacterCalculator.CalcMaxEncumbrance, signed: false);
                break;

            case BaseStat.Health:
                int divisor = para ? 500 : 750;
                Add("HP / tick", $"({level} + 20) × HEA ÷ {divisor}, at least 1",
                    v => Math.Max(1, (level + 20) * v / divisor), signed: false);
                Add("Max HP", $"HEA ÷ 2 + (HEA − 50) × {level} ÷ 16", v => v / 2 + (v - 50) * level / 16);
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
                if (ctx.MageryType == 2 && hasMana)
                    Add("Mana / tick", $"({level} + 20) × WIL × {manaFactor} ÷ 1650", v => Mana(0, v, 0), signed: false);
                if (ctx.MageryType == 3 && hasMana) AddDruidMana("WIL", "INT", stats.Intellect, v => Mana(stats.Intellect, v, 0));
                break;
        }
        return cols;

        // A Druid's mana stat is the average of INT and WIL, so one stat's column
        // holds the other at the character's own value. Before a `stat` read there is
        // no other value, and the column shows this stat's half alone.
        void AddDruidMana(string name, string otherName, int other, Func<int, int> term) =>
            Add("Mana / tick",
                other > 0
                    ? $"({level} + 20) × (({name} + {otherName}) ÷ 2) × {manaFactor} ÷ 1650, at your {otherName} {other}"
                    : $"({level} + 20) × ({name} ÷ 2) × {manaFactor} ÷ 1650",
                term, approx: other <= 0, signed: false);
    }

    // A line under the tables: what a column there leaves unsaid.
    public static string Note(BaseStat stat, StatContext ctx) => stat switch
    {
        BaseStat.Willpower => "WIL adds nothing to combat." + ManaNote(ctx, 2, 3),
        BaseStat.Intellect => ManaNote(ctx, 1, 3).TrimStart(),
        BaseStat.Charm => ManaNote(ctx, 4).TrimStart(),
        BaseStat.Health => "Max HP is HEA's own share; your class and race hit points per level come on top.",
        _ => "",
    };

    private static string ManaNote(StatContext ctx, params int[] mageryTypes) =>
        ctx.MageryLevel > 0 && mageryTypes.Contains(ctx.MageryType)
            ? " Mana / tick is the base amount: the 30-second tick pays it scaled by your mana-regen bonus (ManaRgn%), and a meditate tick pays it as it is."
            : "";

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

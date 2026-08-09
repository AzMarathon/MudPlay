namespace FujinTerm.Game.Calculators;

// Mana-regen breakpoint planning math for casters (mage / druid). The natural,
// non-resting, non-meditating per-tick amount is CharacterCalculator.CalcManaRegen
// — the engine-verified formula the Level Projection grid already trusts. Because
// that value is integer-truncated, it steps up by whole MP at discrete +ManaRgn%
// thresholds; those steps ARE the "breakpoints" (there is no separate table).
//
// A code-145 roll spell (nature tap / mana flux) rolls a magnitude from its
// level-scaled [Min,Max] range each cast, and that magnitude adds straight into
// the +ManaRgn% total (it is a percent modifier, not flat mana). So the question
// "what roll pushes me over the next tick, and where should I stop rerolling?"
// reduces to: sweep the roll value across [Min,Max] and find where the tick steps
// up, then map each step back to the roll value — and its 0-100% position in the
// range — needed to reach it. Anything the range can't reach is a reroll that
// only burns mana for no extra tick, which is exactly what a planner wants to see.
//
// Pure and gear-independent of any UI: the caller supplies the resolved inputs
// (level, magery type/level, stats, gear +ManaRgn%, realm) and the spell's
// level-scaled roll range; SpellCalculator.AffectMagnitude produces that range.
public static class ManaRegenBreakpointCalculator
{
    // Passive (non-resting) mana-regen tick cadence — one tick every 30 s / 6
    // rounds. The client observes this as "MP +N after ~30s"; used only to render
    // a per-minute rate, never in the tick amount itself.
    public const int PassiveTickSeconds = 30;

    public readonly record struct Inputs(
        int Level,
        int MageryType,        // 1 = mage (INT), 3 = druid ((INT+WIL)/2)
        int Intellect,
        int Willpower,
        int MageryLevel,       // class magery tier (Classes.MageryLVL), constant per class
        int GearRegenPercent,  // summed +ManaRgn% from gear / quests (code 145), no spell
        RealmType Realm);

    // One reachable tick step the roll spell can push us to: the tick it yields,
    // the total +ManaRgn% and roll value it needs, and where that roll sits in the
    // spell's [Min,Max] range (0 = worst roll, 1 = best roll).
    public readonly record struct Breakpoint(
        int Tick, int RegenPercentNeeded, int RollValueNeeded, double RollFractionOfRange);

    public readonly record struct Result(
        int BaseTick,      // level + stat only (no gear, no spell)
        int GearTick,      // + gear/quest +ManaRgn% (no spell)
        int WorstTick,     // + gear + spell rolling its minimum
        int BestTick,      // + gear + spell rolling its maximum
        int RollMin,       // level-scaled spell roll range
        int RollMax,
        IReadOnlyList<Breakpoint> Breakpoints,   // steps in (WorstTick, BestTick]
        int? RecommendedRollThreshold);          // suggested reroll target (roll value), or null when no step is reachable

    // Per-tick amount at a given rolled +ManaRgn% (0 = no spell). Gear is always
    // folded in; the engine formula truncates, so this is the whole-MP tick.
    public static int Tick(in Inputs i, int rolledRegenPercent)
        => CharacterCalculator.CalcManaRegen(
            i.Level, i.Intellect, i.Willpower, charm: 0,
            i.MageryType, i.MageryLevel, i.GearRegenPercent + rolledRegenPercent,
            isMeditating: false, i.Realm);

    // recommendCutoff: the highest roll fraction we'll suggest chasing. A step that
    // needs a roll past this (e.g. > 75% of the range) costs too many rerolls per
    // extra tick to be worth it, so the recommendation stops at the best step
    // reachable within the cutoff. The full ladder is still returned so the caller
    // can show every step and let the player judge.
    public static Result Compute(in Inputs i, int rollMin, int rollMax, double recommendCutoff = 0.75)
    {
        if (rollMax < rollMin) (rollMin, rollMax) = (rollMax, rollMin);

        int baseTick = CharacterCalculator.CalcManaRegen(
            i.Level, i.Intellect, i.Willpower, charm: 0,
            i.MageryType, i.MageryLevel, mpRegenPercent: 0, isMeditating: false, i.Realm);
        int gearTick = Tick(i, 0);
        int worst = Tick(i, rollMin);
        int best = Tick(i, rollMax);

        var steps = new List<Breakpoint>();
        for (int t = worst + 1; t <= best; t++)
        {
            if (MinRollFor(i, t, rollMin, rollMax) is not { } v) continue;
            double frac = rollMax > rollMin ? (double)(v - rollMin) / (rollMax - rollMin) : 0;
            steps.Add(new Breakpoint(t, i.GearRegenPercent + v, v, frac));
        }

        // Recommend the highest step reachable within the cutoff; if every step
        // needs a roll past the cutoff, still suggest the cheapest (first) one so a
        // planner has a target — the returned fraction shows how dear it is.
        int? recommended = null;
        for (int s = steps.Count - 1; s >= 0; s--)
            if (steps[s].RollFractionOfRange <= recommendCutoff) { recommended = steps[s].RollValueNeeded; break; }
        if (recommended is null && steps.Count > 0) recommended = steps[0].RollValueNeeded;

        return new Result(baseTick, gearTick, worst, best, rollMin, rollMax, steps, recommended);
    }

    // Smallest roll value in [lo,hi] whose tick reaches at least targetTick, or
    // null if even the best roll falls short. Tick is monotonic non-decreasing in
    // the rolled percent, so a binary search converges.
    private static int? MinRollFor(in Inputs i, int targetTick, int lo, int hi)
    {
        if (Tick(i, hi) < targetTick) return null;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (Tick(i, mid) >= targetTick) hi = mid;
            else lo = mid + 1;
        }
        return lo;
    }
}

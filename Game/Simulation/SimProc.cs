using MudPlay.Game.Spells;

namespace MudPlay.Game.Simulation;

// A monster attack's hit spell (AttHitSpell): it fires whenever that physical
// attack lands (GAME_MECHANICS "Monster on-hit procs (`AttHitSpell-N`) are physical
// attacks, not casts"). One without a duration hits once for a cast of its damage;
// one with a duration and damage burns instead — no damage on the hit, then its
// rolled value on every effect-slot tick until it wears off (envelops' "You are on
// fire!", GAME_MECHANICS "Poison and damage over time — ticks, stacking and
// cures"). The stat changes (knockdown's AC / Dodge / Accuracy loss) sit on the
// player for the duration, and Holds pins them in place (can't walk off) until it
// ends (GAME_MECHANICS "Knockdown — a movement-preventing hold").
public sealed record SimProc(
    int DamageMin, int DamageMax, double DurationSeconds,
    int AcDelta, int DodgeDelta, int AccuracyDelta, bool Holds)
{
    public bool DamageOverTime => DurationSeconds > 0 && DamageMax > 0;

    private const int AcCode = 2, AccuracyCode = 22, DodgeCode = 34, HoldPersonCode = 74;

    // A proc carries no cast level of its own (GAME_MECHANICS "Monster on-hit procs
    // (`AttHitSpell-N`) are physical attacks, not casts"), so a level-scaled spell is
    // read at its own ReqLevel — the bottom of its range; which level to use is an
    // open question there. Signed ability values are kept as signed deltas (a
    // debuff lowers).
    public static SimProc? From(in SpellFormulaInput f)
    {
        int level = f.ReqLevel;
        long a = SpellCalculator.SingleCastMinDamage(f, level), b = SpellCalculator.SingleCastMaxDamage(f, level);
        int lo = (int)Math.Max(0, Math.Min(a, b)), hi = (int)Math.Max(0, Math.Max(a, b));
        int ac = 0, dodge = 0, accuracy = 0;
        bool holds = false;
        (long _, long affMax) = SpellCalculator.AffectMagnitude(f, level);
        foreach (SpellAbility ab in f.Abilities)
        {
            int value = ab.Value != 0 ? ab.Value : (int)affMax;
            switch (ab.Code)
            {
                case AcCode: ac += value; break;
                case DodgeCode: dodge += value; break;
                case AccuracyCode: accuracy += value; break;
                case HoldPersonCode: holds |= value > 0; break;
            }
        }
        if (hi <= 0 && ac == 0 && dodge == 0 && accuracy == 0 && !holds) return null;
        double duration = SpellCalculator.Duration(f, level) * SpellCalculator.SpellRoundSecondsWallClock;
        return new SimProc(lo, hi, duration, ac, dodge, accuracy, holds);
    }
}

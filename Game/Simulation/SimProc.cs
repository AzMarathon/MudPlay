using MudPlay.Game.Spells;

namespace MudPlay.Game.Simulation;

// A monster attack's hit spell (AttHitSpell): it fires whenever that physical
// attack lands (GAME_MECHANICS "Monster on-hit procs (`AttHitSpell-N`) are physical
// attacks, not casts"). One that doesn't burn hits once for a cast of its damage; one
// that burns deals nothing on the hit, then its rolled value on every effect-slot
// tick until it wears off (GAME_MECHANICS "Poison and damage over time — ticks,
// stacking and cures"). The stat changes (knockdown's AC / Dodge / Accuracy loss) sit
// on the player for the duration, and Holds pins them in place (can't walk off) until
// it ends (GAME_MECHANICS "Knockdown — a movement-preventing hold").
public sealed record SimProc(
    int DamageMin, int DamageMax, double DurationSeconds,
    int AcDelta, int DodgeDelta, int AccuracyDelta, bool Holds, bool Burns = true)
{
    public bool DamageOverTime => Burns && DurationSeconds > 0 && DamageMax > 0;

    private const int HoldPersonCode = 74;

    // The engine casts a proc with no level at all, so every per-level term drops out:
    // base damage, base duration, base effect values. Which procs burn follows the
    // realm: on Stock only a Damage (ability 1) spell with a duration takes a ticking
    // slot, and Damage-MR (17) always hits on landing; on Paradigm a damage spell with
    // a duration burns (envelops, which is Damage-MR there too). GAME_MECHANICS
    // "Monster on-hit procs (`AttHitSpell-N`) are physical attacks, not casts".
    // Signed ability values are kept as signed deltas (a debuff lowers).
    public static SimProc? From(in SpellFormulaInput spell, RealmType realm)
    {
        SpellFormulaInput f = spell with { MinInc = 0, MaxInc = 0, DurInc = 0 };
        int level = f.ReqLevel;
        long a = SpellCalculator.SingleCastMinDamage(f, level), b = SpellCalculator.SingleCastMaxDamage(f, level);
        int lo = (int)Math.Max(0, Math.Min(a, b)), hi = (int)Math.Max(0, Math.Max(a, b));
        int ac = 0, dodge = 0, accuracy = 0;
        bool holds = false;
        (long _, long affMax) = SpellCalculator.AffectMagnitude(f, level);
        foreach (SpellAbility ab in f.Abilities)
        {
            int value = ab.Value != 0 ? ab.Value : (int)affMax;
            if (MonsterDebuffCalculator.IsAccuracyCode(ab.Code)) { accuracy += value; continue; }
            switch (ab.Code)
            {
                case MonsterDebuffCalculator.AcCode or MonsterDebuffCalculator.AcBlurCode: ac += value; break;
                case MonsterDebuffCalculator.DodgeCode: dodge += value; break;
                case HoldPersonCode: holds |= value > 0; break;
            }
        }
        if (hi <= 0 && ac == 0 && dodge == 0 && accuracy == 0 && !holds) return null;
        double duration = SpellCalculator.Duration(f, level) * SpellCalculator.SpellRoundSecondsWallClock;
        bool burns = realm == RealmType.ParaMud || f.Abilities.Any(a => a.Code == SpellCalculator.AbilDamage);
        return new SimProc(lo, hi, duration, ac, dodge, accuracy, holds, burns);
    }
}

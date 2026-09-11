namespace MudPlay.Game.Calculators;

// Assembles the per-level rows the Workshop Level Projection grid shows,
// composing ExperienceTableCalculator and CharacterCalculator for a given race /
// class / realm. Pure — no UI and no game-data reads: the caller resolves the
// class/race fields (ExpTable, MinHits/MaxHits, HPPerLVL, MageryType/MageryLVL)
// and passes them in.
// The projection is gear-independent (base race/class progression), so the
// equipment +MaxHP / regen-% inputs are passed as 0 and the HP/MP regens are the
// non-resting, non-meditating per-tick base. Each call projects a single level
// from the stats supplied for it, so the caller varies STR/INT/WIL/AGI/HEA/CHM
// per level to layer in the CP Allocation plan. The derived combat/utility stats
// (Accuracy … MagicRes) are likewise the gear-free stat-and-level portion, from
// StatEffects, so they track the plan the same way HP/Mana do.
public static class LevelProjectionCalculator
{
    // Project a single level's numbers.
    public static LevelProjection ProjectLevel(
        int level, int chart,
        int strength, int intellect, int willpower, int agility, int health, int charm,
        int minHitsPerLevel, int maxHitsPerLevel, int raceHpPerLevel,
        int mageryType, int mageryLevel,
        RealmType realm)
    {
        // Cumulative exp threshold to reach this level. The grid derives the
        // "exp remaining" from this minus the character's current exp.
        long total = ExperienceTableCalculator.CalcExpNeeded(level, chart, realm);

        int hpMin = CharacterCalculator.CalcMaxHp(health, level, minHitsPerLevel, maxHitsPerLevel,
            raceHpPerLevel, plusMaxHp: 0, HpRollMode.Min);
        int hpMax = CharacterCalculator.CalcMaxHp(health, level, minHitsPerLevel, maxHitsPerLevel,
            raceHpPerLevel, plusMaxHp: 0, HpRollMode.Max);
        int hpRegen = CharacterCalculator.CalcHpRegen(level, health, hpRegenPercent: 0, isResting: false, realm);

        // Magery type 5 (Mystic) carries Kai, not Mana — the mana formula gives
        // wildly wrong values for it.
        int mana = mageryType == 5
            ? CharacterCalculator.CalcMaxKai(level)
            : CharacterCalculator.CalcMaxMana(mageryLevel, level, plusMaxMana: 0);
        int mpRegen = CharacterCalculator.CalcManaRegen(level, intellect, willpower, charm,
            mageryType, mageryLevel, mpRegenPercent: 0, isMeditating: false, realm);

        var stats = new StatBlock(level, strength, intellect, willpower, agility, health, charm);
        int accuracy = StatEffects.AccuracyFromStats(stats, realm);
        int crit = StatEffects.CritRating(stats);
        int dodge = StatEffects.DodgeValue(stats);
        int stealth = StatEffects.Stealth(stats);
        int minDmg = StatEffects.MinDamageBonus(stats);
        int maxDmg = StatEffects.MaxDamageBonus(stats);
        int maxEnc = StatEffects.MaxEncumbrance(stats);
        int magicRes = StatEffects.MagicResistance(stats);

        return new LevelProjection(level, total, hpMin, hpMax, hpRegen, mana, mpRegen,
            accuracy, crit, dodge, stealth, minDmg, maxDmg, maxEnc, magicRes);
    }
}

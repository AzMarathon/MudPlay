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
    // Project a single level's numbers. When `gear` is supplied it folds the
    // character's DIRECT equipment/quest bonuses (max-HP, regen-%, max-mana, and the
    // flat +dodge/+crit/+stealth/+magic-res/+damage/+enc abilities) on top of the
    // stat-and-level base, so the row reflects the real character. The base
    // attributes passed in are already gear/quest-inclusive (the `stat` screen is
    // effective), so gear's ATTRIBUTE bonuses aren't re-added here — only the direct
    // derived/HP/mana abilities the aggregate carries. `gear` null = gear-free base.
    public static LevelProjection ProjectLevel(
        int level, int chart,
        int strength, int intellect, int willpower, int agility, int health, int charm,
        int minHitsPerLevel, int maxHitsPerLevel, int raceHpPerLevel,
        int mageryType, int mageryLevel,
        RealmType realm, EquipmentStatSummary? gear = null)
    {
        // Cumulative exp threshold to reach this level. The grid derives the
        // "exp remaining" from this minus the character's current exp.
        long total = ExperienceTableCalculator.CalcExpNeeded(level, chart, realm);

        // Direct (non-attribute) equipment/quest bonuses; 0 when gear-free.
        int plusMaxHp = gear?.PlusMaxHp ?? 0;
        int hpRegenPct = gear?.HpRegenPercent ?? 0;
        int plusMaxMana = gear?.PlusMaxMana ?? 0;
        int mpRegenPct = gear?.MpRegenPercent ?? 0;

        int hpMin = CharacterCalculator.CalcMaxHp(health, level, minHitsPerLevel, maxHitsPerLevel,
            raceHpPerLevel, plusMaxHp, HpRollMode.Min);
        int hpMax = CharacterCalculator.CalcMaxHp(health, level, minHitsPerLevel, maxHitsPerLevel,
            raceHpPerLevel, plusMaxHp, HpRollMode.Max);
        int hpRegen = CharacterCalculator.CalcHpRegen(level, health, hpRegenPct, isResting: false, realm);
        int hpRegenRest = CharacterCalculator.CalcHpRegen(level, health, hpRegenPct, isResting: true, realm);

        // Magery type 5 (Mystic) carries Kai, not Mana — the mana formula gives
        // wildly wrong values for it.
        int mana = mageryType == 5
            ? CharacterCalculator.CalcMaxKai(level)
            : CharacterCalculator.CalcMaxMana(mageryLevel, level, plusMaxMana);
        int mpRegen = CharacterCalculator.CalcManaRegen(level, intellect, willpower, charm,
            mageryType, mageryLevel, mpRegenPct, isMeditating: false, realm);

        var stats = new StatBlock(level, strength, intellect, willpower, agility, health, charm);
        // Accuracy stays the stat-and-level contribution: the gear portion (worn Accy
        // + the realm-specific abil-22 rule) is weapon-dependent and can't be
        // projected to future levels, so it's not folded here. The other derived
        // columns take the aggregate's flat direct bonuses on top of the stat base.
        int accuracy = StatEffects.AccuracyFromStats(stats, realm);
        int crit = StatEffects.CritRating(stats) + (gear?.PlusCrits ?? 0);
        int dodge = StatEffects.DodgeValue(stats) + (gear?.PlusDodge ?? 0);
        int stealth = StatEffects.Stealth(stats) + (gear?.PlusStealth ?? 0);
        int minDmg = StatEffects.MinDamageBonus(stats) + (gear?.PlusMinDamage ?? 0);
        int maxDmg = StatEffects.MaxDamageBonus(stats) + (gear?.PlusMaxDamage ?? 0);
        int maxEnc = StatEffects.MaxEncumbrance(stats) + (gear?.PlusEncumbrance ?? 0);
        int magicRes = StatEffects.MagicResistance(stats) + (gear?.PlusMagicResist ?? 0);

        return new LevelProjection(level, total, hpMin, hpMax, hpRegen, mana, mpRegen,
            accuracy, crit, dodge, stealth, minDmg, maxDmg, maxEnc, magicRes, hpRegenRest);
    }
}

using MudPlay.Game.Calculators;
using MudPlay.Game.Spells;
using MudPlay.Services;

namespace MudPlay.Game.Simulation;

// Moves the character to another level for a simulation — to check a loop you
// played at L45 against what the simulator predicts at L45, or to ask what a loop
// is worth ten levels on. Max HP, max mana (not a Mystic's kai) and Spellcasting shift by the class /
// race formulas' difference between the two levels (CharacterCalculator's average
// HP roll, mana and Spellcasting), so the gear, quest and real-roll share of
// today's numbers carries over unchanged; everything else
// the builder derives from the level (accuracy, swings, regen, spell scaling) moves
// with it. Stats and gear stay as they are now. A spell that needs a higher level
// than the target is dropped.
public static class SimLevelProjection
{
    // A detached copy of the stat screen at targetLevel — never the live
    // PlayerStats, which StatParser alone writes.
    public static PlayerStats StatsAt(PlayerStats now, int targetLevel, GameDataCache gameData)
    {
        ArgumentNullException.ThrowIfNull(now);
        ArgumentNullException.ThrowIfNull(gameData);
        int maxHits = now.MaxHits, maxMana = now.MaxMana, spellcasting = now.Spellcasting;
        StatContext ctx = StatContext.Resolve(gameData, now.Class, now.Race, gameData.ActiveRealm);
        // No class row (hit dice 0) leaves nothing to shift by, so today's pools stand.
        if (targetLevel != now.Level && ctx.MinHits + ctx.MaxHits > 0)
        {
            int Hp(int level) => CharacterCalculator.CalcMaxHp(now.Health, level, ctx.MinHits, ctx.MaxHits,
                ctx.RaceHpPerLevel, plusMaxHp: 0, HpRollMode.Average);
            maxHits = Math.Max(1, maxHits + Hp(targetLevel) - Hp(now.Level));
            // A Mystic's pool is kai, which the mana formula doesn't describe.
            if (ctx.MageryType != 5)
                maxMana = Math.Max(0, maxMana + CharacterCalculator.CalcMaxMana(ctx.MageryLevel, targetLevel, 0)
                                              - CharacterCalculator.CalcMaxMana(ctx.MageryLevel, now.Level, 0));
            spellcasting += CharacterCalculator.CalcSpellcasting(targetLevel, now.Intellect, now.Willpower, now.Charm, ctx.MageryType, ctx.MageryLevel, 0)
                          - CharacterCalculator.CalcSpellcasting(now.Level, now.Intellect, now.Willpower, now.Charm, ctx.MageryType, ctx.MageryLevel, 0);
        }
        return new PlayerStats
        {
            Name = now.Name, Race = now.Race, Class = now.Class, Level = targetLevel,
            Hits = maxHits, MaxHits = maxHits, Mana = maxMana, MaxMana = maxMana,
            ArmourClass = now.ArmourClass, MaxArmourClass = now.MaxArmourClass,
            Strength = now.Strength, Intellect = now.Intellect, Willpower = now.Willpower,
            Agility = now.Agility, Health = now.Health, Charm = now.Charm,
            Perception = now.Perception, Stealth = now.Stealth, MartialArts = now.MartialArts,
            MagicRes = now.MagicRes, Spellcasting = spellcasting,
        };
    }

    public static IReadOnlyList<KnownSpell> SpellsAt(IReadOnlyList<KnownSpell> obtained, int level) =>
        obtained.Where(k => k.ReqLevel <= level).ToList();
}

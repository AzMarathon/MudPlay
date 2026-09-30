using System.Text.Json;
using MudPlay.Game.Calculators;
using MudPlay.Game.Spells;
using MudPlay.Services;

namespace MudPlay.Game.Simulation;

// Moves the character to another level for a simulation — to check a loop you
// played at L45 against what the simulator predicts at L45, or to ask what a loop
// is worth ten levels on. Max HP, max mana and Spellcasting shift by the class /
// race formulas' difference between the two levels (LevelProjectionCalculator's
// average HP roll, CharacterCalculator's mana and Spellcasting), so the gear, quest
// and real-roll share of today's numbers carries over unchanged; everything else
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
        if (targetLevel != now.Level
            && gameData.FindRowByName("Classes", now.Class) is JsonElement cls
            && gameData.FindRowByName("Races", now.Race) is JsonElement race)
        {
            int mageryType = SimCharacterBuilder.ReadInt(cls, "MageryType"), mageryLevel = SimCharacterBuilder.ReadInt(cls, "MageryLVL");
            LevelProjection Project(int level) => LevelProjectionCalculator.ProjectLevel(
                level, chart: 0, now.Strength, now.Intellect, now.Willpower, now.Agility, now.Health, now.Charm,
                SimCharacterBuilder.ReadInt(cls, "MinHits"), SimCharacterBuilder.ReadInt(cls, "MaxHits"), SimCharacterBuilder.ReadInt(race, "HPPerLVL"),
                mageryType, mageryLevel, gameData.ActiveRealm);
            LevelProjection from = Project(now.Level), to = Project(targetLevel);
            maxHits = Math.Max(1, maxHits + (to.HpMin + to.HpMax) / 2 - (from.HpMin + from.HpMax) / 2);
            maxMana = Math.Max(0, maxMana + to.Mana - from.Mana);
            spellcasting += CharacterCalculator.CalcSpellcasting(targetLevel, now.Intellect, now.Willpower, now.Charm, mageryType, mageryLevel, 0)
                          - CharacterCalculator.CalcSpellcasting(now.Level, now.Intellect, now.Willpower, now.Charm, mageryType, mageryLevel, 0);
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

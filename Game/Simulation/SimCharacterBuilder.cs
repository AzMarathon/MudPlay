using System.Text.Json;
using MudPlay.Game.Calculators;
using MudPlay.Game.Inventory;
using MudPlay.Game.Quests;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Simulation;

// Assembles the SimCharacter a loop simulation plays from the character as the
// client knows them right now: the stat screen, worn gear, obtained spells, and
// the live Combat / Health / Spells settings. Offense and defense come from the
// same recipes Monster Intel uses (CharacterCalculator.BuildNormalAttackProfile,
// IncomingHitEstimator.BuildLiveDefense), so the simulation and the Intel window
// never disagree about a matchup.
public static class SimCharacterBuilder
{
    public static SimCharacter Build(
        PlayerStats stats,
        IReadOnlyList<EquippedItem> worn,
        EncumbranceReading encumbrance,
        IReadOnlyList<KnownSpell> obtainedSpells,
        GameDataCache gameData,
        int weaponHitMagic,
        CombatSettings combat,
        HealthSettings health,
        SpellsSettings spells,
        BuffSettings? buffs,
        IReadOnlyList<QuestBonus>? questBonuses,
        Func<int, MonsterOverlay> overlay,
        Func<int, string?> spellShortByNumber,
        int evilPoints)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(worn);
        ArgumentNullException.ThrowIfNull(obtainedSpells);
        ArgumentNullException.ThrowIfNull(gameData);

        RealmType realm = gameData.ActiveRealm;
        PlayerMatchupProfile melee = CharacterCalculator.BuildNormalAttackProfile(stats, worn, encumbrance, gameData);
        PlayerDefenseProfile defense = IncomingHitEstimator.BuildLiveDefense(
            stats, worn, encumbrance, gameData, buffs, obtainedSpells, questBonuses);

        return new SimCharacter(
            realm, stats.Level, stats.MaxHits, stats.MaxMana,
            melee, weaponHitMagic, defense,
            BuildRegen(stats, worn, gameData, questBonuses, realm),
            BuildSpells(stats, worn, obtainedSpells, gameData, realm),
            combat, health, spells, overlay,
            number => number > 0 ? spellShortByNumber(number) : null,
            evilPoints);
    }

    // Per-tick regen from the stat formulas (CharacterCalculator.CalcHpRegen /
    // CalcManaRegen) with gear, race, class and quest regen percents folded in.
    // Paradigm splits each natural cycle into thirds on a 10 s grid and rests on the
    // same grid at 3x, in place of the standing tick; Stock adds a separate rest tick
    // (GAME_MECHANICS "Rest and meditate tick timing").
    private static SimRegen BuildRegen(
        PlayerStats stats, IReadOnlyList<EquippedItem> worn, GameDataCache gameData,
        IReadOnlyList<QuestBonus>? questBonuses, RealmType realm)
    {
        EquipmentStatBreakdown gear = CharacterCalculator.AggregateEquipmentStats(worn, gameData);
        int mageryType = 0, mageryLevel = 0;
        if (gameData.FindRowByName("Races", stats.Race) is JsonElement race)
            CharacterCalculator.ApplyAbilityBonuses(gear, race, stats.Race);
        if (gameData.FindRowByName("Classes", stats.Class) is JsonElement cls)
        {
            CharacterCalculator.ApplyAbilityBonuses(gear, cls, stats.Class);
            mageryType = ReadInt(cls, "MageryType");
            mageryLevel = ReadInt(cls, "MageryLVL");
        }
        if (questBonuses is not null) CharacterCalculator.ApplyQuestBonuses(gear, questBonuses, "Quests");
        EquipmentStatSummary t = gear.Totals;

        double hpStanding = CharacterCalculator.CalcHpRegen(stats.Level, stats.Health, t.HpRegenPercent, false, realm);
        double hpResting = CharacterCalculator.CalcHpRegen(stats.Level, stats.Health, t.HpRegenPercent, true, realm);
        double maStanding = CharacterCalculator.CalcManaRegen(stats.Level, stats.Intellect, stats.Willpower, stats.Charm,
            mageryType, mageryLevel, t.MpRegenPercent, false, realm);
        double maMeditating = CharacterCalculator.CalcManaRegen(stats.Level, stats.Intellect, stats.Willpower, stats.Charm,
            mageryType, mageryLevel, t.MpRegenPercent, true, realm);

        bool paradigm = realm == RealmType.ParaMud;
        return paradigm
            ? new SimRegen(hpStanding / 3, hpResting / 3, maStanding / 3, maMeditating,
                RealmRegenProfile.For(realm), RestReplacesStanding: true)
            : new SimRegen(hpStanding, hpResting, maStanding, maMeditating,
                RealmRegenProfile.For(realm), RestReplacesStanding: false);
    }

    private static IReadOnlyDictionary<string, SimSpell> BuildSpells(
        PlayerStats stats, IReadOnlyList<EquippedItem> worn, IReadOnlyList<KnownSpell> obtained,
        GameDataCache gameData, RealmType realm)
    {
        int bonus = CharacterCalculator.SpellDamageBonus(stats, worn, gameData);
        var map = new Dictionary<string, SimSpell>(StringComparer.OrdinalIgnoreCase);
        foreach (KnownSpell known in obtained)
        {
            if (string.IsNullOrWhiteSpace(known.Short)) continue;
            SpellFormulaInput f = known.Formula;
            // Fires per round = floor(1000 / EnergyCost) for an in-round spell
            // (GAME_MECHANICS "Combat spells: engaged once, auto-repeat per round").
            int fires = f.EnergyCost is > 0 and <= 1000 ? Math.Max(1, 1000 / f.EnergyCost) : 1;
            map.TryAdd(known.Short, new SimSpell(
                known.Short, known.Number, f.ManaCost, fires,
                SpellCastChance.Compute(stats.Spellcasting, f.Diff, known.Magery == SpellCastChance.KaiMagery,
                    realm == RealmType.ParaMud),
                SpellCalculator.MinDamage(f, stats.Level, spellDamageBonus: bonus, realm: realm),
                SpellCalculator.MaxDamage(f, stats.Level, spellDamageBonus: bonus, realm: realm),
                SpellDamageCalculator.UsesMagicResist(f), f.TypeOfResists, f.AttType,
                SpellCalculator.SingleCastMinHeal(f, stats.Level),
                SpellCalculator.SingleCastMaxHeal(f, stats.Level)));
        }
        return map;
    }

    private static int ReadInt(JsonElement row, string name) =>
        row.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;
}

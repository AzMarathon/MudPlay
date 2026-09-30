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

        IReadOnlyDictionary<string, SimSpell> spellMap = BuildSpells(stats, worn, obtainedSpells, gameData, realm);
        return new SimCharacter(
            realm, stats.Level, stats.MaxHits, stats.MaxMana,
            melee, weaponHitMagic, defense,
            BuildRegen(stats, worn, gameData, questBonuses, realm),
            spellMap,
            combat, health, spells, overlay,
            number => number > 0 ? spellShortByNumber(number) : null,
            evilPoints,
            BuildBuffs(buffs, obtainedSpells, spellMap, realm));
    }

    // The solo self-buffs of the Buffs list, in the order the casting engine walks
    // them (BuffPriorityOrder): a self-cast slot (CastOnSelf), or a whole-party spell
    // that's on and allowed solo (WholePartyOn + CastSolo) — a lone character is a
    // party of one. Item-cast tokens and dark-only light spells are left out. A roll
    // threshold counts only once it's in rolled-percent units (always on Paradigm,
    // which reads the roll off `abil 145`).
    private static IReadOnlyList<SimBuff> BuildBuffs(
        BuffSettings? buffs, IReadOnlyList<KnownSpell> obtained,
        IReadOnlyDictionary<string, SimSpell> spellMap, RealmType realm)
    {
        if (buffs is null) return Array.Empty<SimBuff>();
        var targets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (KnownSpell k in obtained) targets.TryAdd(k.Short, k.Targets);
        bool WholeParty(string? code) => code is not null && targets.TryGetValue(code, out int t) && BuffClassifier.IsWholeParty(t);

        var list = new List<SimBuff>();
        foreach (BuffSlot slot in BuffPriorityOrder.InPriorityOrder(buffs.Slots, buffs.PriorityTopDown, buffs.ManualOrder,
                     s => BuffPriorityOrder.Category(ItemCastToken.IsToken(s.Spell), WholeParty(s.Spell))))
        {
            if (string.IsNullOrWhiteSpace(slot.Spell) || slot.OnlyWhenDark || ItemCastToken.IsToken(slot.Spell)) continue;
            if (!spellMap.ContainsKey(slot.Spell)) continue;
            bool selfCast = WholeParty(slot.Spell) ? slot.WholePartyOn && slot.CastSolo : slot.CastOnSelf;
            if (!selfCast) continue;
            int? rerollBelow = slot.RerollThresholdIsRoll || realm == RealmType.ParaMud ? slot.RerollThreshold : null;
            list.Add(new SimBuff(slot.Spell, slot.RecastMarginSec, slot.OnlyWhenHpFull, slot.OnlyWhenMaFull,
                slot.CastBeforeRestingForMana, rerollBelow, slot.RerollCount, slot.RerollInfinite));
        }
        return list;
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

        int hpPct = t.HpRegenPercent, mpPct = t.MpRegenPercent;
        int level = stats.Level, health = stats.Health, intel = stats.Intellect, wil = stats.Willpower, cha = stats.Charm;
        // Paradigm pays each natural cycle in thirds on its 10 s grid.
        double share = realm == RealmType.ParaMud ? 1.0 / 3 : 1.0;
        return new SimRegen(
            HpStanding: extra => share * CharacterCalculator.CalcHpRegen(level, health, hpPct + extra, false, realm),
            HpResting: extra => share * CharacterCalculator.CalcHpRegen(level, health, hpPct + extra, true, realm),
            MaStanding: extra => share * CharacterCalculator.CalcManaRegen(level, intel, wil, cha,
                mageryType, mageryLevel, mpPct + extra, false, realm),
            MaMeditating: CharacterCalculator.CalcManaRegen(level, intel, wil, cha,
                mageryType, mageryLevel, mpPct, true, realm),
            RealmRegenProfile.For(realm), RestReplacesStanding: realm == RealmType.ParaMud);
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
                SpellCalculator.SingleCastMaxHeal(f, stats.Level),
                DurationSeconds: SpellCalculator.Duration(f, stats.Level) * SpellCalculator.SpellRoundSecondsWallClock,
                ManaRegenMin: RegenRange(f, stats.Level, ManaRegenCode).Min,
                ManaRegenMax: RegenRange(f, stats.Level, ManaRegenCode).Max,
                HpRegenMin: RegenRange(f, stats.Level, HpRegenCode).Min,
                HpRegenMax: RegenRange(f, stats.Level, HpRegenCode).Max,
                Debuff: MonsterDebuffCalculator.AffectsMonsterStats(known)
                    ? MonsterDebuffCalculator.Fold(new[] { known }, stats.Level) : default));
        }
        return map;
    }

    private const int HpRegenCode = 123, ManaRegenCode = 145;

    // The regen percent a spell adds while it's up: a stored value is fixed, a 0 is
    // rolled from the spell's level-scaled range each cast (GAME_MECHANICS "Mana
    // regeneration & the ManaRgn breakpoints"). A negative stored HP-regen value is
    // left out rather than guessed at, so the HP side only ever adds regen.
    private static (int Min, int Max) RegenRange(in SpellFormulaInput f, int level, int code)
    {
        foreach (SpellAbility a in f.Abilities)
        {
            if (a.Code != code) continue;
            if (a.Value != 0) return code == HpRegenCode && a.Value < 0 ? (0, 0) : (a.Value, a.Value);
            (long lo, long hi) = SpellCalculator.AffectMagnitude(f, level);
            return ((int)lo, (int)hi);
        }
        return (0, 0);
    }

    private static int ReadInt(JsonElement row, string name) =>
        row.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;
}

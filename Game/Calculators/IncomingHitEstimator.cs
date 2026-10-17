using System;
using System.Collections.Generic;
using System.Text.Json;
using MudPlay.Game.Combat;
using MudPlay.Game.Inventory;
using MudPlay.Game.Quests;
using MudPlay.Game.Spells;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Calculators;

// The player's live defense against a monster's swing — the AC / Dodge / wards a
// monster's attack lands against. Assembled the one way Monster Intel's "Hits You
// %" column does it: worn gear + the permanent race/class innate + completed-quest
// bonuses + the character's configured (assumed-up) AC buffs. Evil is the player's
// own alignment tier (gear-independent); it only scales an evil-only Vile Ward, so
// it carries the same Fiend default Monster Intel seeds (a worn Vile Ward implies an
// evil character).
public readonly record struct PlayerDefenseProfile(
    int Ac, int Dodge, int ProtEvil, int ProtGood, bool Shadow, int VileWard, EvilLevel Evil,
    // The class's ArmourType (Classes table). It only affects the ParaMUD hit-chance
    // FLOOR: light-armour classes (type 1..6 — Silk/Ninja/Leather) can be driven to a
    // 1% minimum, everything else (and all of Stock) floors higher — see
    // CombatCalculator.GetHitMin. 0 = unknown → the realm default floor.
    int ArmourType = 0,
    // The un-floored AC to the tenth (worn gear + buffs), for DISPLAY only — the game
    // shows AC fractional (item AC is stored 10×, char AC rounded to 1 decimal). Ac
    // above is this floored, the whole number the to-hit formula consumes. See the
    // "Armour Class" note in GAME_MECHANICS.md.
    double AcExact = 0,
    // Resist percentages against a monster's spell damage, from the same sources.
    ElementalResists Resists = default);

// Shared source for "how likely is this monster to hit me right now" — the single
// weighted incoming-hit figure Monster Intel's master list surfaces, extracted here
// so the route Details window colours monster names from the same calculation Monster
// Intel uses. Pure domain: takes explicit character inputs, no service holder.
public static class IncomingHitEstimator
{
    // Assemble the live defense profile from explicit character inputs — the exact
    // recipe MonsterIntelViewModel uses to seed its Hits-You-% sim: worn-gear
    // aggregate + race/class innate + completed-quest folds + configured AC buffs.
    // AC rides the WORN gear + permanent base + configured buffs, not the live `stat`
    // ArmourClass (which already folds any buffs active at capture — adding the
    // configured-buff AC on top would double-count them).
    public static PlayerDefenseProfile BuildLiveDefense(
        PlayerStats stats, IReadOnlyList<EquippedItem> worn, EncumbranceReading encum,
        GameDataCache gameData, BuffSettings? buffs, IReadOnlyList<KnownSpell>? spells,
        IReadOnlyList<QuestBonus>? questBonuses, EvilLevel evil = EvilLevel.Fiend)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(worn);
        ArgumentNullException.ThrowIfNull(gameData);

        EquipmentStatBreakdown gear = CharacterCalculator.AggregateEquipmentStats(worn, gameData);
        if (gameData.FindRowByName("Races", stats.Race) is GameRow raceRow)
            CharacterCalculator.ApplyAbilityBonuses(gear, raceRow, stats.Race);
        int armourType = 0;
        if (gameData.FindRowByName("Classes", stats.Class) is GameRow classRow)
        {
            CharacterCalculator.ApplyAbilityBonuses(gear, classRow, stats.Class);
            if (classRow.TryGetProperty("ArmourType", out GameValue atEl)
                && atEl.ValueKind == JsonValueKind.Number && atEl.TryGetInt32(out int at))
                armourType = at;
        }
        if (questBonuses is not null)
            CharacterCalculator.ApplyQuestBonuses(gear, questBonuses, "Quests");
        EquipmentStatSummary totals = gear.Totals;

        BuffDefense buff = BuffDefenseCalculator.Compute(buffs, stats.Level, spells);
        // Truncate the fractional AC, don't round: item AC is stored ×10 so the
        // summed PlusAC carries tenths (e.g. gear +36.5), and the game's integer
        // combat AC is the FLOOR of that total — the same value Character Info's
        // "Projected AC" reflects. Math.Round pushed a trailing .5 up to the next
        // integer, over-stating AC by 1 (user-confirmed: projected 61.5 = actual,
        // but the sim was seeding 62). buff.Ac is already an integer, so truncating
        // PlusAC alone floors the whole.
        double acExact = totals.PlusAC + buff.Ac;   // un-floored, for the fractional display
        int ac = (int)acExact;
        int dodge = CombatCalculator.CalcDodge(
            stats.Level, stats.Agility, stats.Charm, totals.PlusDodge,
            encum.CurrentWeight, encum.MaxWeight, gameData.ActiveRealm);
        int protEvil = totals.PlusProtEvil + buff.ProtEvil;
        bool shadow = totals.PlusShadowResist > 0 || buff.HasShadow;
        return new PlayerDefenseProfile(
            ac, dodge, protEvil, totals.PlusProtGood, shadow, totals.PlusVileWard, evil, armourType,
            AcExact: acExact,
            Resists: buff.Resists + new ElementalResists(
                totals.PlusColdResist, totals.PlusFireResist, totals.PlusStoneResist,
                totals.PlusLightningResist, totals.PlusWaterResist, totals.PlusPoisonResist));
    }

    // A monster's blended chance to land a hit on the player across ALL its physical
    // attacks, rounded to the whole percent the route Details bands compare — Monster
    // Intel's Hits-You-% column with no debuffs applied and its what-if fields at their
    // live values. Null when the monster has
    // no catalogued physical attack (an NPC/caster record).
    public static int? WeightedHitPercent(
        MonsterCatalogEntry monster, in PlayerDefenseProfile def, RealmType realm)
        => WeightedHitChance(monster, def, realm) is { } chance ? RoundPercent(chance) : null;

    // The blend unrounded. accuracyDelta is a folded accuracy debuff (0 for none).
    // Every Hits-You-% figure Monster Intel shows goes through here or AttackHitChance,
    // so the list, the Incoming threat lines and the Attacks rows can't disagree.
    public static double? WeightedHitChance(
        MonsterCatalogEntry monster, in PlayerDefenseProfile def, RealmType realm, int accuracyDelta = 0)
    {
        ArgumentNullException.ThrowIfNull(monster);
        return MonsterMatchupCalculatorSpells.WeightedIncomingHitChance(
            monster.PhysicalAttacks, accuracyDelta, monster.Align,
            def.Ac, def.Dodge, def.ProtEvil, def.ProtGood,
            realm, def.Shadow, def.VileWard, def.Evil, def.ArmourType);
    }

    // One of the monster's attacks against the player, unrounded.
    public static double AttackHitChance(
        MonsterAttackSlot attack, int alignment, in PlayerDefenseProfile def, RealmType realm,
        int accuracyDelta = 0)
    {
        ArgumentNullException.ThrowIfNull(attack);
        return MonsterMatchupCalculatorSpells.AttackHitChance(
            attack.Accuracy - accuracyDelta, alignment,
            def.Ac, def.Dodge, def.ProtEvil, def.ProtGood,
            realm, def.Shadow, def.VileWard, def.Evil, def.ArmourType);
    }

    // The blend and each physical attack behind it, most-used first. Null when the
    // monster has no physical attack.
    public static IncomingHitBreakdown? Breakdown(
        MonsterCatalogEntry monster, in PlayerDefenseProfile def, RealmType realm, int accuracyDelta = 0)
    {
        if (WeightedHitChance(monster, def, realm, accuracyDelta) is not { } overall) return null;
        PlayerDefenseProfile d = def;   // a lambda can't capture an `in` parameter
        List<IncomingAttackHit> attacks = monster.PhysicalAttackSlots
            .Select(a => new IncomingAttackHit(a, MonsterCatalogEntry.UseWeight(a),
                AttackHitChance(a, monster.Align, d, realm, accuracyDelta)))
            .OrderByDescending(x => x.Weight)
            .ToList();
        return new IncomingHitBreakdown(overall, attacks);
    }

    public static int RoundPercent(double chance) => (int)Math.Round(chance, MidpointRounding.AwayFromZero);

    // A hit chance for display: to the tenth below 10%, where the floor and dodge
    // live and a whole percent would hide them, whole above. A chance too small for
    // a tenth isn't shown as 0%: it can still land.
    public static string FormatPercent(double chance)
        => chance > 0 && chance < 0.05 ? "<0.1%"
            : chance < 9.95
                ? chance.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%"
                : RoundPercent(chance).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
}

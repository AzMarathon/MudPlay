using System;
using MudPlay.Game.Calculators;

namespace MudPlay.Game.Inventory;

// Estimates the live character's damage with a given weapon or piece of gear, for the
// Item Finder's Est. BS Dmg / Dmg/Rnd columns and its damage-based Find Best criteria.
// Every number comes from CombatCalculator's existing formulas (CalcBSDamage,
// ComputeMeleeOffense, CalcMartialArtsDamage); nothing here models a new mechanic.
//
// Damage per round assumes every swing lands: the finder has no target, so there's no
// AC / dodge to roll against. Crits blend in the way MonsterMatchupCalculator does.
//
// Strength / Agility / Stealth are the live stat-screen values, which already include
// whatever is worn now. A non-weapon item is scored by adding its deltas on top of
// that, so the piece currently in the same slot is counted twice; the error is the
// same for every candidate in that slot, so the ranking Find Best needs still holds.
public sealed record ItemDamageModel(
    RealmType Realm, int Level, int CombatLevel, int Strength, int Agility, int Stealth,
    bool HasClassStealth, int CurrentEncum, int MaxEncum,
    ItemDamageModel.RestBonuses Rest, ItemDamageModel.WeaponInputs? CurrentWeapon)
{
    // A weapon's damage-relevant fields, pulled off an Items row (or the weapon in hand).
    // Strength / Agility / Stealth are the stat bonuses the weapon itself grants.
    public readonly record struct WeaponInputs(
        int Min, int Max, int Speed, int StrReq, int PlusMin, int PlusMax, int Crits, int BsMin, int BsMax,
        bool CanBackstab, int Strength = 0, int Agility = 0, int Stealth = 0);

    // The gear bonuses a damage estimate folds in besides the weapon: everything worn
    // except the weapon hand, plus race / class abilities — the same aggregation Monster
    // Intel's attack profile uses, so the two surfaces agree on a character's damage.
    public readonly record struct RestBonuses(
        int PlusMin, int PlusMax, int Crits, int BsMin, int BsMax,
        int PunchDmg, int KickDmg, int JumpKickDmg);

    // A non-weapon item's contribution to damage: the stat and bonus deltas it would add.
    public readonly record struct GearDelta(
        int Strength, int Agility, int Stealth, int PlusMin, int PlusMax, int Crits, int BsMin, int BsMax,
        int PunchDmg, int KickDmg, int JumpKickDmg);

    // A backstab needs a real level; the swing model also needs a class combat level.
    public bool IsUsable => Level > 0 && CombatLevel > 0;

    // Backstab damage range with this weapon in hand. Null when the weapon can't
    // backstab (no backstab ability) or the model isn't usable.
    public BSDamageResult? Backstab(WeaponInputs weapon) =>
        IsUsable && weapon.CanBackstab ? BackstabWith(weapon, default) : null;

    // Average damage per round with this weapon for a physical attack type. A
    // backstab is one strike, so its value is the backstab average. 0 when the
    // weapon can't make that attack.
    public double DamagePerRound(WeaponInputs weapon, MudAttackType type)
    {
        if (!IsUsable) return 0;
        return type switch
        {
            MudAttackType.Backstab => weapon.CanBackstab ? BackstabWith(weapon, default).AvgDamage : 0,
            MudAttackType.Normal or MudAttackType.Bash or MudAttackType.Smash => MeleeRound(weapon, default, type),
            _ => 0,
        };
    }

    // Average damage per round for a bare-handed martial-arts attack.
    public double MartialArtsPerRound(MudAttackType type) => MartialArtsRound(type, default);

    // How much a non-weapon item raises backstab damage over the current loadout.
    // Scored against the weapon in hand (bare-handed when there's none), so it
    // measures what the item adds to the backstab you'd actually make.
    public (double Min, double Max, double Avg) BackstabGain(GearDelta item)
    {
        if (!IsUsable) return (0, 0, 0);
        WeaponInputs weapon = CurrentWeapon ?? default;
        BSDamageResult baseline = BackstabWith(weapon, default);
        BSDamageResult with = BackstabWith(weapon, item);
        return (with.MinDamage - baseline.MinDamage,
                with.MaxDamage - baseline.MaxDamage,
                with.AvgDamage - baseline.AvgDamage);
    }

    // How much a non-weapon item raises damage per round for this attack type,
    // over the current loadout.
    public double DamagePerRoundGain(GearDelta item, MudAttackType type)
    {
        if (!IsUsable) return 0;
        WeaponInputs weapon = CurrentWeapon ?? default;
        return type switch
        {
            MudAttackType.Backstab => BackstabWith(weapon, item).AvgDamage - BackstabWith(weapon, default).AvgDamage,
            MudAttackType.Normal or MudAttackType.Bash or MudAttackType.Smash =>
                MeleeRound(weapon, item, type) - MeleeRound(weapon, default, type),
            MudAttackType.Punch or MudAttackType.Kick or MudAttackType.Jumpkick =>
                MartialArtsRound(type, item) - MartialArtsRound(type, default),
            _ => 0,
        };
    }

    // The live stats already carry the weapon in hand's own bonuses, so a different
    // weapon swaps them out rather than stacking on top.
    private int StrengthWith(WeaponInputs weapon, GearDelta item) =>
        Strength - (CurrentWeapon?.Strength ?? 0) + weapon.Strength + item.Strength;
    private int AgilityWith(WeaponInputs weapon, GearDelta item) =>
        Agility - (CurrentWeapon?.Agility ?? 0) + weapon.Agility + item.Agility;
    private int StealthWith(WeaponInputs weapon, GearDelta item) =>
        Stealth - (CurrentWeapon?.Stealth ?? 0) + weapon.Stealth + item.Stealth;

    private BSDamageResult BackstabWith(WeaponInputs weapon, GearDelta item) =>
        CombatCalculator.CalcBSDamage(
            Level, StealthWith(weapon, item), StrengthWith(weapon, item), weapon.Min, weapon.Max,
            Rest.BsMin + weapon.BsMin + item.BsMin, Rest.BsMax + weapon.BsMax + item.BsMax,
            Rest.PlusMax + weapon.PlusMax + item.PlusMax, HasClassStealth, Realm);

    private double MeleeRound(WeaponInputs weapon, GearDelta item, MudAttackType type)
    {
        if (weapon.Max <= 0 || weapon.Speed <= 0) return 0;
        MeleeOffense o = CombatCalculator.ComputeMeleeOffense(
            type, Realm, Level, CombatLevel, StrengthWith(weapon, item), AgilityWith(weapon, item),
            weapon.Min, weapon.Max, weapon.Speed, weapon.StrReq,
            Rest.PlusMax + weapon.PlusMax + item.PlusMax, Rest.PlusMin + weapon.PlusMin + item.PlusMin,
            Rest.Crits + weapon.Crits + item.Crits, CurrentEncum, MaxEncum);
        return PerRound(o.AvgDamage, o.SwingsPerRound, o.CritChance, o.AvgCritDamage);
    }

    private double MartialArtsRound(MudAttackType type, GearDelta item)
    {
        if (!IsUsable) return 0;
        int speed = CombatCalculator.MartialArtsSpeed(type, Realm);
        if (speed <= 0) return 0;
        int maDmg = type switch
        {
            MudAttackType.Punch => Rest.PunchDmg + item.PunchDmg,
            MudAttackType.Kick => Rest.KickDmg + item.KickDmg,
            _ => Rest.JumpKickDmg + item.JumpKickDmg,
        };
        // The damage formula takes the item +MA-skill bonus floored to 1, never the
        // Martial Arts stat — the same input Monster Intel's profile passes.
        const int maPlusSkill = 1;
        MeleeDamageResult d = CombatCalculator.CalcMartialArtsDamage(
            type, Realm, Level, maPlusSkill, Strength + item.Strength, Rest.PlusMax + item.PlusMax, maDmg);
        double swings = CombatCalculator.CalcSwings(
            CombatLevel, Level, speed, Agility + item.Agility, Strength + item.Strength, weaponStrReq: 0,
            CurrentEncum, MaxEncum, realmType: Realm).RawSwings;
        return PerRound((d.MinDamage + d.MaxDamage) / 2, swings, 0, 0);
    }

    private static double PerRound(int avgDamage, double swings, int critChance, int avgCritDamage)
    {
        double crit = Math.Clamp(critChance, 0, 100) / 100.0;
        return swings * ((1.0 - crit) * avgDamage + crit * avgCritDamage);
    }
}

namespace MudPlay.Game.Calculators;

// Composes a both-directions combat preview between the player and one monster,
// on top of CombatCalculator.CalculateHitChance:
//   Player → monster — normal-attack hit chance vs the monster's AC and dodge
//     (some monsters carry a Dodge ability; they have no alignment ward),
//     per-hit damage after the monster's damage-resist, then DPS
//     (hit% * dmg/hit * swings) and rounds-to-kill.
//   Monster → player — the monster's primary physical attack's hit chance vs
//     our AC + dodge (+ our prot-evil / prot-good only when the monster is
//     evil / good), and its per-hit damage after our damage-resist. Physical
//     attacks reduce by DR; elemental / magic resist doesn't apply to the
//     physical melee slot.
// Pure math — the caller extracts the monster row and current player profile
// and passes typed values in.
public static class MonsterMatchupCalculator
{
    // Run the matchup for the supplied player and monster profiles.
    public static MonsterMatchupResult Compute(PlayerMatchupProfile player, MonsterMatchupProfile monster)
    {
        RealmType realm = player.Realm;

        // Player → monster. Some monsters carry a Dodge ability (e.g. Lord of the
        // Hunt); it raises their effective defence exactly like the player's dodge
        // does on the return direction. Monsters have no prot wards.
        HitCalcResult playerHit = CombatCalculator.CalculateHitChance(
            attackerAccuracy: player.NormalAccuracy,
            defenderAC: monster.ArmourClass,
            defenderDodge: monster.Dodge,
            realmType: realm);

        int playerDmgPerHit = System.Math.Max(0, player.AvgWeaponDamage - monster.DamageResist);

        // Fold critical hits into the per-swing average the same way MajorMUD's
        // round-damage does: a crit averages 3x the normal max (its own DR is
        // subtracted), blended by the crit chance. The displayed dmg/hit stays
        // the non-crit value (the "avg hit"); only DPS reflects crits.
        int critPerHit = System.Math.Max(0, player.AvgCritDamage - monster.DamageResist);
        double critPct = System.Math.Clamp(player.CritChancePercent, 0, 100) / 100.0;
        double effectivePerHit = ((1.0 - critPct) * playerDmgPerHit) + (critPct * critPerHit);

        double dps = player.HasWeapon
            ? playerHit.OverallHitPercent / 100.0 * effectivePerHit * player.SwingsPerRound
            : 0;
        // RoundsToKill is 0 when the player can't out-damage a kill (no weapon
        // or zero effective DPS) — the UI renders that as "—".
        int roundsToKill = dps > 0 ? (int)System.Math.Ceiling(monster.Hp / dps) : 0;

        // Monster → player. Only the primary physical slot is previewed.
        int monsterHit = 0;
        int monsterDmgPerHit = 0;
        if (monster.HasPhysicalAttack)
        {
            HitCalcResult mHit = CombatCalculator.CalculateHitChance(
                attackerAccuracy: monster.AttackAccuracy,
                defenderAC: player.ArmourClass,
                defenderDodge: player.Dodge,
                protEvil: monster.IsEvil ? player.ProtEvil : 0,
                protGood: monster.IsGood ? player.ProtGood : 0,
                realmType: realm);
            monsterHit = mHit.OverallHitPercent;
            monsterDmgPerHit = System.Math.Max(0, monster.AvgAttackDamage - player.DamageResist);
        }

        return new MonsterMatchupResult(
            PlayerHitPercent: playerHit.OverallHitPercent,
            PlayerDamagePerHit: playerDmgPerHit,
            PlayerSwingsPerRound: player.HasWeapon ? player.SwingsPerRound : 0,
            PlayerDps: dps,
            RoundsToKill: roundsToKill,
            HasWeapon: player.HasWeapon,
            MonsterHasPhysicalAttack: monster.HasPhysicalAttack,
            MonsterHitPercent: monsterHit,
            MonsterDamagePerHit: monsterDmgPerHit);
    }
}

// Player-side inputs to MonsterMatchupCalculator.Compute — the offensive numbers
// (normal-attack accuracy, average weapon damage, swings/round) and the
// defensive numbers (AC, dodge, prot wards, DR).
//   Realm             — active realm, selects the Stock / ParaMUD hit formula.
//   NormalAccuracy    — computed normal-attack accuracy (the to-hit number).
//   AvgWeaponDamage   — avg of the normal-attack min/max damage, before monster DR.
//   SwingsPerRound    — swings landed per round with the current weapon.
//   HasWeapon         — false when unarmed, gates DPS / rounds-to-kill.
//   ArmourClass       — player AC, the monster swings against.
//   Dodge             — player raw dodge value (not a percentage).
//   ProtEvil          — prot-evil ward, applied only when the monster is evil.
//   ProtGood          — prot-good ward, applied only when the monster is good.
//   DamageResist      — player DR, subtracted from each monster hit.
//   CritChancePercent — normal-attack crit chance (0-100), gear/quest crit +
//                       Quick-and-Deadly. Folds into DPS, not the per-hit display.
//   AvgCritDamage     — avg crit damage before the monster's DR (3x the normal max).
public readonly record struct PlayerMatchupProfile(
    RealmType Realm,
    int NormalAccuracy,
    int AvgWeaponDamage,
    double SwingsPerRound,
    bool HasWeapon,
    int ArmourClass,
    int Dodge,
    int ProtEvil,
    int ProtGood,
    int DamageResist,
    int CritChancePercent = 0,
    int AvgCritDamage = 0);

// Monster-side inputs to MonsterMatchupCalculator.Compute — defense
// (AC / DR / HP) and the primary physical attack slot (accuracy + average
// damage), plus the evil / good flags that gate the player's wards.
//   ArmourClass       — monster AC the player swings against.
//   DamageResist      — monster DR, subtracted from each player hit.
//   Hp                — monster max HP, the rounds-to-kill denominator.
//   Dodge             — monster raw dodge (the Dodge ability, abil 34), 0 for most.
//   HasPhysicalAttack — true when the monster has a melee / rob slot to preview.
//   AttackAccuracy    — primary physical slot's to-hit accuracy.
//   AvgAttackDamage   — avg of the primary physical slot's min/max, before player DR.
//   IsEvil            — monster is evil (Align in {1,2,5,6}), enables prot-evil.
//   IsGood            — monster is good (Align in {0,4}), enables prot-good.
public readonly record struct MonsterMatchupProfile(
    int ArmourClass,
    int DamageResist,
    int Hp,
    int Dodge,
    bool HasPhysicalAttack,
    int AttackAccuracy,
    int AvgAttackDamage,
    bool IsEvil,
    bool IsGood);

// Output of MonsterMatchupCalculator.Compute — both hit directions plus the
// player's DPS / rounds-to-kill projection.
//   PlayerHitPercent          — player normal-attack hit chance vs monster AC + dodge.
//   PlayerDamagePerHit        — player avg damage per landed hit, after monster DR.
//   PlayerSwingsPerRound      — swings/round used in the DPS projection (0 unarmed).
//   PlayerDps                 — projected damage per round: hit% * dmg/hit * swings.
//   RoundsToKill              — rounds to drop the monster at the projected DPS;
//                               0 when not killable (no weapon / zero DPS).
//   HasWeapon                 — whether the player had a weapon (gates DPS fields).
//   MonsterHasPhysicalAttack  — whether the monster has a physical slot to preview.
//   MonsterHitPercent         — monster's primary-physical hit chance vs player.
//   MonsterDamagePerHit       — monster avg damage per landed hit, after player DR.
public readonly record struct MonsterMatchupResult(
    int PlayerHitPercent,
    int PlayerDamagePerHit,
    double PlayerSwingsPerRound,
    double PlayerDps,
    int RoundsToKill,
    bool HasWeapon,
    bool MonsterHasPhysicalAttack,
    int MonsterHitPercent,
    int MonsterDamagePerHit);

// One of the player's known, damage-dealing attack spells — the input
// MonsterMatchupCalculator.RankAttackSpells scores against a specific
// monster's SpellImmu and elemental resist. MaxDamagePerRound and
// ManaCostPerRound are already level-scaled and energy-multiplied (see
// SpellCalculator.MaxDamage / ManaCost) — this record carries the RESULT of
// that player-side math, not the raw formula, so this file stays free of a
// Game.Spells dependency. AffectsUndeadOnly / AffectsLivingOnly mirror the
// spell's own Abil 23 / Abil 108 flags (AbilityNames.cs) — a caster-side
// target-type gate independent of SpellImmu and elemental resist.
public readonly record struct PlayerAttackSpell(
    string Name, string Short, int ReqLevel, int AttType,
    long MaxDamagePerRound, long ManaCostPerRound,
    bool AffectsUndeadOnly = false, bool AffectsLivingOnly = false);

// One spell's effectiveness against a specific monster — either blocked
// (SpellImmu too high, or the monster resists its element at or above 100%)
// with a human-readable reason, or eligible with its resist-adjusted
// effective damage.
public readonly record struct SpellEffectivenessResult(
    string Name, string Short, string Element, long EffectiveDamage,
    long ManaCostPerRound, bool Eligible, string? BlockedReason);

// Spell-matchup additions to MonsterMatchupCalculator below — kept as their
// own members rather than folded into Compute(), since the inputs (known
// spells, SpellImmu, elemental resists) share nothing with Compute()'s
// weapon/AC/dodge inputs and Compute() already has real callers (the
// Calculators tab's Hit Calculator) that must keep working unchanged.
public static class MonsterMatchupCalculatorSpells
{
    // Whether the player's currently-worn weapon can even land a physical hit —
    // MonsterMagicIndex.MagicalLevel(monster) is the level a weapon's own
    // HitMagic (ItemMagicIndex) must meet or exceed. A monster with no Magical
    // ability reads 0, so any weapon (including bare hands, HitMagic 0) hits.
    public static bool WeaponMeetsMagical(int weaponHitMagic, int monsterMagical)
        => weaponHitMagic >= monsterMagical;

    // Rank the player's known attack spells by effective damage against one
    // monster: a spell whose ReqLevel is below the monster's SpellImmu never
    // lands (GAME_MECHANICS' ReqLevel ≥ SpellImmu eligibility rule, mirroring
    // CombatManager.Spells.cs's SpellEligible); a spell restricted to undead-
    // only (Abil 23) or living-only (Abil 108) targets is blocked outright
    // against a monster on the wrong side of that gate, independent of
    // SpellImmu/resist; a spell whose element the monster resists ≥100% deals
    // zero real damage (MonsterResistIndex's own determinism — exactly 100
    // blocks, over 100 heals the target, so both read as blocked here, not
    // just "reduced to 0"). Everything else scores by (100 - resist%) applied
    // to the spell's own per-round max damage. Eligible spells sort first,
    // highest effective damage first; blocked spells trail, in their input
    // order.
    public static IReadOnlyList<SpellEffectivenessResult> RankAttackSpells(
        IReadOnlyList<PlayerAttackSpell> spells,
        int monsterSpellImmunity,
        IReadOnlyDictionary<int, int> monsterElementalResists,
        bool monsterIsUndead = false)
    {
        ArgumentNullException.ThrowIfNull(spells);
        ArgumentNullException.ThrowIfNull(monsterElementalResists);

        var results = new List<SpellEffectivenessResult>();
        foreach (PlayerAttackSpell s in spells)
        {
            string element = Game.GameData.LookupEnums.FormatSpellAttackType(
                s.AttType.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? "?";

            if (s.ReqLevel < monsterSpellImmunity)
            {
                results.Add(new SpellEffectivenessResult(s.Name, s.Short, element, 0,
                    s.ManaCostPerRound, Eligible: false,
                    $"Spell immune below level {monsterSpellImmunity} (this spell is {s.ReqLevel})"));
                continue;
            }

            if (s.AffectsUndeadOnly && !monsterIsUndead)
            {
                results.Add(new SpellEffectivenessResult(s.Name, s.Short, element, 0,
                    s.ManaCostPerRound, Eligible: false, "Affects undead only — this monster isn't undead"));
                continue;
            }
            if (s.AffectsLivingOnly && monsterIsUndead)
            {
                results.Add(new SpellEffectivenessResult(s.Name, s.Short, element, 0,
                    s.ManaCostPerRound, Eligible: false, "Affects living only — this monster is undead"));
                continue;
            }

            int resistCode = Game.Combat.MonsterResistIndex.ElementalResistCode(s.AttType);
            int resistPercent = resistCode >= 0 && monsterElementalResists.TryGetValue(resistCode, out int pct)
                ? pct : 0;
            if (resistPercent >= 100)
            {
                results.Add(new SpellEffectivenessResult(s.Name, s.Short, element, 0,
                    s.ManaCostPerRound, Eligible: false,
                    resistPercent == 100 ? $"{element} fully resisted" : $"{element} heals this monster instead"));
                continue;
            }

            long effective = (long)System.Math.Round(
                s.MaxDamagePerRound * (100 - resistPercent) / 100.0, System.MidpointRounding.AwayFromZero);
            results.Add(new SpellEffectivenessResult(
                s.Name, s.Short, element, effective, s.ManaCostPerRound, Eligible: true, BlockedReason: null));
        }

        return results
            .OrderByDescending(r => r.Eligible)
            .ThenByDescending(r => r.EffectiveDamage)
            .ToList();
    }
}

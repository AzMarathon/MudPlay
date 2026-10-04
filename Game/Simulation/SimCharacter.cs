using MudPlay.Game.Calculators;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;

namespace MudPlay.Game.Simulation;

// One spell the character knows, pre-scaled to their level. Damage is the
// per-ROUND range (every fire of a round summed, the caster's spell-damage bonus
// folded in); a round's fires split it evenly. Heal is one cast's range. ManaPerCast
// is charged on every fire, landed or not (GAME_MECHANICS "You attempt to cast
// <spell>, but fail." — cast but missed). CastChance is null for a non-caster.
// DurationSeconds is how long a landed buff / debuff lasts; ManaRegen / HpRegen are
// the regen-percent range it adds while up (a roll spell's range, or a fixed value
// as both ends); Debuff is what it strips off a monster it lands on.
public sealed record SimSpell(
    string Short, int Number, int ManaPerCast, int FiresPerRound, int? CastChance,
    long MinDamagePerRound, long MaxDamagePerRound, bool UsesMagicResist, int TypeOfResists,
    int AttType, long MinHeal, long MaxHeal,
    double DurationSeconds = 0,
    int ManaRegenMin = 0, int ManaRegenMax = 0,
    int HpRegenMin = 0, int HpRegenMax = 0,
    MonsterDebuffEffect Debuff = default);

// One self-maintained buff from the Buffs list, in cast-priority order: recast when
// it's down or within RecastMarginSec of wearing off. A roll spell recasts at once
// while its roll lands below RerollBelow, up to RerollCount times (or without limit).
public sealed record SimBuff(
    string Spell, int RecastMarginSec, bool OnlyWhenHpFull, bool OnlyWhenMaFull,
    bool BeforeRestingForMana, int? RerollBelow, int RerollCount, bool RerollInfinite);

// Per-tick regen, as functions of the regen percent active buffs add (ManaRgn /
// HPRegen), on the realm cadence they arrive on (RealmRegenProfile).
//
// Stock: the standing tick pays HP and mana together; resting adds a full HpResting
// tick on its own count from lying down, and every rest tick is the same.
//
// Paradigm: rest takes the standing HP gain's place and rides the round grid, and
// the amount runs in threes counted from lying down — RestRun gains of HpRestingLow,
// then RestRun of HpResting, and round again (GAME_MECHANICS "Rest and meditate
// tick timing"). The low and standing gains are thirds of the amount before the
// regen percent; HpLowExtra is what the percent adds to the whole amount, paid on
// the last low gain of a run and on the standing gain that comes with the mana pass.
//
// MaMeditating always adds on top of the standing mana tick.
public sealed record SimRegen(
    Func<int, double> HpStanding, Func<int, double> HpResting,
    Func<int, double> MaStanding, double MaMeditating,
    RealmRegenProfile Cadence, bool RestReplacesStanding,
    Func<int, double>? HpRestingLow = null, int RestRun = 1,
    Func<int, double>? HpLowExtra = null)
{
    // HP paid by the tick-th rest tick since lying down (1-based), with extra HP-regen
    // percent from active buffs.
    public double RestTickHp(long tick, int extra = 0)
    {
        int run = Math.Max(1, RestRun);
        if (HpRestingLow is not { } low || (tick - 1) / run % 2 != 0) return HpResting(extra);
        bool lastOfRun = (tick - 1) % run == run - 1;
        return low(extra) + (lastOfRun ? StandingPassExtra(extra) : 0);
    }

    // What the regen percent adds to the standing gain that lands on the mana pass.
    public double StandingPassExtra(int extra = 0) => HpLowExtra?.Invoke(extra) ?? 0;
}

// Everything the loop simulator plays by: the character's pools, offense, defense
// and regen, their spellbook and buff list, and the live Combat / Health / Spells
// settings the engines read — so a simulated round picks what the client would pick.
// AlignmentValue is the character's alignment number (evil points, or the who
// title's band when the points aren't known). HangupsDisabled is the General tab's
// master switch that stops the Health hang-up. DefaultMaxHp / DefaultMaxMana are the
// Default gear set's pools, the basis the live engines resolve rest and heal
// thresholds against (GAME_MECHANICS "Rest basis — the Default-gear baseline");
// 0 = unknown, the current max stands in. Backstab is the sneak opener's profile
// (the Backstab set's weapon in hand), null when the character doesn't open with one
// (Backstab off, Auto-Sneak off, or no Stealth); BackstabHitMagic is that weapon's
// hit-magic level.
public sealed record SimCharacter(
    RealmType Realm,
    int Level,
    int MaxHp,
    int MaxMana,
    PlayerMatchupProfile Melee,
    int WeaponHitMagic,
    PlayerDefenseProfile Defense,
    SimRegen Regen,
    IReadOnlyDictionary<string, SimSpell> Spells,
    CombatSettings Combat,
    HealthSettings Health,
    SpellsSettings SpellSlots,
    Func<int, MonsterOverlay> Overlay,
    Func<int, string?> SpellShortByNumber,
    int AlignmentValue,
    IReadOnlyList<SimBuff>? Buffs = null,
    bool HangupsDisabled = false,
    int DefaultMaxHp = 0,
    int DefaultMaxMana = 0,
    PlayerMatchupProfile? Backstab = null,
    int BackstabHitMagic = 0);

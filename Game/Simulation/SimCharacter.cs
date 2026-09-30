using MudPlay.Game.Calculators;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;

namespace MudPlay.Game.Simulation;

// One spell the character knows, pre-scaled to their level. Damage is the
// per-ROUND range (every fire of a round summed, the caster's spell-damage bonus
// folded in); a round's fires split it evenly. Heal is one cast's range. ManaPerCast
// is charged on every fire, landed or not (GAME_MECHANICS "You attempt to cast
// <spell>, but fail." — cast but missed). CastChance is null for a non-caster.
public sealed record SimSpell(
    string Short, int Number, int ManaPerCast, int FiresPerRound, int? CastChance,
    long MinDamagePerRound, long MaxDamagePerRound, bool UsesMagicResist, int TypeOfResists,
    int AttType, long MinHeal, long MaxHeal);

// Per-tick regen amounts and the realm cadence they arrive on (RealmRegenProfile).
// Rest ticks count from the moment the character lies down: every RestFullEvery-th
// tick pays HpResting and the ones between pay HpResting × RestReducedShare. On
// Stock every rest tick is full and adds to the standing tick; on Paradigm rest
// replaces the standing tick and runs in cycles of three 10 s ticks, the third
// full (GAME_MECHANICS "Rest and meditate tick timing"). MaMeditating always adds
// on top of the standing mana tick.
public readonly record struct SimRegen(
    double HpStanding, double HpResting, double MaStanding, double MaMeditating,
    RealmRegenProfile Cadence, bool RestReplacesStanding, int RestFullEvery = 1, double RestReducedShare = 1)
{
    // HP paid by the tick-th rest tick since lying down (1-based).
    public double RestTickHp(long tick) =>
        tick % Math.Max(1, RestFullEvery) == 0 ? HpResting : HpResting * RestReducedShare;
}

// Everything the loop simulator plays by: the character's pools, offense, defense
// and regen, their spellbook, and the live Combat / Health / Spells settings the
// engines read — so a simulated round picks what the client would pick.
// AlignmentValue is the character's alignment number (evil points, or the who
// title's band when the points aren't known). DefaultMaxHp / DefaultMaxMana are the
// Default gear set's pools, the basis the live engines resolve rest and heal
// thresholds against (GAME_MECHANICS "Rest basis — the Default-gear baseline");
// 0 = unknown, the current max stands in.
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
    int DefaultMaxHp = 0,
    int DefaultMaxMana = 0);

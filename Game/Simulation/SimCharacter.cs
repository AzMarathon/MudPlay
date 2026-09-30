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
// HpResting replaces the standing tick while resting on Paradigm (rest rides the
// same 10 s grid at 3x) and adds its own tick on Stock; MaMeditating always adds on
// top of the standing mana tick.
public readonly record struct SimRegen(
    double HpStanding, double HpResting, double MaStanding, double MaMeditating,
    RealmRegenProfile Cadence, bool RestReplacesStanding);

// Everything the loop simulator plays by: the character's pools, offense, defense
// and regen, their spellbook, and the live Combat / Health / Spells settings the
// engines read — so a simulated round picks what the client would pick.
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
    int EvilPoints);

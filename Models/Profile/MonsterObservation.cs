using System.Text.Json.Serialization;

namespace MudPlay.Models.Profile;

// One per-character record of combat outcomes actually observed against a
// specific monster, maintained by Game.Combat.MonsterObservationTracker and
// surfaced by Monster Intel's "Your Observations" section. Deliberately
// separate from MonsterCatalogEntry (the authoritative MDB facts) — this is
// what this character has personally seen happen, not a game-data record.
//
// Keyed on MonsterNumber (MonsterCatalogEntry.Number), matching the identity
// RoomEntity already resolves a combat line's target name to.
public sealed class MonsterObservation
{
    public int MonsterNumber { get; set; }

    // Landed weapon-swing damage. Min/Max are only meaningful once
    // HitCount > 0 — display code checks that, mirroring
    // CombatSessionTracker's DamageTally convention.
    public int HitCount { get; set; }
    public int HitDamageMin { get; set; }
    public int HitDamageMax { get; set; }
    public long HitDamageSum { get; set; }

    // Whiffed weapon swings against this monster specifically (attributed via
    // the live combat target, since the wire's miss line carries no name).
    public int MissCount { get; set; }

    // "Your weapon/fists have no effect against this monster!" — a confirmed
    // discovery that this monster's Magical requirement exceeds what you're
    // hitting it with. Weapon and fists folded into one counter: both mean
    // the same thing (physical damage isn't getting through).
    public int PhysicalNoEffectCount { get; set; }

    // "Your spell has no effect on <monster>." — a confirmed discovery that a
    // cast spell's level didn't clear this monster's SpellImmunity.
    public int SpellNoEffectCount { get; set; }

    public DateTimeOffset FirstObservedAt { get; set; }
    public DateTimeOffset LastObservedAt { get; set; }

    [JsonIgnore]
    public double AvgHitDamage => HitCount == 0 ? 0 : (double)HitDamageSum / HitCount;

    [JsonIgnore]
    public int SwingCount => HitCount + MissCount;

    [JsonIgnore]
    public double HitRatePercent => SwingCount == 0 ? 0 : 100.0 * HitCount / SwingCount;
}

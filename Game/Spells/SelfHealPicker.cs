using MudPlay.Game.Health;
using MudPlay.Models.Profile;

namespace MudPlay.Game.Spells;

// The pools and lookups one self-heal decision reads. HealHpTrigger resolves a
// Health-tab heal trigger (per HpThresholdMode) to an absolute HP; Affordable says
// whether the pool covers a spell (an unknown cost never blocks); HpRegenRecastDue
// says whether the HP-regen HoT slot is due for a recast.
public readonly record struct SelfHealInputs(
    int Hp, int MaxHp, int Ma, int MaxMa, bool InCombat, bool Resting,
    Func<ThresholdMode, int, int> HealHpTrigger,
    Func<string, bool> Affordable,
    Func<string, bool> HpRegenRecastDue);

// Which self-heal tier is due, and with which spell — the Emergency / Major / Minor
// picks CastingDirector ranks against the rest of the between-round queue. Pure, so
// the loop simulator heals at exactly the thresholds the live client does.
public static class SelfHealPicker
{
    // Last-resort self-save. Deliberately does NOT gate on ManaClearsHealFloor
    // (unlike Major/Minor below) — an emergency spends whatever mana is left
    // rather than conserving the pool for a "later" that might not come, and it
    // fires in ANY state (combat, resting, mid-walk), not just combat/rest like
    // Minor's own position gate. Falls back Emergency → Major → Minor so a
    // player who's only configured the older two tiers still gets a life-threat
    // save at the new, lower trigger once they set EmergencyHealTrigger.
    public static string? Emergency(SpellsSettings spells, HealthSettings health, in SelfHealInputs s)
    {
        if (s.MaxHp <= 0) return null;
        if (s.Hp > s.HealHpTrigger(health.HpThresholdMode, health.EmergencyHealTrigger)) return null;
        if (!string.IsNullOrWhiteSpace(spells.EmergencyHealSpell)) return spells.EmergencyHealSpell;
        if (!string.IsNullOrWhiteSpace(spells.MajorHealSpell)) return spells.MajorHealSpell;
        return spells.MinorHealSpell;
    }

    public static string? Major(SpellsSettings spells, HealthSettings health, in SelfHealInputs s)
    {
        if (s.MaxHp <= 0) return null;
        if (!ManaClearsHealFloor(health, s)) return null;
        if (s.Hp > s.HealHpTrigger(health.HpThresholdMode, health.MajorHealCombatTrigger)) return null;
        // Fall back to minor when the user hasn't configured a major
        // — better to fire something than skip the life-threat path.
        return !string.IsNullOrWhiteSpace(spells.MajorHealSpell)
            ? spells.MajorHealSpell
            : spells.MinorHealSpell;
    }

    public static string? Minor(SpellsSettings spells, HealthSettings health, in SelfHealInputs s)
    {
        if (s.MaxHp <= 0) return null;
        if (!ManaClearsHealFloor(health, s)) return null;

        // Use the in-combat trigger while engaged, the rest-time
        // trigger otherwise (matches the user's two-threshold mental
        // model from the Health tab).
        int triggerValue = s.InCombat ? health.MinorHealCombatTrigger : health.HealRestTrigger;
        if (s.Hp > s.HealHpTrigger(health.HpThresholdMode, triggerValue)) return null;

        // Out-of-combat heal-spell-during-rest only — don't cast
        // mid-walk between rooms.
        if (!s.InCombat && !s.Resting) return null;

        // Prefer an HP-regen HoT (regeneration / rejuvinating field) over the
        // single-target heal: once it's ticking it restores far more per mana
        // than repeated instant heals, so cast it FIRST when the minor-heal
        // trigger trips. Two gates keep it safe:
        //  • It's only substituted while HP sits ABOVE the major-heal trigger —
        //    inside the life-threat band we want the instant top-up, never a
        //    slow HoT that heals a round later.
        //  • HpRegenRecastDue is false once the HoT is confirmed active with
        //    remaining duration, so a running HoT falls through to the instant
        //    single-target heal for the immediate top-up while it ticks.
        int majorTrigger = s.HealHpTrigger(health.HpThresholdMode, health.MajorHealCombatTrigger);

        // Two exclusive bands. Once HP falls into the major-heal band, yield to
        // Major instead of firing minor again. Minor is walked BEFORE major
        // (lower priority int by default), and without this lower bound minor
        // matched the whole Hp<=minorTrigger range and fired even at single-digit
        // HP — major was dead code in combat and the player died (report
        // paradigm-20260819-121247: minor cast at 13/142 HP with mana to spare).
        // Yield only when a major spell is configured AND affordable, so a
        // mana-starved caster still falls back to the cheaper minor heal rather
        // than healing nothing.
        if (s.Hp <= majorTrigger
            && !string.IsNullOrWhiteSpace(spells.MajorHealSpell)
            && s.Affordable(spells.MajorHealSpell))
            return null;

        if (s.Hp > majorTrigger
            && !string.IsNullOrWhiteSpace(spells.HpRegenSpell)
            && s.HpRegenRecastDue(spells.HpRegenSpell))
            return spells.HpRegenSpell;

        return string.IsNullOrWhiteSpace(spells.MinorHealSpell) ? null : spells.MinorHealSpell;
    }

    // Mana-floor gate for self heals: only cast a heal when the caster pool sits at
    // or above HealIfAboveMaCombat (in combat) or HealIfAboveMaResting (resting /
    // idle), so a low pool regenerates instead of being drained on heal spells. A
    // floor of 0 disables the gate. An unknown pool (MaxMa 0, percentage mode)
    // resolves to 0, so a heal is never blocked before prompt data loads.
    public static bool ManaClearsHealFloor(HealthSettings health, in SelfHealInputs s)
    {
        int floorValue = s.InCombat ? health.HealIfAboveMaCombat : health.HealIfAboveMaResting;
        if (floorValue <= 0) return true;
        return s.Ma >= PoolThreshold.Resolve(health.MaThresholdMode, floorValue, s.MaxMa);
    }
}

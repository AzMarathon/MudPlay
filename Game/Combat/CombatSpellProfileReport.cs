using System;
using System.Collections.Generic;
using MudPlay.Models.Profile;

namespace MudPlay.Game.Combat;

// Builds the one-line "switched to profile" report shown on the terminal / program
// log and replied by @profile. It names the profile by number (+ name when set)
// and lists what spell sits in each slot BY ITS CAST CODE — never the full spell
// name — in cast order, empty slots omitted.
public static class CombatSpellProfileReport
{
    public static string Describe(CombatSpellProfile profile, int number)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string label = string.IsNullOrWhiteSpace(profile.Name)
            ? $"Combat profile {number}"
            : $"Combat profile {number} ({profile.Name.Trim()})";

        var parts = new List<string>();
        Add(parts, "multi", profile.MultiAttackSpell);
        Add(parts, "AoE-debuff", profile.AreaDebuffSpell);
        Add(parts, "debuff", profile.SingleTargetDebuffSpell);
        Add(parts, "normal", profile.NormalAttackSpell);
        Add(parts, "alt", profile.AlternateAttackSpell);
        Add(parts, "drain", profile.DrainSpell);

        string slots = parts.Count > 0 ? string.Join(" · ", parts) : "no spells set";
        return $"{label} — {slots}";
    }

    private static void Add(List<string> parts, string label, CombatSpellSlot slot)
    {
        string? code = slot?.SpellName?.Trim();
        if (!string.IsNullOrEmpty(code)) parts.Add($"{label}: {code}");
    }

    // Full-config one-liner: every slot (empty shown as —) with its gates, plus
    // the profile-level knobs. Feeds the program log (switch Debug line, combat-
    // engage Combat line) and the bug report, so "how it's configured" is captured
    // without opening Settings. Cast codes only, never full spell names — same rule
    // as Describe.
    public static string DescribeConfig(CombatSpellProfile profile, int number)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string label = string.IsNullOrWhiteSpace(profile.Name)
            ? $"Combat profile {number}"
            : $"Combat profile {number} ({profile.Name.Trim()})";

        // MinEnemies is honoured only on the two room-wide rows; the engine ignores
        // it on the four single-target rows, so don't report it there.
        string slots = string.Join(" · ", new[]
        {
            SlotDetail("multi", profile.MultiAttackSpell, roomWide: true),
            SlotDetail("AoE-debuff", profile.AreaDebuffSpell, roomWide: true),
            SlotDetail("debuff", profile.SingleTargetDebuffSpell, roomWide: false),
            SlotDetail("normal", profile.NormalAttackSpell, roomWide: false),
            SlotDetail("alt", profile.AlternateAttackSpell, roomWide: false),
            SlotDetail("drain", profile.DrainSpell, roomWide: false),
        });

        string knobs = $"mana-mode={profile.SpellManaThresholdMode}" +
                       $" · drain-HP-trigger={profile.DrainHpTrigger}" +
                       $" · drains-override-AoE={(profile.DrainsOverrideAoe ? "on" : "off")}";

        return $"{label}: {slots} · {knobs}";
    }

    private static string SlotDetail(string label, CombatSpellSlot slot, bool roomWide)
    {
        string? code = slot?.SpellName?.Trim();
        if (string.IsNullOrEmpty(code)) return $"{label} —";

        var gates = new List<string>();
        if (roomWide && slot!.MinEnemies > 0) gates.Add($"≥{slot.MinEnemies}");
        if (slot!.MaxCastsPerRoom is { } max) gates.Add(max == 0 ? "×0" : $"×{max}");
        if (slot.MinManaPerCast > 0) gates.Add($"m{slot.MinManaPerCast}");

        return gates.Count > 0 ? $"{label} {code}({string.Join(", ", gates)})" : $"{label} {code}";
    }

    // The @profile query roster: the active profile flagged Current, the rest On
    // Standby, each as "<number>)<name>" — e.g.
    // "{Current: 1)Fire, On Standby: 2)Cold, 3)Lightning}". An unnamed profile
    // shows "unnamed"; a lone profile omits the On-Standby clause.
    public static string DescribeRoster(IReadOnlyList<CombatSpellProfile> profiles, int activeIndex)
    {
        if (profiles is null || profiles.Count == 0) return "{no combat profiles}";
        int active = activeIndex >= 0 && activeIndex < profiles.Count ? activeIndex : 0;

        var standby = new List<string>();
        for (int i = 0; i < profiles.Count; i++)
            if (i != active) standby.Add(Slot(i, profiles[i]));

        string body = "Current: " + Slot(active, profiles[active]);
        if (standby.Count > 0) body += ", On Standby: " + string.Join(", ", standby);
        return "{" + body + "}";
    }

    private static string Slot(int index, CombatSpellProfile p)
        => $"{index + 1}){(string.IsNullOrWhiteSpace(p.Name) ? "unnamed" : p.Name.Trim())}";
}

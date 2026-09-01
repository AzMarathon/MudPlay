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
}

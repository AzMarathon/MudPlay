using System;
using System.Collections.Generic;
using System.Linq;

namespace MudPlay.Game.Spells;

// Who a configured buff slot's spell can land on, derived from its targeting
// flags — the shape needed to tell whether two slots could ever affect the same
// character (a precondition for a RemovesSpell conflict between their spells to
// matter in play). Built from a slot's raw flags, not live party state, so a
// specific-member list "safely outlives the party it was built for" the same way
// BuffSlot.Targets itself does.
public readonly struct BuffAffectSet
{
    public bool Everyone { get; init; }          // whole-party cast — lands on self + every member
    public bool Self { get; init; }              // CastOnSelf ticked
    public bool AllMembers { get; init; }         // single-target slot's AllMembers auto-adapt
    public IReadOnlyCollection<string> Members { get; init; } // single-target slot's explicit Targets (lower-cased)

    public static readonly BuffAffectSet None = new() { Members = Array.Empty<string>() };

    public bool IsEmpty => !Everyone && !Self && !AllMembers && Members.Count == 0;

    // isWholePartySpell: the slot's spell has Targets 10/13 (BuffClassifier.IsWholeParty).
    // wholePartyOn: the slot's all-on/all-off toggle for a whole-party spell.
    // castOnSelf/allMembers/targets: BuffSlot's targeting flags, meaningful only when
    // the spell isn't whole-party (a whole-party cast always includes self and needs
    // no member list).
    public static BuffAffectSet From(
        bool isWholePartySpell, bool wholePartyOn,
        bool castOnSelf, bool allMembers, IReadOnlyCollection<string> targets)
    {
        if (isWholePartySpell)
            return wholePartyOn ? new BuffAffectSet { Everyone = true, Members = Array.Empty<string>() } : None;

        return new BuffAffectSet
        {
            Self = castOnSelf,
            AllMembers = allMembers,
            Members = allMembers
                ? Array.Empty<string>()
                : targets.Select(t => t.Trim().ToLowerInvariant()).ToArray(),
        };
    }
}

// Cross-references two configured buff slots against the spellbook's RemovesSpell
// (Abil 122) relation. Purely informational — see AppServices.BuffSlotOverwritePairs
// for the caller that builds these from live BuffSettings, and SelfBuffCoverage for
// the one case (self superseded by a whole-party buff) that also drives automation.
public readonly record struct BuffOverwritePair(
    string RemovingCode, string RemovingName, string RemovedCode, string RemovedName);

public static class BuffConflictAnalyzer
{
    // True when two slots' targeting could ever land on the same character — the
    // precondition for a RemovesSpell relation between their spells to matter.
    public static bool CanCoLand(in BuffAffectSet a, in BuffAffectSet b)
    {
        if (a.IsEmpty || b.IsEmpty) return false;
        if (a.Everyone || b.Everyone) return true;
        if (a.Self && b.Self) return true;
        if (a.AllMembers && (b.AllMembers || b.Members.Count > 0)) return true;
        if (b.AllMembers && a.Members.Count > 0) return true;
        foreach (string m in a.Members)
            if (b.Members.Contains(m)) return true;
        return false;
    }

    // Combines a conflict-pair list into a per-cast-code lookup: the name of another
    // configured buff that removes castCode (RemovedBy) and/or the name of another it
    // removes (Removes). Shared by the Buff Panel's per-row warning and the Buff
    // Watchdog's row annotation so "which pairs conflict" is answered in one place.
    public static (string? RemovedBy, string? Removes) Resolve(
        IReadOnlyList<BuffOverwritePair> pairs, string castCode)
    {
        string? removedBy = null, removes = null;
        foreach (BuffOverwritePair p in pairs)
        {
            if (removedBy is null && string.Equals(p.RemovedCode, castCode, StringComparison.OrdinalIgnoreCase))
                removedBy = p.RemovingName;
            if (removes is null && string.Equals(p.RemovingCode, castCode, StringComparison.OrdinalIgnoreCase))
                removes = p.RemovedName;
        }
        return (removedBy, removes);
    }

    // One combined tooltip covering both directions, or null when neither applies.
    public static string? FormatTooltip(string? removedBy, string? removes)
    {
        if (removedBy is null && removes is null) return null;
        List<string> lines = new();
        if (removedBy is not null) lines.Add($"Removed by: {removedBy}");
        if (removes is not null) lines.Add($"Removes: {removes}");
        return string.Join("\n", lines);
    }
}

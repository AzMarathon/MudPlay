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

// A self-castable buff eligible for the Buff Panel's "Add all blesses" bulk add
// — self-only or single-target-on-self, alignment-eligible, not already slotted.
// Removes is its Abil-122 RemovesSpell target numbers. Neither level- nor
// obtained-gated: the whole point is a browsable roster of every buff the class
// could ever have, learned or not, so the player can theorycraft ahead of
// actually training something. IsObtained still matters for which candidate gets
// pre-checked (see SelectSelfBlessCandidates) — defaults true so existing callers
// that only care about the RemovesSpell math don't have to plumb it through.
public readonly record struct SelfBlessCandidate(
    string CastCode, string Name, int Number, int ReqLevel, int ManaCost,
    IReadOnlyCollection<int> Removes, bool IsObtained = true);

// One SelfBlessCandidate as "Add all blesses" presents it: always added as a row,
// but only Recommended candidates come pre-checked (Self ticked) — the rest are
// listed unchecked so the player can swap one in by hand.
public readonly record struct SelfBlessPick(SelfBlessCandidate Candidate, bool Recommended);

// An already-configured slot's spell identity + who it can land on + what it
// removes — enough to tell whether a self-bless candidate would clobber it, or be
// clobbered by it, without needing the slot DTO itself.
public readonly record struct ExistingBuffSlot(
    int Number, BuffAffectSet Affect, IReadOnlyCollection<int> Removes);

public static class BuffConflictAnalyzer
{
    // The Abil code the Spells table uses for RemovesSpell.
    public const int RemovesSpellAbil = 122;

    // The Abil-122 (RemovesSpell) target spell numbers carried in a spell's formula.
    public static HashSet<int> RemovedSpellNumbers(SpellFormulaInput formula)
    {
        HashSet<int> nums = new();
        foreach (SpellAbility a in formula.Abilities)
            if (a.Code == RemovesSpellAbil) nums.Add(a.Value);
        return nums;
    }

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

    // The Buff Panel's "Add all blesses" pick: EVERY self-castable candidate gets a
    // row — learned or not — so the whole roster of what the character could ever
    // self-bless is browsable and any auto-pick can be swapped for another family
    // member by hand — but only one per RemovesSpell family (typically a tiered
    // pair like zeal / greater zeal, which mutually remove each other) comes
    // pre-checked: the highest-ReqLevel OBTAINED member (an unlearned pick is
    // never auto-checked — the game would just refuse the cast), and only when it
    // wouldn't immediately clobber — or be clobbered by — something already active
    // in an existing slot. A cluster with no obtained member recommends nothing. A
    // self candidate is always assumed able to co-land with itself, so CanCoLand
    // only needs to gate against the EXISTING slot's own targeting (a single-target
    // slot aimed at other members only, for instance, can never conflict with a
    // self cast).
    public static IReadOnlyList<SelfBlessPick> SelectSelfBlessCandidates(
        IReadOnlyList<SelfBlessCandidate> pool, IReadOnlyList<ExistingBuffSlot> existing)
    {
        if (pool.Count == 0) return Array.Empty<SelfBlessPick>();

        BuffAffectSet selfAffect = new() { Self = true, Members = Array.Empty<string>() };
        bool ClobbersExisting(SelfBlessCandidate cand)
        {
            foreach (ExistingBuffSlot slot in existing)
            {
                if (!CanCoLand(selfAffect, slot.Affect)) continue;
                if (cand.Removes.Contains(slot.Number) || slot.Removes.Contains(cand.Number)) return true;
            }
            return false;
        }

        Dictionary<int, int> parent = pool.ToDictionary(s => s.Number, s => s.Number);
        int Find(int x) => parent[x] == x ? x : (parent[x] = Find(parent[x]));
        void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra != rb) parent[ra] = rb;
        }
        HashSet<int> poolNumbers = pool.Select(s => s.Number).ToHashSet();
        foreach (SelfBlessCandidate cand in pool)
            foreach (int removed in cand.Removes)
                if (poolNumbers.Contains(removed)) Union(cand.Number, removed);

        HashSet<int> recommended = new();
        foreach (IGrouping<int, SelfBlessCandidate> cluster in pool.GroupBy(s => Find(s.Number)))
        {
            SelfBlessCandidate? preferred = null;
            foreach (SelfBlessCandidate cand in cluster)
            {
                if (!cand.IsObtained) continue;
                if (preferred is not { } cur
                    || cand.ReqLevel > cur.ReqLevel
                    || (cand.ReqLevel == cur.ReqLevel && cand.ManaCost > cur.ManaCost))
                    preferred = cand;
            }
            if (preferred is { } p && !ClobbersExisting(p)) recommended.Add(p.Number);
        }

        return pool
            .Select(s => new SelfBlessPick(s, recommended.Contains(s.Number)))
            .OrderBy(p => p.Candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

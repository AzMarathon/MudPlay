using System.Collections.Generic;
using MudPlay.Game.Calculators;

namespace MudPlay.Game.Quests;

// Single source of truth for "can this character complete this quest?" — used by
// both the Quest Status journal (QuestSectionViewModel) and the availability
// announcer (QuestEligibility) so the tab's "Cannot complete" mark and the login
// availability dump never disagree.
//
// Beyond the crawl's own class/race guards it applies two player-facing gates:
//   • an explicit per-quest class restriction (QuestDefinition.ClassRestrict) — the
//     hand-set override for genuinely class-locked quests the crawl can't detect
//     reliably (Magebane, Tarl), and
//   • the character's alignment-quest checkboxes — an alignment-gated quest
//     (CrawledQuest.RequiredAlignment) is eligible only when its bucket is enabled.
// A null classId (the classless default profile) never triggers the class gates —
// eligibility stays open rather than hiding a quest whose viewer has no class.
public static class QuestEligibilityResolver
{
    public static bool IsIneligible(
        CrawledQuest quest,
        int? classId,
        int? raceId,
        IReadOnlyList<int>? classRestrictOverride,
        bool allowGood,
        bool allowNeutral,
        bool allowEvil)
    {
        if (quest.ClassIds is { Count: > 0 } cls && classId is int c && !cls.Contains(c)) return true;
        if (quest.RaceIds is { Count: > 0 } rcs && raceId is int r && !rcs.Contains(r)) return true;
        if (classRestrictOverride is { Count: > 0 } ov && classId is int co && !ov.Contains(co)) return true;
        if (quest.RequiredAlignment is { } a && !AlignmentEnabled(a, allowGood, allowNeutral, allowEvil)) return true;
        return false;
    }

    private static bool AlignmentEnabled(AlignmentBucket a, bool good, bool neutral, bool evil) => a switch
    {
        AlignmentBucket.Good => good,
        AlignmentBucket.Neutral => neutral,
        AlignmentBucket.Evil => evil,
        _ => true,
    };
}

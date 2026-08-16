using System.Collections.Generic;

namespace MudPlay.Models.Profile;

// Per-character completion state for one crawled quest (or alignment band),
// keyed by the same (Flag, Step) identity QuestCrawler emits. Persisted as
// CharacterProfile.QuestLog — separate from the per-set quest definitions
// (names / visibility), so a completion follows the character across sets.
//
// Complete is the effective, bonus-applying state. It's set either by ticking
// the quest's manual complete box or, for a single-part quest with a followable
// checklist, by ticking every step (CheckedSteps holds the give-step orders the
// user has ticked so partial progress survives a reload). Unticking a step never
// auto-clears Complete — the manual box owns that.
public sealed class QuestProgress
{
    // Quest-flag ability id (the giveability <flag> target).
    public int Flag { get; set; }

    // Band level for a multi-part quest; 0 for a single-part quest.
    public int Step { get; set; }

    // True when the quest counts as done — applies its bonus to Character Info.
    public bool Complete { get; set; }

    // Give-step orders the user has ticked in the followable checklist
    // (single-part quests only). null / empty when nothing's ticked.
    public List<int>? CheckedSteps { get; set; }

    // Per-character opt-in to keep this quest in the journal even though the
    // character can't complete it (wrong class/race/alignment, or a class
    // restriction). Default false: a "Cannot complete" quest is hidden unless the
    // user ticks "Show in quest journal" for it in the editor. Lives here rather
    // than on the per-set QuestDefinition because eligibility is a per-character
    // fact, so the choice to override it follows the character, not the board.
    // Never affects the login availability dump — that always excludes ineligible
    // quests.
    public bool ShowIfIneligible { get; set; }

    public QuestProgress() { }

    public QuestProgress(int flag, int step)
    {
        Flag = flag;
        Step = step;
    }
}

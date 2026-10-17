using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Quests;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.ViewModels.CharacterWorkshop;

// Moves a character's saved quest-step ticks onto the steps they were made on, once each
// time the drafted checklists gained lines: first their kill lines, then their gather
// lines (fetching an item a later step needs). A tick is kept as the number of its
// checkbox, and a line drafted ahead of a ticked step pushes that step down; left alone,
// every later tick would sit too early and the last would fall off.
//
// The profile's schema upgrade marks a profile whose ticks predate the kill lines
// (CharacterProfile.QuestTicksPredateKillSteps); CharacterProfile.QuestDraftRevision
// records the draft the ticks were last made against. Both are brought up to date when
// this has run, so it runs once per character per change and a second call does nothing.
internal static class QuestTickMigration
{
    // The drafted checklists' current shape: 1 = with gather lines.
    public const int CurrentDraftRevision = 1;

    // resolve gives a quest's definition by flag and step; itemSources / itemDrops are
    // the indexes the Quest tab drafts with, so the gather lines are numbered as it shows
    // them. True when the ticks were gone through and the marks brought up to date, so
    // the caller saves the profile; moved counts the quests whose ticks changed.
    public static bool Apply(CharacterProfile profile, GameDataCache gameData,
        System.Func<int, int, QuestDefinition> resolve, out int moved,
        ItemSourceIndex? itemSources = null, MonsterDropIndex? itemDrops = null)
    {
        System.ArgumentNullException.ThrowIfNull(profile);
        System.ArgumentNullException.ThrowIfNull(gameData);
        System.ArgumentNullException.ThrowIfNull(resolve);
        moved = 0;
        bool beforeKillSteps = profile.QuestTicksPredateKillSteps;
        if (!beforeKillSteps && profile.QuestDraftRevision >= CurrentDraftRevision) return false;

        // Where the new lines fall is read from the game data; with no set loaded there
        // is no draft to place the ticks on, so the marks stay for the next time.
        IReadOnlyList<CrawledQuest> crawled = QuestCrawler.Crawl(gameData, null);
        if (crawled.Count == 0) return false;

        foreach (QuestProgress progress in profile.QuestLog ?? Enumerable.Empty<QuestProgress>())
        {
            if (progress.CheckedSteps is not { Count: > 0 } ticks) continue;
            CrawledQuest? quest = crawled.FirstOrDefault(q => q.Flag == progress.Flag && q.Step == progress.Step);
            if (quest is null) continue;
            // A checklist the user wrote counts its own lines, which did not change.
            if (!string.IsNullOrWhiteSpace(resolve(progress.Flag, progress.Step).Steps)) continue;

            // Ticks from before the kill lines map straight onto today's draft.
            IReadOnlyList<int> now = beforeKillSteps
                ? QuestTextFormatter.CheckboxesSinceKillSteps(gameData, quest, itemSources, itemDrops)
                : QuestTextFormatter.CheckboxesSinceGatherSteps(gameData, quest, itemSources, itemDrops);
            List<int> placed = new();
            foreach (int tick in ticks)
            {
                // A number past the end of the old draft was not made against it; it is
                // kept as it is rather than guessed at.
                int target = tick >= 0 && tick < now.Count ? now[tick] : tick;
                if (target >= 0 && !placed.Contains(target)) placed.Add(target);
            }
            if (placed.SequenceEqual(ticks)) continue;
            progress.CheckedSteps = placed.Count == 0 ? null : placed;
            moved++;
        }

        profile.QuestTicksPredateKillSteps = false;
        profile.QuestDraftRevision = CurrentDraftRevision;
        return true;
    }
}

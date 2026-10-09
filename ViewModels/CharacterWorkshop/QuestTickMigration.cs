using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Quests;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.ViewModels.CharacterWorkshop;

// Moves a character's saved quest-step ticks onto the steps they were made on, once, after
// the drafted checklists gained their kill lines. A tick is kept as the number of its
// checkbox, and a kill line drafted ahead of a ticked step pushes that step down one; left
// alone, every later tick would sit one step too early and the last would fall off.
//
// The profile's schema upgrade marks a profile that holds ticks
// (CharacterProfile.QuestTicksPredateKillSteps); this clears the mark when it has run, so it
// runs once per character and a second call does nothing.
internal static class QuestTickMigration
{
    // resolve gives a quest's definition by flag and step. True when the ticks were gone
    // through and the mark cleared, so the caller saves the profile.
    public static bool Apply(CharacterProfile profile, GameDataCache gameData,
        System.Func<int, int, QuestDefinition> resolve)
    {
        System.ArgumentNullException.ThrowIfNull(profile);
        System.ArgumentNullException.ThrowIfNull(gameData);
        System.ArgumentNullException.ThrowIfNull(resolve);
        if (!profile.QuestTicksPredateKillSteps) return false;

        // Where the new lines fall is read from the game data; with no set loaded there
        // is no draft to place the ticks on, so the mark stays for the next time.
        IReadOnlyList<CrawledQuest> crawled = QuestCrawler.Crawl(gameData, null);
        if (crawled.Count == 0) return false;

        foreach (QuestProgress progress in profile.QuestLog ?? Enumerable.Empty<QuestProgress>())
        {
            if (progress.CheckedSteps is not { Count: > 0 } ticks) continue;
            CrawledQuest? quest = crawled.FirstOrDefault(q => q.Flag == progress.Flag && q.Step == progress.Step);
            if (quest is null) continue;
            // A checklist the user wrote counts its own lines, which did not change.
            if (!string.IsNullOrWhiteSpace(resolve(progress.Flag, progress.Step).Steps)) continue;

            IReadOnlyList<int> now = QuestTextFormatter.CheckboxesSinceKillSteps(gameData, quest);
            List<int> moved = new();
            foreach (int tick in ticks)
            {
                // A number past the end of the old draft was not made against it; it is
                // kept as it is rather than guessed at.
                int target = tick >= 0 && tick < now.Count ? now[tick] : tick;
                if (target >= 0 && !moved.Contains(target)) moved.Add(target);
            }
            if (!moved.SequenceEqual(ticks)) progress.CheckedSteps = moved.Count == 0 ? null : moved;
        }

        profile.QuestTicksPredateKillSteps = false;
        return true;
    }
}

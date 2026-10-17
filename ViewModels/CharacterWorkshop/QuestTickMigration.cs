using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Quests;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.ViewModels.CharacterWorkshop;

// Moves a character's saved quest-step ticks onto the steps they were made on, once each
// time the drafted checklists changed shape: first their kill lines, then their gather
// lines (fetching an item a later step needs) and each tier ending with its reward
// hand-in, which moves that line onto the tier before. A tick is kept as the number of
// its checkbox on its tier's card, and a line drafted ahead of a ticked step pushes that
// step down; left alone, every later tick would sit too early and the last would fall
// off, and a moved hand-in's tick would land on another step.
//
// The profile's schema upgrade marks a profile whose ticks predate the kill lines
// (CharacterProfile.QuestTicksPredateKillSteps); CharacterProfile.QuestDraftRevision
// records the draft the ticks were last made against. Both are brought up to date when
// this has run, so it runs once per character per change and a second call does nothing.
internal static class QuestTickMigration
{
    // The drafted checklists' current shape: 1 = with gather lines, tiers ending on
    // their reward hand-in.
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
        // Mid set switch the quest overlay reloads (and rebuilds the tab) before the drop
        // index has read the new set, so a draft then would number the gather lines off
        // the old set's drops; the marks stay until both are on the same set.
        if (itemDrops is not null && !string.Equals(itemDrops.ActiveSet, gameData.ActiveSet, System.StringComparison.Ordinal))
            return false;

        List<QuestProgress> log = profile.QuestLog ?? new List<QuestProgress>();
        // A checklist the user wrote counts its own lines, which did not change.
        bool Drafted(int flag, int step) => string.IsNullOrWhiteSpace(resolve(flag, step).Steps);

        foreach (int flag in log.Where(p => p.CheckedSteps is { Count: > 0 }).Select(p => p.Flag).Distinct().ToList())
        {
            List<CrawledQuest> tiers = crawled.Where(q => q.Flag == flag).ToList();
            if (tiers.Count == 0) continue;

            // Ticks from before the kill lines map straight onto today's draft.
            IReadOnlyDictionary<(int Step, int Index), (int Step, int Index)> moves =
                QuestTextFormatter.CheckboxMoves(gameData, flag, beforeKillSteps, itemSources, itemDrops);
            var placed = new Dictionary<int, List<int>>();
            var touched = new HashSet<int>();
            foreach (QuestProgress progress in log.Where(p => p.Flag == flag && p.CheckedSteps is { Count: > 0 }).ToList())
            {
                if (!tiers.Exists(t => t.Step == progress.Step) || !Drafted(flag, progress.Step)) continue;
                touched.Add(progress.Step);
                int oldCount = moves.Keys.Count(k => k.Step == progress.Step);
                foreach (int tick in progress.CheckedSteps!)
                {
                    (int Step, int Index) target;
                    if (moves.TryGetValue((progress.Step, tick), out target)) { }
                    // A number past the end of the old draft was not made against it; it is
                    // kept as it is rather than guessed at.
                    else if (tick >= oldCount) target = (progress.Step, tick);
                    else continue;
                    if (!Drafted(flag, target.Step)) continue;
                    List<int> list = placed.TryGetValue(target.Step, out List<int>? l) ? l : placed[target.Step] = new List<int>();
                    if (!list.Contains(target.Index)) list.Add(target.Index);
                    touched.Add(target.Step);
                }
            }

            foreach (int step in touched)
            {
                QuestProgress? progress = log.FirstOrDefault(p => p.Flag == flag && p.Step == step);
                List<int>? ticks = placed.TryGetValue(step, out List<int>? l) ? l : null;
                if (progress is null)
                {
                    if (ticks is null) continue;
                    log.Add(progress = new QuestProgress(flag, step));
                }
                List<int> before = progress.CheckedSteps ?? new List<int>();
                List<int> after = ticks ?? new List<int>();
                if (before.SequenceEqual(after)) continue;
                progress.CheckedSteps = after.Count == 0 ? null : after;
                moved++;
            }
        }
        if (log.Count > 0) profile.QuestLog = log;

        profile.QuestTicksPredateKillSteps = false;
        profile.QuestDraftRevision = CurrentDraftRevision;
        return true;
    }
}

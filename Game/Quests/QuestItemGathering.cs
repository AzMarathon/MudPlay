using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MudPlay.Services;

namespace MudPlay.Game.Quests;

// Where to get an item a drafted quest step turns in or asks for, when no earlier step
// of the quest hands it over: a hand-written guide lists "kill the metallic monstrosity
// (gleaming shard)" ahead of "ask martok forge (gleaming shard required)", and the draft
// would otherwise name the shard with nowhere to fetch it. Each such item gets one
// gather step, ahead of the first step that needs it, drawn from the set's own records
// in this order: an NPC or room that hands it over through a textblock (ItemSourceIndex),
// a room command that summons a monster which always drops it, the monsters whose drop
// slots carry it (MonsterDropIndex), then a chest that can hold it. An item none of them
// yields is left as the step's own note.
internal static class QuestItemGathering
{
    // Monsters named on one kill line; a common drop can come off dozens.
    private const int MaxDroppers = 3;

    // The checklist a quest's steps (QuestStepGraph.Build, in its order) draft to. On the
    // give-step axis one step stands for each give-step — the first, the same chain echoed
    // from many rooms folding into it — while the value axis keeps every step. A gather
    // line from the quest's own data stays only when a step kept at its place takes or
    // checks for what it hands over, nothing kept earlier already did, and no gather kept
    // there yields the same: so a lost-item reissue (`failitem 622:giveitem 622`), a gather
    // for a step that folded away, and the other wordings or sources of one fetch drop
    // out. Then the items still unaccounted for get their gather lines from the indexes.
    public static IReadOnlyList<QuestStep> Draft(
        IReadOnlyList<QuestStep> steps, bool byAbilityValue, ItemSourceIndex? givers, MonsterDropIndex? drops)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var seenOrders = new HashSet<int>();
        List<QuestStep> kept = steps
            .Where(s => s.Gathers || byAbilityValue || seenOrders.Add(s.Order))
            .ToList();

        var drafted = new List<QuestStep>(kept.Count);
        for (int i = 0; i < kept.Count; i++)
        {
            QuestStep step = kept[i];
            if (step.Gathers
                && (!kept.Any(s => !s.Gathers && s.Order == step.Order && Needs(s, step.GrantedItems))
                    || drafted.Any(s => s.GrantedItems.Intersect(step.GrantedItems).Any())))
                continue;
            drafted.Add(step);
        }
        return WithSources(drafted, givers, drops);
    }

    private static bool Needs(QuestStep step, IReadOnlyList<int> items) =>
        items.Any(i => step.TurnInItems.Contains(i) || step.RequiredItems.Contains(i));

    private static IReadOnlyList<QuestStep> WithSources(
        IReadOnlyList<QuestStep> steps, ItemSourceIndex? givers, MonsterDropIndex? drops)
    {
        if (givers is null && drops is null) return steps;

        var result = new List<QuestStep>(steps.Count);
        var gathered = new HashSet<int>();
        for (int i = 0; i < steps.Count; i++)
        {
            QuestStep step = steps[i];
            if (!step.Gathers)
                foreach (int item in step.TurnInItems.Concat(step.RequiredItems).Distinct())
                {
                    if (!gathered.Add(item) || YieldedBefore(steps, i, item)) continue;
                    if (SourceStep(step.Order, item, givers, drops) is { } gather) result.Add(gather);
                }
            result.Add(step);
        }
        return result;
    }

    private static bool YieldedBefore(IReadOnlyList<QuestStep> steps, int index, int item)
    {
        for (int i = 0; i < index; i++)
            if (steps[i].GrantedItems.Contains(item)) return true;
        return false;
    }

    private static QuestStep? SourceStep(int order, int item, ItemSourceIndex? givers, MonsterDropIndex? drops)
    {
        int[] granted = { item };
        if (givers is not null)
        {
            foreach (ItemGiver g in givers.GiversOf(item))
            {
                // A give reached from a greeting keyword has nothing to type.
                if (g.Keyword.Length == 0 || QuestStepGraph.IsAutoShownKeyword(g.Keyword)) continue;
                // "quest reward" says only that the line also sets a flag.
                string? note = g.Requirement.Length > 0 && g.Requirement != "quest reward" ? g.Requirement : null;
                if (g.Kind == ItemGiverKind.Monster)
                    return new QuestStep(order, $"ask {g.Name.ToLowerInvariant()} {g.Keyword}",
                        string.Create(CultureInfo.InvariantCulture, $"Monster #{g.Number}"),
                        Array.Empty<int>(), Array.Empty<int>(), granted, Gathers: true, Note: note);
                if (g.Map > 0 && g.Room > 0)
                    return new QuestStep(order, g.Keyword,
                        string.Create(CultureInfo.InvariantCulture, $"Room {g.Map}/{g.Room}"),
                        Array.Empty<int>(), Array.Empty<int>(), granted, Gathers: true, Note: note);
            }

            if (givers.SummonDropsOf(item) is { Count: > 0 } summons)
            {
                SummonDropSource s = summons[0];
                return new QuestStep(order, s.Command,
                    string.Create(CultureInfo.InvariantCulture, $"Room {s.Map}/{s.Room}"),
                    Array.Empty<int>(), Array.Empty<int>(), granted, Gathers: true, Droppers: new[] { s.MonsterId });
            }
        }

        if (drops?.DroppersOf(item) is { Count: > 0 } droppers)
        {
            int[] best = droppers
                .OrderByDescending(d => d.DropPercent)
                .Select(d => d.MonsterId)
                .Distinct()
                .Take(MaxDroppers)
                .ToArray();
            return new QuestStep(order, null, null, Array.Empty<int>(), Array.Empty<int>(), granted,
                Gathers: true, Droppers: best);
        }

        if (givers?.ContainersOf(item) is { Count: > 0 } chests)
            return new QuestStep(order, null, null, Array.Empty<int>(), Array.Empty<int>(), granted,
                Gathers: true, Note: "from " + chests[0].ContainerName);

        return null;
    }
}

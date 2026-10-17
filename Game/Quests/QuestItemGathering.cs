using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MudPlay.Services;

namespace MudPlay.Game.Quests;

// Where to get an item a drafted quest step turns in or asks for, when no earlier step
// of the quest hands it over: a hand-written guide lists "kill the metallic monstrosity
// (gleaming shard)" ahead of "ask martok forge (gleaming shard required)", and the draft
// would otherwise name the shard with nowhere to fetch it. Each such item gets one
// gather step, ahead of the first step that needs it, drawn from the set's own records,
// sure sources first: a room command that summons a monster which always drops it, the
// monsters whose drop slot holds it at 100 (MonsterDropIndex), an NPC or room that hands
// it over through a textblock (ItemSourceIndex), any other monster that drops it, then a
// chest that can hold it. An item none of them yields is left as the step's own note.
// A drop off a monster an earlier line already kills is noted on that line instead.
internal static class QuestItemGathering
{
    // Monsters named on one kill line; a common drop can come off dozens.
    private const int MaxDroppers = 3;

    // How many fetches deep a chain of trades is followed (a fetch for a fetch).
    private const int MaxFetchDepth = 3;

    // A drop slot at this chance always drops (MonsterDropIndex: 0 is unset).
    private const int SureDropPercent = 100;

    // The checklist a quest's steps (QuestStepGraph.Build, in its order) draft to. On the
    // give-step axis one step stands for each give-step — the first, the same chain echoed
    // from many rooms folding into it — while the value axis keeps every step. A gather
    // line from the quest's own data stays only when a step kept at its place takes or
    // checks for what it hands over, nothing kept earlier already did, and no gather kept
    // there yields the same: so a lost-item reissue (`failitem 622:giveitem 622`), a gather
    // for a step that folded away, and the other wordings or sources of one fetch drop
    // out. Then the items still unaccounted for get their gather lines from the indexes.
    public static IReadOnlyList<QuestStep> Draft(GameDataCache cache,
        IReadOnlyList<QuestStep> steps, bool byAbilityValue, ItemSourceIndex? givers, MonsterDropIndex? drops)
    {
        ArgumentNullException.ThrowIfNull(cache);
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
        return WithSources(cache, drafted, givers, drops);
    }

    private static bool Needs(QuestStep step, IReadOnlyList<int> items) =>
        items.Any(i => step.TurnInItems.Contains(i) || step.RequiredItems.Contains(i));

    private static IReadOnlyList<QuestStep> WithSources(GameDataCache cache,
        IReadOnlyList<QuestStep> steps, ItemSourceIndex? givers, MonsterDropIndex? drops)
    {
        if (givers is null && drops is null) return steps;

        var result = new List<QuestStep>(steps.Count);
        var gathered = new HashSet<int>();

        // The fetches for what step needs go in ahead of it, and a fetch that needs
        // something itself (an NPC who trades the item for another) gets its own fetch
        // ahead of it in turn, as a guide lists "obtain 50 orc-heads" before "ask aldreth
        // heads".
        void AddFetches(QuestStep step, int depth)
        {
            foreach (int item in step.TurnInItems.Concat(step.RequiredItems).Distinct())
            {
                if (!gathered.Add(item) || result.Any(s => s.GrantedItems.Contains(item))) continue;
                if (SourceStep(step.Order, item, givers, drops) is not { } gather) continue;

                // The monster is killed on an earlier line anyway (the quest's own
                // kill, or the fetch of another of its drops): the item comes with it.
                int killer = gather.Command is null && gather.Droppers is { } droppers
                    ? result.FindIndex(s => Kills(cache, s).Intersect(droppers).Any())
                    : -1;
                if (killer >= 0)
                {
                    result[killer] = result[killer] with
                    {
                        GrantedItems = result[killer].GrantedItems.Append(item).ToArray(),
                    };
                    continue;
                }
                if (depth < MaxFetchDepth) AddFetches(gather, depth + 1);
                result.Add(gather);
            }
        }

        foreach (QuestStep step in steps)
        {
            AddFetches(step, 0);
            result.Add(step);
        }
        return result;
    }

    // The monsters a drafted line has the player kill: a drop fetch's droppers, or the
    // monsters whose death runs a command-less step.
    private static IEnumerable<int> Kills(GameDataCache cache, QuestStep s) =>
        s.Command is not null ? Enumerable.Empty<int>()
        : s.Droppers is { Count: > 0 } droppers ? droppers
        : QuestDeathSpells.KilledAt(cache, s.Location);

    private static QuestStep? SourceStep(int order, int item, ItemSourceIndex? givers, MonsterDropIndex? drops)
    {
        int[] granted = { item };

        if (givers?.SummonDropsOf(item) is { Count: > 0 } summons)
        {
            SummonDropSource s = summons[0];
            return new QuestStep(order, s.Command,
                string.Create(CultureInfo.InvariantCulture, $"Room {s.Map}/{s.Room}"),
                Array.Empty<int>(), Array.Empty<int>(), granted, Gathers: true, Droppers: new[] { s.MonsterId });
        }

        IReadOnlyList<MonsterDropIndex.MonsterDrop> droppers =
            drops?.DroppersOf(item) ?? Array.Empty<MonsterDropIndex.MonsterDrop>();
        if (DropStep(order, granted, droppers.Where(d => d.DropPercent >= SureDropPercent)) is { } sure)
            return sure;

        if (givers is not null)
        {
            // A line that also sets a flag ("quest reward") is another quest's step,
            // gated on that quest: Stock's Good 1 box give refuses an evil character, who
            // takes the box off Commander Markus instead.
            List<ItemGiver> usable = givers.GiversOf(item).Where(g => g.Requirement != "quest reward").ToList();
            foreach (ItemGiver g in usable.Where(g => IsTyped(g.Keyword)))
            {
                if (g.Kind == ItemGiverKind.Monster)
                    return GiverStep(order, granted, g, $"ask {g.Name.ToLowerInvariant()} {g.Keyword}",
                        string.Create(CultureInfo.InvariantCulture, $"Monster #{g.Number}"));
                if (g.Map > 0 && g.Room > 0)
                    return GiverStep(order, granted, g, g.Keyword,
                        string.Create(CultureInfo.InvariantCulture, $"Room {g.Map}/{g.Room}"));
            }
            // A room that gives it with nothing to type for it (a draw of a room's roll
            // table): where to go, not what to type.
            foreach (ItemGiver g in usable.Where(g => g.Kind == ItemGiverKind.Room && g.Map > 0 && g.Room > 0))
                return GiverStep(order, granted, g, null,
                    string.Create(CultureInfo.InvariantCulture, $"Room {g.Map}/{g.Room}"));
        }

        if (DropStep(order, granted, droppers) is { } drop) return drop;

        if (givers?.ContainersOf(item) is { Count: > 0 } chests)
            return new QuestStep(order, null, null, Array.Empty<int>(), Array.Empty<int>(), granted,
                Gathers: true, Note: "from " + chests[0].ContainerName);

        return null;
    }

    // A give from an NPC or room: the item it takes is needed first (and fetched in turn);
    // a purchase is noted, since what it costs isn't an item.
    private static QuestStep GiverStep(int order, int[] granted, ItemGiver g, string? command, string location) =>
        new(order, command, location, Array.Empty<int>(),
            g.TakenItemId > 0 ? new[] { g.TakenItemId } : Array.Empty<int>(), granted, Gathers: true,
            Note: g.TakenItemId == 0 && g.Requirement.Length > 0 ? g.Requirement : null);

    // A keyword the player types to get the give: not one an NPC shows by itself, and
    // not a roll-table band.
    private static bool IsTyped(string keyword) =>
        keyword.Length > 0 && !QuestStepGraph.IsAutoShownKeyword(keyword)
        && !int.TryParse(keyword, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

    private static QuestStep? DropStep(int order, int[] granted, IEnumerable<MonsterDropIndex.MonsterDrop> droppers)
    {
        int[] best = droppers
            .OrderByDescending(d => d.DropPercent)
            .Select(d => d.MonsterId)
            .Distinct()
            .Take(MaxDroppers)
            .ToArray();
        return best.Length == 0 ? null
            : new QuestStep(order, null, null, Array.Empty<int>(), Array.Empty<int>(), granted,
                Gathers: true, Droppers: best);
    }
}

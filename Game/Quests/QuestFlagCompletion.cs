using System.Collections.Generic;

namespace MudPlay.Game.Quests;

// The pure decision at the heart of quest-flag sync: given each quest's effective
// "complete value", the character's observed flag values, and which quests are already
// complete, which quests should be NEWLY marked done. Strictly one-way — it never returns
// a quest to clear, so a flag reading below its complete value (or an undetectable quest)
// simply leaves the quest as it is. The worst failure is a missed completion the user ticks
// by hand, never a wrongly-cleared quest.
public static class QuestFlagCompletion
{
    // A crawled quest (or band) keyed by (Flag, Step) with its effective complete value —
    // the per-quest override if the user set one, else the crawl's derived value; null when
    // undetectable (tops out at 0 / no absolute grant).
    public readonly record struct Target(int Flag, int Step, int? CompleteValue);

    public readonly record struct QuestKey(int Flag, int Step);

    // The (Flag, Step) of quests whose flag has reached its complete value and that aren't
    // already complete. An undetectable quest (null / ≤ 0 complete value) is skipped.
    public static IReadOnlyList<QuestKey> ResolveNewlyComplete(
        IEnumerable<Target> targets,
        IReadOnlyDictionary<int, int> observed,
        ISet<QuestKey> alreadyComplete)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(alreadyComplete);

        var result = new List<QuestKey>();
        foreach (Target t in targets)
        {
            if (t.CompleteValue is not int complete || complete <= 0) continue; // undetectable
            var key = new QuestKey(t.Flag, t.Step);
            if (alreadyComplete.Contains(key)) continue;                        // never re-mark
            if (observed.TryGetValue(t.Flag, out int value) && value >= complete)
                result.Add(key);
        }
        return result;
    }

    // The distinct flags worth querying on the per-flag (paradigm) path: every detectable,
    // not-yet-complete target's flag. Bounds the burst of `abil` sends to what could
    // actually change — a flag with no incomplete detectable quest is never asked about.
    public static IReadOnlyList<int> FlagsToQuery(
        IEnumerable<Target> targets, ISet<QuestKey> alreadyComplete)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(alreadyComplete);

        var flags = new List<int>();
        var seen = new HashSet<int>();
        foreach (Target t in targets)
        {
            if (t.CompleteValue is not int complete || complete <= 0) continue;
            if (alreadyComplete.Contains(new QuestKey(t.Flag, t.Step))) continue;
            if (seen.Add(t.Flag)) flags.Add(t.Flag);
        }
        return flags;
    }
}

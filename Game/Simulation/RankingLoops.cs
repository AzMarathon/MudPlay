using MudPlay.Game.Map;

namespace MudPlay.Game.Simulation;

// The user's own loops in a kept ranking, as against those loops now. A ★ row is
// the simulation of a loop as it was when the ranking ran; the loop edited, deleted
// or renamed since, the row is the figure of a loop that is no longer there. Each
// ranked loop is recorded by its name and its LoopSignature, which is its waypoints
// and the two loop-wide settings of the character the ranking was made for.
//
// It is kept apart from RankingCharacter: the loops are the set's, shared by every
// character on it, and the check names the loops that changed, which a hash of the
// lot could not. Only the loops ranked count. One saved since is not in the ranking
// and marks nothing: saving a tour as a loop from a row would otherwise mark the
// ranking it was saved from.
public static class RankingLoops
{
    // How the group is named in a ranking's note: "your loops have changed since".
    public const string Group = "loops";

    // The signature of each ranked loop as the catalogue holds it now, by name; a
    // loop no longer there is left out.
    public static IReadOnlyDictionary<string, string> Now(IEnumerable<AreaRank> rows, IEnumerable<Loop> catalogue)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(catalogue);
        var ranked = new HashSet<string>(rows.Where(r => r.IsLoop).Select(r => r.Area), StringComparer.OrdinalIgnoreCase);
        var now = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ranked.Count == 0) return now;
        foreach (Loop loop in catalogue)
            if (ranked.Contains(loop.Name)) now.TryAdd(loop.Name, LoopSignature.Of(loop));
        return now;
    }

    // The ranked loops that are not now what they were when made was taken, by
    // name, in name order: edited, run with other settings, gone, or with no record
    // of what they were (in doubt, changed).
    public static IReadOnlyList<string> ChangedSince(
        IEnumerable<AreaRank> rows, IReadOnlyDictionary<string, string>? made, IReadOnlyDictionary<string, string> now)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(now);
        return rows.Where(r => r.IsLoop).Select(r => r.Area)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => made is null || !made.TryGetValue(name, out string? then)
                           || !now.TryGetValue(name, out string? current)
                           || !string.Equals(then, current, StringComparison.Ordinal))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

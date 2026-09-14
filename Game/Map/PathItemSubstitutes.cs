namespace MudPlay.Game.Map;

// Which items stand in for a path item on the current route. A hazard's
// protection is an any-of group — the river takes a log raft, wooden skiff,
// silverbark canoe or river punt — but the route picker and the walker resolve
// that group to ONE representative item (the cheapest reachable), and the
// acquisition pipeline then tracks that id. Counting only the representative
// buys a raft for a crosser who already carries a canoe, and asks the party
// about rafts when a member holds a spare canoe.
//
// Substitutes are scoped to the route, not global. The same item can sit in
// several groups with different members: a log raft protects on the river
// (four boats) and on Crystal Lake (raft or skiff only). A route that crosses
// both needs a boat good for both, so the substitutes for the raft are the
// intersection of every group it's announced for — raft or skiff — and a canoe
// correctly does not count.
//
// Recording is two-phase because the walker announces a route room by room:
// Record stages each hazard room's group during the pass, and Commit swaps the
// staged set in when the pass hands its item list on. A later route that
// crosses no hazard therefore drops the old substitutes (an unrelated
// "(Item: 691)" exit must not accept a canoe), while an item still being
// obtained keeps its entry across a detour's own announce — the walk to the
// shop doesn't cross the river, but the canoe still covers the raft.
public sealed class PathItemSubstitutes
{
    private readonly Dictionary<int, HashSet<int>> _active = new();
    private readonly Dictionary<int, HashSet<int>> _staged = new();

    public void Clear()
    {
        _active.Clear();
        _staged.Clear();
    }

    // Narrow itemId's staged substitutes to those also in group (a hazard room on
    // the route being announced whose any-of counter set contains itemId). A
    // group that doesn't contain itemId says nothing about it and is ignored.
    public void Record(int itemId, IReadOnlyList<int> group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (itemId <= 0 || !group.Contains(itemId)) return;
        if (_staged.TryGetValue(itemId, out HashSet<int>? subs))
            subs.IntersectWith(group);
        else
            _staged[itemId] = new HashSet<int>(group.Where(static id => id > 0));
    }

    // End of an announce pass: the staged entries become the route's substitutes.
    // An active entry the pass didn't restage survives only while keep says the
    // item is still wanted.
    public void Commit(Func<int, bool> keep)
    {
        ArgumentNullException.ThrowIfNull(keep);
        foreach (int id in _active.Keys.ToArray())
            if (!_staged.ContainsKey(id) && !keep(id)) _active.Remove(id);
        foreach (KeyValuePair<int, HashSet<int>> kv in _staged) _active[kv.Key] = kv.Value;
        _staged.Clear();
    }

    // Every item that satisfies itemId on this route, itemId first. An item with
    // nothing recorded (an Item/Ticket exit gate, a single-counter hazard) has
    // only itself.
    public IReadOnlyList<int> For(int itemId)
    {
        if (!_active.TryGetValue(itemId, out HashSet<int>? subs) || subs.Count <= 1)
            return new[] { itemId };
        var ordered = new List<int>(subs.Count) { itemId };
        foreach (int id in subs.OrderBy(static id => id))
            if (id != itemId) ordered.Add(id);
        return ordered;
    }

    // Copies carried that satisfy itemId — its own count plus every substitute's.
    public int Coverage(int itemId, Func<int, int> carriedCount)
    {
        ArgumentNullException.ThrowIfNull(carriedCount);
        int total = 0;
        foreach (int id in For(itemId)) total += Math.Max(0, carriedCount(id));
        return total;
    }
}

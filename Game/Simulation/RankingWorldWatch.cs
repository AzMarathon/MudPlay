namespace MudPlay.Game.Simulation;

// Which game data this client is running on, for a ranking kept between sessions
// (RankingWorld): the table files as they were when the client read them, not as
// they are on disk now. Another client can import the set while this one runs; this
// one goes on with the rows it read, a ranking it makes is a ranking of those, and
// stamped with the new files it would be trusted after a restart.
//
// A table is stamped with the file it was read from in this load of the set, which
// the cache knows (stampAsRead; null when it has not read the table in this load).
// One it has not read is stamped with its file as it is now (stampOnDisk; null with
// no file): nothing in memory came from an older one, and that file is what any
// lookup from here on reads. A set without the table (Lairs is optional in an
// export) is stamped as without it.
//
// The cache drops a table nobody has read for a while and reads it again when asked,
// and then knows only the later file. Had that file changed in between, the room map
// and catalogs built at load are from one version and the rows looked up since from
// another, and no stamp says what a ranking would be made on. So the first stamp
// seen for a table in a load is remembered here, and a later one that differs makes
// the whole load unfit: no world is given, and so no ranking is saved or read back,
// until the set is read in again (a restart, or an import in this client). Observe
// is called on the heartbeat, far more often than the cache can drop a table and
// read it again, so the first read is the one remembered.
public sealed class RankingWorldWatch(Func<string, string?> stampAsRead, Func<string, string?> stampOnDisk)
{
    // The stamp of a table the set does not have.
    public const string NoTable = "none";

    // A table file's stamp: its length and last-write time. The tables run to tens
    // of megabytes and an import rewrites them, so the pair tells two apart without
    // reading either. Both sources hand theirs in through this, so a table stamped
    // from disk and then read from that same file is one stamp.
    public static string Stamp(long length, DateTime lastWriteUtc) => $"{length}:{lastWriteUtc.Ticks}";

    private readonly object _seenLock = new();
    private readonly Dictionary<string, string> _first = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _changed = new(StringComparer.Ordinal);
    private object? _load;

    // The tables read again in this load from a file that was not the one first
    // read, in name order; empty is the usual case.
    public IReadOnlyList<string> Changed
    {
        get { lock (_seenLock) return _changed.ToArray(); }
    }

    // Take in what the cache has read by now. load says which load of which set is
    // in hand: two that are equal are the same load, and anything remembered of
    // another is forgotten.
    public void Observe(object? load)
    {
        lock (_seenLock) See(load);
    }

    // The world a ranking made in this load is made in, or null when the load has no
    // one world, with why in words for the program log.
    public RankingWorld? Now(object? load, string seed, out string? why)
    {
        ArgumentNullException.ThrowIfNull(seed);
        lock (_seenLock)
        {
            See(load);
            foreach (string table in RankingWorld.TableNames)
                if (!_first.ContainsKey(table)) _first[table] = stampOnDisk(table) ?? NoTable;
            if (_changed.Count > 0)
            {
                why = $"{string.Join(", ", _changed)} {(_changed.Count == 1 ? "was" : "were")} read again from a file " +
                      "that had changed since the set was loaded";
                return null;
            }
            why = null;
            return new RankingWorld(LoopSimulator.SimulatorVersion,
                RankingWorld.TableNames.ToDictionary(t => t, t => _first[t], StringComparer.Ordinal), seed);
        }
    }

    private void See(object? load)
    {
        if (!Equals(load, _load))
        {
            _load = load;
            _first.Clear();
            _changed.Clear();
        }
        foreach (string table in RankingWorld.TableNames)
        {
            if (stampAsRead(table) is not { } stamp) continue;
            if (!_first.TryGetValue(table, out string? first)) _first[table] = stamp;
            else if (!string.Equals(first, stamp, StringComparison.Ordinal)) _changed.Add(table);
        }
    }
}

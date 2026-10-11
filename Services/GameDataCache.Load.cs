namespace MudPlay.Services;

public sealed partial class GameDataCache
{
    // One load of one set: the set's name and a count that moves every time its
    // tables are dropped for re-reading (a switch, a reload, a set imported again
    // over itself). The name alone does not change on the last two, so whatever is
    // built from a set's tables and kept is kept under this, not under the name:
    // GameDataCache.ActiveLoad is the one the cache is serving.
    public readonly record struct Load(string? Set, long Generation)
    {
        // No load the cache ever serves (its count starts at 0 and only rises): what
        // a store that reloads with the set holds while it is between two loads.
        public static readonly Load None = new(null, -1);
    }
}

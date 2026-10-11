namespace MudPlay.Game.Simulation;

// The area rankings finished while the client has been running, each kept for the
// game-data set and character it was made on (SimulatorOwner). The Simulator shows
// the active pairing's own and no other's: switching game data or character puts
// that pairing's ranking on show when it has one, and switching back brings the
// first one back without ranking again.
//
// In memory only, so a ranking never outlives the client: nothing here can tell
// that the character has levelled or re-geared, or that the game data was edited,
// since the ranking was made. What it is told is when a set's data is replaced, and
// every ranking made on that set then goes, whether or not the set is the active one
// and whether or not a Simulator is open: the active set read in again under the
// same name (NoteActiveSet), and a set imported again, deleted, or copied or moved
// into or out of in Manage Sets (ForgetSet).
//
// Bounded: a ranking is a few hundred rows, each with its tour's rooms and its
// runs' figures (about a megabyte for a whole realm). Only the MaxKept most
// recently made or shown are held; an older one is ranked again when wanted.
//
// Used from the UI thread only, as the Simulator's view-model is.
public sealed class AreaRankingStore
{
    public const int MaxKept = 8;

    // Least recently made or shown first.
    private readonly List<(SimulatorOwner Owner, AreaRanking Ranking)> _kept = new();

    public int Count => _kept.Count;

    // The ranking made for owner, or null. Asking counts as showing it.
    public AreaRanking? Get(SimulatorOwner owner)
    {
        int at = _kept.FindIndex(k => k.Owner == owner);
        if (at < 0) return null;
        (SimulatorOwner, AreaRanking Ranking) found = _kept[at];
        _kept.RemoveAt(at);
        _kept.Add(found);
        return found.Ranking;
    }

    // Keep owner's ranking in place of any it had. Returns the pairing whose ranking
    // was dropped to stay within MaxKept, or null.
    public SimulatorOwner? Keep(SimulatorOwner owner, AreaRanking ranking)
    {
        ArgumentNullException.ThrowIfNull(ranking);
        _kept.RemoveAll(k => k.Owner == owner);
        _kept.Add((owner, ranking));
        if (_kept.Count <= MaxKept) return null;
        SimulatorOwner dropped = _kept[0].Owner;
        _kept.RemoveAt(0);
        return dropped;
    }

    public void Forget(SimulatorOwner owner) => _kept.RemoveAll(k => k.Owner == owner);

    // The loaded character is now known as to (it was renamed or moved to another
    // BBS, or the default profile was given a name): its rankings on every set go
    // with it. Any kept under the new name were another character's, which that name
    // no longer is, and are dropped; nothing is left under the old name for the next
    // character to be given it. Returns how many were carried.
    public int RenameCharacter(string? from, string? to)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return 0;
        _kept.RemoveAll(k => string.Equals(k.Owner.Character, to, StringComparison.OrdinalIgnoreCase));
        int carried = 0;
        for (int i = 0; i < _kept.Count; i++)
        {
            if (!string.Equals(_kept[i].Owner.Character, from, StringComparison.OrdinalIgnoreCase)) continue;
            _kept[i] = (_kept[i].Owner with { Character = to }, _kept[i].Ranking);
            carried++;
        }
        return carried;
    }

    // The set last told active.
    private string? _activeSet;

    // The active game-data set is now set. Another name is a switch and drops
    // nothing. The same name again is that set read in afresh: its tables may
    // differ, so every character's ranking made on it goes. Returns how many went.
    public int NoteActiveSet(string? set)
    {
        bool readAgain = string.Equals(set, _activeSet, StringComparison.OrdinalIgnoreCase);
        _activeSet = set;
        return readAgain ? ForgetSet(set) : 0;
    }

    // The data of set was replaced (an import over it, its deletion, a copy or move
    // of the user's edits into or out of it): drop every character's ranking made on
    // it. Returns how many went.
    //
    // A Simulator that is listing one of them is not told from here. Each of those
    // changes, made to the active set, also reads the set in again or switches away
    // from it, and that is what takes the ranking off the list.
    public int ForgetSet(string? set) =>
        _kept.RemoveAll(k => string.Equals(k.Owner.Set, set, StringComparison.OrdinalIgnoreCase));
}

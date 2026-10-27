using System.IO;
using MudPlay.Game.Map;
using MudPlay.Models.Settings;
using MudPlay.Services;

namespace MudPlay.Game.Recovery;

// A Stock board whose sysop edited its rooms can still be played on the standard
// game data, and nothing in the data says so. What does is a death the client
// sees there that ends in a room the standard list doesn't allow for the room
// died in (ArrivalRoomLearner). This keeps the board's count of those, and says
// when the board is marked for it: the list is then read there as on any board
// with edited rooms (HangupArrivalListTrust.Unverified).
//
// One such death is not enough. The map can be sure of a room and wrong (a room
// in a grid of same-named rooms), and the death then looks like it ended in the
// wrong place on a board that is standard. Two are asked for.
//
// The count belongs to the board AND the data set it was made against
// (2026-10-11): it is kept on the realm with the set's name, and counts for
// nothing while the realm plays another set. So pointing the realm at another
// set (the board's own data, imported) starts afresh, and there is nothing to
// clear by hand.
public sealed class StandardListContradictions
{
    // How many deaths the standard list doesn't allow mark the board.
    public const int DeathsToMark = 2;

    private readonly Func<(BbsProfile Bbs, RealmProfile Realm)?> _active;
    private readonly Func<string?> _activeSet;
    private readonly Action<BbsProfile> _save;
    private readonly LogService? _log;
    // Counts the board's file couldn't take, by board, realm and set (Key). The
    // board is read afresh from its file on every ask, so a count that never
    // reached the file is held here for the session, or it would be gone at the
    // next read. A count is said once when it first fails, not on every retry.
    // Concurrent: a bug report may read it off the UI thread.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _unsaved =
        new(StringComparer.OrdinalIgnoreCase);
    // The board's file couldn't be read, and it has been said.
    private bool _readFailed;

    //   active    — the active board, read afresh, and the realm on it being
    //               played; null with none.
    //   activeSet — the game-data set in play.
    //   save      — writes that board's file.
    public StandardListContradictions(
        Func<(BbsProfile Bbs, RealmProfile Realm)?> active, Func<string?> activeSet, Action<BbsProfile> save,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(activeSet);
        ArgumentNullException.ThrowIfNull(save);
        _active = active;
        _activeSet = activeSet;
        _save = save;
        _log = log;
    }

    // From one read of the board: the deaths counted on it against the set in
    // play, whether they mark it (its rooms are taken to differ from the standard
    // set's), and whether the count is held for the session, not yet in its file.
    public (int Count, bool Marked, bool Unsaved) Standing()
    {
        if (_active() is not { } active || _activeSet() is not { Length: > 0 } set) return (0, false, false);
        int count = CountOn(active, set);
        return (count, count >= DeathsToMark, _unsaved.ContainsKey(Key(active, set)));
    }

    public bool Marked => Standing().Marked;

    private static string Key((BbsProfile Bbs, RealmProfile Realm) active, string set) =>
        $"{active.Bbs.Name}\n{active.Realm.Name}\n{set}";

    private int CountOn((BbsProfile Bbs, RealmProfile Realm) active, string set)
    {
        int onFile = string.Equals(active.Realm.DeathsContradictingStandardArrivalRoomsOn, set, StringComparison.OrdinalIgnoreCase)
            ? active.Realm.DeathsContradictingStandardArrivalRooms
            : 0;
        // Another client on the board may have counted since.
        return _unsaved.TryGetValue(Key(active, set), out int held) ? Math.Max(held, onFile) : onFile;
    }

    // A death seen in diedIn ended in arrivedIn, which the standard list doesn't
    // allow: its dead arrive in one of expected. With no board to count it on,
    // nothing changes. Called from inside the room display's handling, so
    // nothing here may throw.
    public void Note(RoomKey diedIn, RoomKey arrivedIn, IReadOnlyList<RoomKey> expected)
    {
        // One read of the board: the count goes onto the copy that is saved.
        if (_activeSet() is not { Length: > 0 } set || ReadActive() is not { } active) return;
        int count = CountOn(active, set) + 1;
        Write(active, set, count);

        string seen = $"A death seen in {diedIn} ended in {arrivedIn}, where the standard list has the dead of that room "
            + $"arrive in {string.Join(" or ", expected)}";
        if (count >= DeathsToMark)
            _log?.Warn(LearnedArrivalRoomStore.LogCategory,
                $"{seen}. That is {count} such deaths on this board: its rooms do not match the standard data set it is "
                + $"played on ('{set}'). From now on the list is taken here as on any board with edited rooms: it says where "
                + "the dead arrive, never that a room is the wrong one, and what is learned from deaths seen is used. If the "
                + "board has its own game data, import it and point the realm at it; that also starts this count afresh.");
        else
            _log?.Warn(LearnedArrivalRoomStore.LogCategory,
                $"{seen}. On the standard data set ('{set}') that means this board's rooms were edited, or the map had the "
                + $"wrong room for one end of it. Nothing changes yet: {DeathsToMark - count} more such death(s) and the list "
                + "is taken here as on a board with edited rooms.");
    }

    // A count the board's file couldn't take is tried again, while that board,
    // realm and set are the ones in play. Called when a character is loaded and
    // when the game is entered: not on a timer, as a write that keeps failing can
    // spend a good part of a second retrying, on the UI thread.
    public void RetryUnsaved()
    {
        if (_unsaved.IsEmpty || _activeSet() is not { Length: > 0 } set || ReadActive() is not { } active
            || !_unsaved.ContainsKey(Key(active, set)))
            return;
        int count = CountOn(active, set);
        Write(active, set, count);
        if (!_unsaved.ContainsKey(Key(active, set)))
            _log?.Info(LearnedArrivalRoomStore.LogCategory,
                $"The board's count of deaths the standard list doesn't allow ({count}) is written now.");
    }

    private (BbsProfile Bbs, RealmProfile Realm)? ReadActive()
    {
        try
        {
            (BbsProfile Bbs, RealmProfile Realm)? active = _active();
            _readFailed = false;
            return active;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (!_readFailed)
                _log?.Warn(LearnedArrivalRoomStore.LogCategory,
                    $"The board's file couldn't be read ({ex.Message}). A death the standard list doesn't allow isn't "
                    + "counted on it while it can't.");
            _readFailed = true;
            return null;
        }
    }

    // A count the file won't take is held for the session, and tried again with
    // the next death counted and on RetryUnsaved.
    private void Write((BbsProfile Bbs, RealmProfile Realm) active, string set, int count)
    {
        active.Realm.DeathsContradictingStandardArrivalRooms = count;
        active.Realm.DeathsContradictingStandardArrivalRoomsOn = set;
        string key = Key(active, set);
        try
        {
            _save(active.Bbs);
            _unsaved.TryRemove(key, out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_unsaved.ContainsKey(key))
                _log?.Warn(LearnedArrivalRoomStore.LogCategory,
                    $"The board's file couldn't be written ({ex.Message}). Its count of deaths the standard list doesn't "
                    + "allow is kept for this session, and tried again with the next one counted, when a character is "
                    + "loaded and when the game is entered.");
            _unsaved[key] = count;
        }
    }
}

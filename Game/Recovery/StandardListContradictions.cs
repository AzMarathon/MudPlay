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
    // The save failed, and it has been said.
    private bool _saveFailed;
    // A count the board's file couldn't take, with the board, realm and set it was
    // counted on. The board is read afresh from its file on every ask, so a count
    // that never reached the file is held here for the session, or it would be
    // gone at the next read.
    private (string Bbs, string Realm, string Set, int Count)? _unsaved;

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

    // The deaths counted on this board against the set in play.
    public int Count => _active() is { } active && _activeSet() is { Length: > 0 } set ? CountOn(active, set) : 0;

    // The board's rooms are taken to differ from the standard set's.
    public bool Marked => Count >= DeathsToMark;

    private int CountOn((BbsProfile Bbs, RealmProfile Realm) active, string set)
    {
        int onFile = string.Equals(active.Realm.DeathsContradictingStandardArrivalRoomsOn, set, StringComparison.OrdinalIgnoreCase)
            ? active.Realm.DeathsContradictingStandardArrivalRooms
            : 0;
        // Another client on the board may have counted since.
        return HeldFor(active, set) is int held ? Math.Max(held, onFile) : onFile;
    }

    private int? HeldFor((BbsProfile Bbs, RealmProfile Realm) active, string set) =>
        _unsaved is { } held
        && string.Equals(held.Bbs, active.Bbs.Name, StringComparison.OrdinalIgnoreCase)
        && string.Equals(held.Realm, active.Realm.Name, StringComparison.OrdinalIgnoreCase)
        && string.Equals(held.Set, set, StringComparison.OrdinalIgnoreCase)
            ? held.Count
            : null;

    // A death seen in diedIn ended in arrivedIn, which the standard list doesn't
    // allow: its dead arrive in one of expected. With no board to count it on,
    // nothing changes. Called from inside the room display's handling, so
    // nothing here may throw.
    public void Note(RoomKey diedIn, RoomKey arrivedIn, IReadOnlyList<RoomKey> expected)
    {
        // One read of the board: the count goes onto the copy that is saved.
        if (_active() is not { } active || _activeSet() is not { Length: > 0 } set) return;
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
    // realm and set are still the ones in play.
    public void RetryUnsaved()
    {
        if (_unsaved is null || _active() is not { } active || _activeSet() is not { Length: > 0 } set
            || HeldFor(active, set) is null)
            return;
        int count = CountOn(active, set);
        Write(active, set, count);
        if (_unsaved is null)
            _log?.Info(LearnedArrivalRoomStore.LogCategory,
                $"The board's count of deaths the standard list doesn't allow ({count}) is written now.");
    }

    // A count the file won't take is held for the session and tried again on
    // the heartbeat.
    private void Write((BbsProfile Bbs, RealmProfile Realm) active, string set, int count)
    {
        active.Realm.DeathsContradictingStandardArrivalRooms = count;
        active.Realm.DeathsContradictingStandardArrivalRoomsOn = set;
        try
        {
            _save(active.Bbs);
            if (HeldFor(active, set) is not null) _unsaved = null;
            _saveFailed = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _unsaved = (active.Bbs.Name, active.Realm.Name, set, count);
            if (!_saveFailed)
                _log?.Warn(LearnedArrivalRoomStore.LogCategory,
                    $"The board's file couldn't be written ({ex.Message}). Its count of deaths the standard list doesn't "
                    + "allow is kept for this session and tried again until it is written.");
            _saveFailed = true;
        }
    }
}

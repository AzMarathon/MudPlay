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

    private readonly Func<RealmProfile?> _realm;
    private readonly Func<string?> _activeSet;
    private readonly Action _save;
    private readonly LogService? _log;
    // The save failed, and it has been said.
    private bool _saveFailed;

    //   realm     — the active board's realm, or null with none.
    //   activeSet — the game-data set in play.
    //   save      — writes the board's file.
    public StandardListContradictions(Func<RealmProfile?> realm, Func<string?> activeSet, Action save, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(realm);
        ArgumentNullException.ThrowIfNull(activeSet);
        ArgumentNullException.ThrowIfNull(save);
        _realm = realm;
        _activeSet = activeSet;
        _save = save;
        _log = log;
    }

    // The deaths counted on this board against the set in play.
    public int Count => _realm() is { } realm && ForThisSet(realm) ? realm.DeathsContradictingStandardArrivalRooms : 0;

    // The board's rooms are taken to differ from the standard set's.
    public bool Marked => Count >= DeathsToMark;

    private bool ForThisSet(RealmProfile realm) =>
        _activeSet() is { Length: > 0 } set
        && string.Equals(realm.DeathsContradictingStandardArrivalRoomsOn, set, StringComparison.OrdinalIgnoreCase);

    // A death seen in diedIn ended in arrivedIn, which the standard list doesn't
    // allow: its dead arrive in one of expected. With no board to count it on,
    // nothing changes. Called from inside the room display's handling, so
    // nothing here may throw.
    public void Note(RoomKey diedIn, RoomKey arrivedIn, IReadOnlyList<RoomKey> expected)
    {
        if (_realm() is not { } realm || _activeSet() is not { Length: > 0 } set) return;
        realm.DeathsContradictingStandardArrivalRooms = Count + 1;
        realm.DeathsContradictingStandardArrivalRoomsOn = set;
        Save();

        string seen = $"A death seen in {diedIn} ended in {arrivedIn}, where the standard list has the dead of that room "
            + $"arrive in {string.Join(" or ", expected)}";
        if (Marked)
            _log?.Warn(LearnedArrivalRoomStore.LogCategory,
                $"{seen}. That is {Count} such deaths on this board: its rooms do not match the standard data set it is "
                + $"played on ('{set}'). From now on the list is taken here as on any board with edited rooms: it says where "
                + "the dead arrive, never that a room is the wrong one, and what is learned from deaths seen is used. If the "
                + "board has its own game data, import it and point the realm at it; that also starts this count afresh.");
        else
            _log?.Warn(LearnedArrivalRoomStore.LogCategory,
                $"{seen}. On the standard data set ('{set}') that means this board's rooms were edited, or the map had the "
                + $"wrong room for one end of it. Nothing changes yet: {DeathsToMark - Count} more such death(s) and the list "
                + "is taken here as on a board with edited rooms.");
    }

    // The count is on the realm in memory either way, and goes out with the
    // board's next save when this one fails.
    private void Save()
    {
        try
        {
            _save();
            _saveFailed = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_saveFailed)
                _log?.Warn(LearnedArrivalRoomStore.LogCategory,
                    $"The board's file couldn't be written ({ex.Message}). Its count of deaths the standard list doesn't "
                    + "allow is kept for this session and written with the board's next save.");
            _saveFailed = true;
        }
    }
}

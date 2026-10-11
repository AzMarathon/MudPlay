using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Recovery;

// Learns where the dead of a room arrive from the deaths the client sees. Each
// room names the room its dead go to, and the imported game data doesn't carry
// that (GAME_MECHANICS "Death threshold & consequences"): for Paradigm's own
// rooms, and for a board whose rooms were edited, watching a death is the only
// way to find out. What is learned is kept with the game-data set
// (LearnedArrivalRoomStore) and read by the hang-up death check
// (HangupDeathArrivalRooms), for the rooms no list covers.
//
// A pair is learned only when both ends are certain:
//   - the death was seen (its lines read), in a room the map was sure of;
//   - it was not an arena death. That one is the arena's own rule, and where it
//     puts the character says nothing of a death that costs a life;
//   - the room the character arrived in is the one the first room display after
//     the death showed, with no move sent in between, and the map is sure of it.
// A graveyard too dark to show, a display the map can't place, a move typed
// before the room was drawn: nothing is learned, and nothing is tried again from
// a later display, by which time the character may stand somewhere else.
//
// Where the Stock list is the world's own (an unmodified Stock set, a zone
// Paradigm shares with Stock) nothing is learned: the list stands. A death seen
// to end somewhere the list doesn't have is said in the log, and left at that.
public sealed class ArrivalRoomLearner
{
    private readonly RoomTracker _tracker;
    private readonly LearnedArrivalRoomStore _store;
    private readonly Func<HangupArrivalListTrust> _trust;
    private readonly Func<RoomKey, RoomKey, bool>? _couldBeTakenFor;
    private readonly Func<RoomKey, bool>? _inAlbion;
    private readonly LogService? _log;

    // The room of a death just seen, until the first room display after it.
    private RoomKey? _diedIn;

    //   trust           — how far the Stock list goes for the active game data.
    //   couldBeTakenFor — RoomGraphManager.CouldBeTakenFor: the map can confirm a
    //                     listed arrival room as a room that looks the same.
    //   inAlbion        — the room is in Paradigm's Albion, whose dead are known
    //                     to arrive in the Burial Vault.
    public ArrivalRoomLearner(
        RoomTracker tracker,
        LearnedArrivalRoomStore store,
        Func<HangupArrivalListTrust> trust,
        Func<RoomKey, RoomKey, bool>? couldBeTakenFor = null,
        Func<RoomKey, bool>? inAlbion = null,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(trust);
        _tracker = tracker;
        _store = store;
        _trust = trust;
        _couldBeTakenFor = couldBeTakenFor;
        _inAlbion = inAlbion;
        _log = log;
    }

    // RoomTracker.PlayerDeathObserved.
    public void OnPlayerDied()
    {
        _diedIn = null;
        if (_tracker.LastDeathTookNothing || !_tracker.LastDeathRoomConfirmed) return;
        _diedIn = _tracker.LastDeathRoom;
    }

    // OutboundMovementObserver.MoveSent: the room the next display shows is no
    // longer the one the death left the character in.
    public void NoteMoveSent() => Forget();

    // The game was left, or the link dropped, before the room was drawn: on the
    // way back in the character may have been played from somewhere else.
    public void Forget() => _diedIn = null;

    // RoomTracker.StateChanged. Only the first transition a room display decides
    // counts, whatever it comes to.
    public void OnRoomStateChanged(RoomTransition transition)
    {
        if (_diedIn is not { } diedIn || !transition.Displayed) return;
        _diedIn = null;
        if (transition.NewConfidence != RoomConfidence.Confirmed || transition.NewRoom is not { } arrived)
        {
            _log?.Debug(LearnedArrivalRoomStore.LogCategory,
                $"nothing learned from the death in {diedIn}: the map couldn't place the room it arrived in");
            return;
        }
        Learn(diedIn, arrived.Key);
    }

    private void Learn(RoomKey diedIn, RoomKey arrivedIn)
    {
        HangupArrivalListTrust trust = _trust();
        bool Fits(IReadOnlyList<RoomKey> known) =>
            known.Contains(arrivedIn) || known.Any(room => _couldBeTakenFor?.Invoke(room, arrivedIn) == true);

        if (HangupDeathArrivalRooms.ListCovers(diedIn, trust))
        {
            IReadOnlyList<RoomKey> listed = HangupDeathArrivalRooms.For(diedIn)!;
            if (Fits(listed)) return;
            // On a board whose rooms may have been edited the list is only Stock's
            // word, and what was seen is one more room its dead can arrive in.
            if (trust == HangupArrivalListTrust.Unverified)
            {
                _store.Learn(diedIn, arrivedIn);
                return;
            }
            // The list is of where a HANG-UP's dead arrive, which for the rooms
            // with a leave-the-game room is not where a death's do. So this is
            // either one of those, or a finding about the data.
            _log?.Warn(LearnedArrivalRoomStore.LogCategory,
                $"A death seen in {diedIn} ended in {arrivedIn}, where the Stock list has the dead of a hang-up there arrive "
                + $"in {string.Join(" or ", listed)}. Not learned: the list stands for this game data. (The two differ for a "
                + "room with a leave-the-game room; otherwise the list or the map is wrong about one of these rooms.)");
            return;
        }

        // Albion's dead are known to arrive in the Burial Vault: seeing it adds nothing.
        if (trust == HangupArrivalListTrust.Paradigm && _inAlbion?.Invoke(diedIn) == true
            && Fits(new[] { HangupDeathArrivalRooms.ParadigmAlbionVault }))
            return;
        _store.Learn(diedIn, arrivedIn);
    }
}

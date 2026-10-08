namespace MudPlay.Game.Map;

// The wait both movement engines keep for a room command that only works in an
// empty room (`go hole:nomonsters …`). With a monster there the game refuses the
// command, sometimes without a word, and sending it again is refused again. So
// the step is held before it goes out, the room is cleared meanwhile even with
// Auto-Combat off, and the engine sends it once the room roster shows no monster.
//
// One instance per engine. The engine owns its own step state; this owns the
// probes, the limit timer and whether a hold is up.
internal sealed class EmptyRoomCommandHold
{
    // How long a held command waits with nothing else holding the engine before
    // the engine gives up: a monster nothing will fight (an NPC) never leaves.
    public static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(60);

    private Func<bool>? _roomHasMonster;
    private Action? _requestRoomClear;
    private Action? _abortPartyReform;
    private Func<Room, string, bool>? _needsEmptyRoom;
    private IDisposable? _timer;

    // True while a command waits for the room to be cleared. Combat reads it as a
    // force-clear, so the room is fought with Auto-Combat off.
    public bool Active { get; private set; }

    public bool RoomHasMonster => _roomHasMonster?.Invoke() == true;

    // roomHasMonster: any monster on the current room roster. requestRoomClear:
    // start the fight. abortPartyReform: drop the regroup hold a leader's relayed
    // teleport put up, since nobody went anywhere. needsEmptyRoom: whether a
    // command typed in a room carries the game data's empty-room condition.
    public void SetHooks(Func<bool> roomHasMonster, Action requestRoomClear, Action abortPartyReform,
        Func<Room, string, bool> needsEmptyRoom)
    {
        ArgumentNullException.ThrowIfNull(roomHasMonster);
        ArgumentNullException.ThrowIfNull(requestRoomClear);
        ArgumentNullException.ThrowIfNull(abortPartyReform);
        ArgumentNullException.ThrowIfNull(needsEmptyRoom);
        _roomHasMonster = roomHasMonster;
        _requestRoomClear = requestRoomClear;
        _abortPartyReform = abortPartyReform;
        _needsEmptyRoom = needsEmptyRoom;
    }

    // The first of a step's commands that can't work right now: it needs an empty
    // room and a monster is here. Null when the step may go out.
    public string? BlockedCommand(Room? room, IEnumerable<string> commands)
    {
        if (room is null || _needsEmptyRoom is null || !RoomHasMonster) return null;
        foreach (string command in commands)
            if (_needsEmptyRoom(room, command)) return command;
        return null;
    }

    // Put the hold up and have the room cleared. alreadySent is true when the
    // command went out and came back refused: a leader's relay then has the party
    // waiting to regroup after a teleport nobody made.
    public void Begin(Func<TimeSpan, Action, IDisposable>? schedule, Action onLimit, bool alreadySent)
    {
        if (alreadySent) _abortPartyReform?.Invoke();
        Active = true;
        _timer?.Dispose();
        _timer = schedule?.Invoke(WaitLimit, onLimit);
        _requestRoomClear?.Invoke();
    }

    public void End()
    {
        Active = false;
        _timer?.Dispose();
        _timer = null;
    }
}

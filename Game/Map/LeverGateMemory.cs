using MudPlay.Services;

namespace MudPlay.Game.Map;

// The lever-opened doors and gates a pull of ours is known to have opened, so a
// detour doesn't pull the lever again while one stands open: the engine toggles a
// door or gate on every action, and a pull on an open one locks it (GAME_MECHANICS
// "Multi-action exits and levers"). A loop lapping such a gate inside its open
// window pulled it shut every other lap.
//
// A gate counts as opened by us when its room's next display after our pull shows
// it open. It then stays open for at least the engine's exit reset, five minutes
// from the pull (a reset count of 1; the queue can only stretch it), unless
// someone else works the lever. A display of its room showing it shut forgets it.
// A gate seen open with no pull of ours behind it is left alone: when it opened,
// and so how long it stays, isn't known.
public sealed class LeverGateMemory
{
    public static readonly TimeSpan OpenWindow = TimeSpan.FromMinutes(5);

    private readonly RoomTracker _tracker;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;
    private readonly Dictionary<(RoomKey Room, Direction Dir), DateTimeOffset> _pulledAt = new();
    private readonly Dictionary<(RoomKey Room, Direction Dir), DateTimeOffset> _openedAt = new();

    public LeverGateMemory(RoomTracker tracker, Func<DateTimeOffset>? now = null, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
        _now = now ?? (static () => DateTimeOffset.UtcNow);
        _log = log;
        tracker.StateChanged += OnTransition;
    }

    // A lever of this door or gate went out from one of our detours.
    public void NotePulled((RoomKey Room, Direction Dir) gate) => _pulledAt[gate] = _now();

    // When the gate was opened by a pull of ours that is still inside its window;
    // null when it isn't known to stand open.
    public DateTimeOffset? OpenedAt((RoomKey Room, Direction Dir) gate) =>
        _openedAt.TryGetValue(gate, out DateTimeOffset at) && _now() - at < OpenWindow ? at : null;

    public IReadOnlyList<string> Describe() =>
        _openedAt.Where(kv => _now() - kv.Value < OpenWindow)
            .Select(kv => $"{kv.Key.Room} {kv.Key.Dir} opened {kv.Value.ToLocalTime():HH:mm:ss}")
            .ToList();

    // A display settled us in a room. Read off the transition, not RoomShown: an
    // engine moves on from inside the transition, and the display of a room left at
    // once raises no RoomShown. The doors the tracker holds are that display's
    // until the next one is read.
    private void OnTransition(RoomTransition t)
    {
        if (!t.Displayed || t.NewConfidence != RoomConfidence.Confirmed || t.NewRoom is not { } room) return;
        if (_pulledAt.Count == 0 && _openedAt.Count == 0) return;
        NoteDoors(room.Key, _tracker.State.OpenDoorDirections, _tracker.State.ClosedDoorDirections);
    }

    private void NoteDoors(RoomKey room, IReadOnlySet<Direction>? openDoors, IReadOnlySet<Direction>? closedDoors)
    {
        foreach ((RoomKey Room, Direction Dir) gate in _pulledAt.Keys.Concat(_openedAt.Keys).Distinct().ToList())
        {
            if (!gate.Room.Equals(room)) continue;
            if (closedDoors?.Contains(gate.Dir) == true)
            {
                _pulledAt.Remove(gate);
                if (_openedAt.Remove(gate))
                    _log?.Info("LeverGates", $"{gate.Room} {gate.Dir} shown shut: its lever is pulled again next time");
                continue;
            }
            if (openDoors?.Contains(gate.Dir) != true
                || !_pulledAt.Remove(gate, out DateTimeOffset pulled)) continue;
            _openedAt[gate] = pulled;
            _log?.Info("LeverGates",
                $"{gate.Room} {gate.Dir} shown open after our pull at {pulled.ToLocalTime():HH:mm:ss}: its lever isn't pulled again for {OpenWindow.TotalMinutes:0} minutes");
        }
    }
}

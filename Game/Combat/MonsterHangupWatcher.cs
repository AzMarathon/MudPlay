using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.Game.Combat;

// Hangs up when a monster whose Game Data relationship is Hangup is on the room
// roster (user, 2026-10-09: "if we see that monster by name, it hangs up the
// client"). The sight is the trigger, not an attack, so this is built ahead of the
// combat tracker and engine: its handler runs before theirs and the exit command is
// on the wire before anything that would start a fight in the room, which is what a
// board charges its hang-up penalty for.
//
// "Seen" is whatever puts the monster on the classifier's roster: the room display's
// "Also here:" line, an arrival line that names it, and in a dark room the attack
// line DarkRoomCombatWatcher reads its name off. A room that shows no names (too
// dark, or we are blind) puts nothing on the roster, so nothing is answered. A
// `look` into the next room is dropped by the classifier and is not a sighting.
//
// The relationship is read off the record the classifier resolved the name to, as
// the combat engine reads it. A name it could not pin to a record has no
// relationship to read and is left alone.
//
// One answer per sighting. A sighting lasts while some Hangup monster stays on the
// roster, so the death, arrival and re-issued rosters that follow while the line is
// dropping send nothing more. A dropped connection ends it: the same monster in the
// same room after a reconnect is a new sighting. That can't loop on its own, since
// the hang-up is never dialled back and the next connect stops at the menu
// (HangupSignal).
//
// While Disable Hangups is on nothing is sent and the sighting stays open, so the
// first roster after it is turned off is answered.
public sealed class MonsterHangupWatcher : IDisposable
{
    public const string LogCategory = "MonsterHangup";

    private readonly RoomEntityClassifier _classifier;
    private readonly Func<int, MonsterOverlay> _resolveOverlay;
    private readonly Func<bool> _hangupsDisabled;
    private readonly Func<string, bool> _hangUp;
    private readonly Func<string> _describeRoom;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;

    private bool _answered;
    private bool _heldLogged;

    // The last sighting and what came of it, for the bug report.
    public string LastSighting { get; private set; } = "(none this session)";

    // hangUp is HealthManager.HangUpForMonster: true when the escape went out.
    // describeRoom words where the roster was read, for the log.
    public MonsterHangupWatcher(
        RoomEntityClassifier classifier,
        Func<int, MonsterOverlay> resolveOverlay,
        Func<bool> hangupsDisabled,
        Func<string, bool> hangUp,
        Func<string> describeRoom,
        LogService? log = null,
        Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(resolveOverlay);
        ArgumentNullException.ThrowIfNull(hangupsDisabled);
        ArgumentNullException.ThrowIfNull(hangUp);
        ArgumentNullException.ThrowIfNull(describeRoom);
        _classifier = classifier;
        _resolveOverlay = resolveOverlay;
        _hangupsDisabled = hangupsDisabled;
        _hangUp = hangUp;
        _describeRoom = describeRoom;
        _log = log;
        _now = now ?? (() => DateTimeOffset.Now);

        _classifier.EntitiesObserved += OnEntitiesObserved;
    }

    // The connection dropped. The roster outlives it, so without this the monster
    // still standing there after a reconnect would read as the sighting already
    // answered.
    public void NoteDisconnected()
    {
        _answered = false;
        _heldLogged = false;
    }

    private void OnEntitiesObserved(RoomEntitiesObservation obs)
    {
        if (FindHangupMonster(obs) is not { } seen)
        {
            _answered = false;
            _heldLogged = false;
            return;
        }
        if (_answered) return;

        string what = $"{seen.RawName} (#{seen.MonsterNumber})";
        string where = _describeRoom();

        if (_hangupsDisabled())
        {
            // Once per sighting: a fight in the room re-issues the roster every round.
            if (_heldLogged) return;
            _heldLogged = true;
            Record($"{what} seen {where}: its relationship is Hangup, but Disable Hangups is on, so no hang-up");
            return;
        }

        _answered = true;
        Record($"{what} seen {where}: its relationship is Hangup, hanging up");
        if (_hangUp($"{what} is here, relationship Hangup")) return;

        // HealthManager has said why in its own line (no exit command is set).
        LastSighting += "; the hang-up did not go out";
        _log?.Warn(LogCategory, $"{what}: the hang-up did not go out");
    }

    private RoomEntity? FindHangupMonster(RoomEntitiesObservation obs)
    {
        foreach (RoomEntity e in obs.Entities)
        {
            if (e.Kind != EntityKind.Monster || e.MonsterNumber is not int number) continue;
            if (RelationshipOf(number) == MonsterRelationship.Hangup) return e;
        }
        return null;
    }

    private MonsterRelationship? RelationshipOf(int number)
    {
        try { return _resolveOverlay(number)?.Relationship; }
        catch
        {
            // A record that can't be read (no active set, a malformed override
            // file) is no instruction to hang up, and a throw here would stop the
            // combat handlers queued behind this one.
            return null;
        }
    }

    private void Record(string what)
    {
        LastSighting = $"{_now().ToLocalTime():HH:mm:ss} {what}";
        _log?.Info(LogCategory, what);
    }

    public void Dispose() => _classifier.EntitiesObserved -= OnEntitiesObserved;
}

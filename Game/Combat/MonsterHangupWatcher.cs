using MudPlay.Game.Health;
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
// One answer per sighting. A sighting is one room display's roster: it begins with
// an "Also here:" line and runs through the arrivals, deaths and re-issues that
// follow, so those send nothing more; the next "Also here:" line (the next room, or
// this one displayed again) begins another. Inside one, only a Hangup monster that
// was not there when it was answered is answered again.
//
// What is asked for is HealthManager's escape, so what stops the low-HP hang-up
// stops this: Disable Hangups, and the all-off rule. So does a roster the PvP side
// is handling, a fight with a player or an Enemy player in the room (user,
// 2026-10-09: "pvp actions win"). Those, and the board's menu, leave the sighting
// open, so the first roster after they lift is answered. With
// the sysop wimpy jump set up the escape is a jump and the session goes on: nothing
// here stops a running loop from walking back to the monster and jumping again,
// just as nothing does after a low-HP jump.
//
// After a hang-up for a monster seen here, the watch is off for a minute once the
// character is back in the game (user, 2026-10-09: "if a user manually reconnects
// ... the auto-hang feature should be suppressed for a minute with a countdown
// timer visible somewhere"), so a manual reconnect into the same room is not hung
// up on at once. The minute starts at the first game prompt after the reconnect, so
// the login does not eat it, and when it ends the roster is read again. It is for a
// reconnect the user makes: the PvP response's own dial-back cancels it
// (CancelHold), or it would stand the character beside the monster unattended.
public sealed class MonsterHangupWatcher : IDisposable
{
    public const string LogCategory = "MonsterHangup";

    // How long the watch stays off after a reconnect that follows our own hang-up.
    public static readonly TimeSpan HoldLength = TimeSpan.FromSeconds(60);

    // The client closes the line itself within a second of the exit command. A
    // hang-up with no disconnect this long after it did not take, and the watch
    // goes back on.
    private static readonly TimeSpan DropLimit = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private readonly RoomEntityClassifier _classifier;
    private readonly Func<int, MonsterOverlay> _resolveOverlay;
    private readonly Func<string, EscapeOutcome> _hangUp;
    private readonly Func<bool> _hangupsDisabled;
    private readonly Func<RoomEntitiesObservation, bool> _pvpHandles;
    private readonly Func<bool> _atBoardMenu;
    private readonly Func<string> _describeRoom;
    private readonly Action<TimeSpan, Action> _schedule;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;

    // The sighting: the room display it began with, the Hangup records on the
    // roster when it was answered, and the reason last logged for not answering.
    private DateTimeOffset? _displayAt;
    private readonly HashSet<int> _answered = new();
    private string? _heldFor;

    private DateTimeOffset? _hungUpAt;      // our exit command went out; the drop is awaited
    private DateTimeOffset? _escapeSeenAt;  // a Hangup monster was seen just after another path's escape went out
    private bool _holdArmed;                // dropped by our hang-up; the minute starts at the next game prompt
    private DateTimeOffset? _holdUntil;     // the minute is running
    private bool _holdSightingLogged;
    private int _holdTicks;                 // outdates the ticks of a hold that has ended
    private bool _unreadableWarned;

    // The last sighting and what came of it, for the bug report.
    public string LastSighting { get; private set; } = NoSighting;
    private const string NoSighting = "(none this session)";

    // Whole seconds left of the minute, or null when it is not running.
    public int? HoldSecondsLeft =>
        _holdUntil is { } until ? Math.Max(0, (int)Math.Ceiling((until - _now()).TotalSeconds)) : null;

    // "Hangup watch off 0:59" while the minute runs, for the status bar.
    public string? HoldText =>
        HoldSecondsLeft is { } left ? $"Hangup watch off {left / 60}:{left % 60:00}" : null;

    // No hang-up will come for a Hangup monster right now, by the user's choice
    // (Disable Hangups) or for the minute after a reconnect. Self-defence reads
    // it: a Hangup monster that attacks while this is true is fought back (user,
    // 2026-10-09: "fight back"). The other reasons nothing is sent are not in it:
    // in all-off mode nothing automatic responds, and a roster with a player to
    // answer is the PvP actions' to run.
    public bool WatchIsOff => _hangupsDisabled() || _holdArmed || _holdUntil is not null;

    // The hold started, ticked down a second, or ended.
    public event Action? HoldChanged;

    // A line for the terminal when the hold starts and when it ends.
    public event Action<string>? HoldNotice;

    // hangUp is HealthManager.HangUpForMonster. hangupsDisabled is the Disable
    // Hangups switch, read here only for WatchIsOff: whether a hang-up goes out
    // is HealthManager's to say. pvpHandles is true for a roster the PvP side is
    // answering: a fight with a player under way, or an Enemy player on it. This
    // handler runs ahead of the PvP response's, so it has to ask rather than see
    // what the response did. atBoardMenu is true while the character has left the
    // game for the board's menu with the link up, where an exit command would be
    // a menu selection. describeRoom words where the roster was read, for the
    // log. schedule runs a callback after a delay, for the countdown.
    public MonsterHangupWatcher(
        RoomEntityClassifier classifier,
        Func<int, MonsterOverlay> resolveOverlay,
        Func<string, EscapeOutcome> hangUp,
        Func<bool> hangupsDisabled,
        Func<RoomEntitiesObservation, bool> pvpHandles,
        Func<bool> atBoardMenu,
        Func<string> describeRoom,
        Action<TimeSpan, Action> schedule,
        LogService? log = null,
        Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(resolveOverlay);
        ArgumentNullException.ThrowIfNull(hangUp);
        ArgumentNullException.ThrowIfNull(hangupsDisabled);
        ArgumentNullException.ThrowIfNull(pvpHandles);
        ArgumentNullException.ThrowIfNull(atBoardMenu);
        ArgumentNullException.ThrowIfNull(describeRoom);
        ArgumentNullException.ThrowIfNull(schedule);
        _classifier = classifier;
        _resolveOverlay = resolveOverlay;
        _hangUp = hangUp;
        _hangupsDisabled = hangupsDisabled;
        _pvpHandles = pvpHandles;
        _atBoardMenu = atBoardMenu;
        _describeRoom = describeRoom;
        _schedule = schedule;
        _log = log;
        _now = now ?? (() => DateTimeOffset.Now);

        _classifier.EntitiesObserved += OnEntitiesObserved;
    }

    // The hold, for the bug report.
    public string DescribeHold() =>
        HoldSecondsLeft is { } left ? $"watch off, {left}s left"
        : _holdArmed ? "watch off: the minute starts at the first game prompt after the reconnect"
        : "none";

    // The connection dropped. If it dropped with a Hangup monster in sight (our
    // hang-up, or a low-HP or PvP hang-up that had just gone out when the monster
    // was seen), the watch is off until a minute into the next stay in the game.
    public void NoteDisconnected()
    {
        DateTimeOffset now = _now();
        bool ours = (_hungUpAt is { } hungUp && now - hungUp < DropLimit)
                    || (_escapeSeenAt is { } seen && now - seen < DropLimit);
        _hungUpAt = null;
        _escapeSeenAt = null;
        EndSighting();
        _displayAt = null;
        if (!ours || _holdArmed) return;
        _holdArmed = true;
        _holdSightingLogged = false;
        _log?.Info(LogCategory,
            $"the line dropped for a Hangup monster: the watch is off until {HoldLength.TotalSeconds:0}s after the next game prompt");
    }

    // The dial-back that follows this drop is automatic (the PvP response's own
    // reconnect). The minute is for a user who came back to look: nobody is looking
    // now, so a Hangup monster still there is answered at once.
    public void CancelHold()
    {
        if (!_holdArmed) return;
        _holdArmed = false;
        _log?.Info(LogCategory, "the reconnect is automatic, so the watch stays on after it");
    }

    // Back in the game from the board's menu: a sighting held there (the room is
    // displayed ahead of the first prompt) is answered now.
    public void NoteBackInGame()
    {
        if (_classifier.Current is { } roster) OnEntitiesObserved(roster);
    }

    // A game prompt: the character is in the game. The first one after our hang-up
    // dropped the line starts the minute.
    public void NoteInGamePrompt()
    {
        if (!_holdArmed) return;
        _holdArmed = false;
        _holdUntil = _now() + HoldLength;
        int ticks = ++_holdTicks;
        string note = $"Hangup watch off for {HoldLength.TotalSeconds:0} seconds: this character hung up for a Hangup monster before reconnecting";
        _log?.Info(LogCategory, note);
        HoldNotice?.Invoke(note);
        HoldChanged?.Invoke();
        _schedule(Second, () => Tick(ticks));
    }

    // Another character: none of this is theirs.
    public void Reset()
    {
        bool held = _holdUntil is not null;
        _holdTicks++;
        _hungUpAt = null;
        _escapeSeenAt = null;
        _holdArmed = false;
        _holdUntil = null;
        _holdSightingLogged = false;
        _displayAt = null;
        EndSighting();
        LastSighting = NoSighting;
        if (held) HoldChanged?.Invoke();
    }

    private void Tick(int ticks)
    {
        if (ticks != _holdTicks || _holdUntil is not { } until) return;
        if (_now() < until)
        {
            HoldChanged?.Invoke();
            _schedule(Second, () => Tick(ticks));
            return;
        }

        _holdUntil = null;
        _holdSightingLogged = false;
        const string note = "Hangup watch back on";
        _log?.Info(LogCategory, note);
        HoldNotice?.Invoke(note);
        HoldChanged?.Invoke();
        // Whatever stood in the room through the minute is seen now.
        EndSighting();
        if (_classifier.Current is { } roster) OnEntitiesObserved(roster);
    }

    private void OnEntitiesObserved(RoomEntitiesObservation obs)
    {
        // At names a room display: only a fresh "Also here:" parse stamps a new one,
        // while a re-emit or a reclassified roster carries the stamp it had.
        if (obs.Source == RoomObservationSource.AlsoHere && obs.At != _displayAt)
        {
            _displayAt = obs.At;
            EndSighting();
        }

        List<RoomEntity> hangups = HangupMonsters(obs);
        if (hangups.Count == 0)
        {
            EndSighting();
            return;
        }
        // One that left and came back inside the same display is seen afresh.
        _answered.IntersectWith(hangups.Select(e => e.MonsterNumber!.Value));
        RoomEntity? unanswered = null;
        foreach (RoomEntity e in hangups)
        {
            if (_answered.Contains(e.MonsterNumber!.Value)) continue;
            unanswered = e;
            break;
        }
        if (unanswered is not { } seen) return;

        string what = $"{seen.RawName} (#{seen.MonsterNumber})";

        if (DropAwaited()) return;
        if (_holdArmed || _holdUntil is not null)
        {
            // Once per hold: a fight in the room re-issues the roster every round.
            if (_holdSightingLogged) return;
            _holdSightingLogged = true;
            Record($"{what} seen {_describeRoom()}: its relationship is Hangup, but the watch is off after the reconnect ({DescribeHold()})");
            return;
        }
        if (_atBoardMenu())
        {
            Hold("menu", what, "the character is at the board's menu, not in the game");
            return;
        }
        if (_pvpHandles(obs))
        {
            // The roster is issued again when the fight ends or the player leaves,
            // and the monster is answered then.
            Hold("pvp", what, "the PvP actions come first (a fight with a player, or an Enemy player in the room)");
            return;
        }

        EscapeOutcome outcome = _hangUp($"{what} is here, relationship Hangup");
        switch (outcome)
        {
            case EscapeOutcome.HangupsDisabled:
                Hold("disabled", what, "Disable Hangups is on, so no hang-up");
                return;
            case EscapeOutcome.AllOff:
                Hold("all-off", what,
                    "the master switch is off and Allow hangup in all-off mode is not ticked, so no hang-up");
                return;
        }

        foreach (RoomEntity e in hangups) _answered.Add(e.MonsterNumber!.Value);
        _heldFor = null;
        switch (outcome)
        {
            case EscapeOutcome.HungUp:
                _hungUpAt = _now();
                Record($"{what} seen {_describeRoom()}: its relationship is Hangup, hung up");
                break;
            case EscapeOutcome.Jumped:
                Record($"{what} seen {_describeRoom()}: its relationship is Hangup, jumped to the wimpy location in place of the hang-up");
                break;
            case EscapeOutcome.AlreadyEscaping:
                // Kept apart from _hungUpAt: the escape may have been a jump, and no
                // drop is owed for one.
                _escapeSeenAt = _now();
                Record($"{what} seen {_describeRoom()}: its relationship is Hangup, and another escape had just gone out, so nothing more was sent");
                break;
            default:
                // HealthManager has said why in its own line (no exit command is set).
                Record($"{what} seen {_describeRoom()}: its relationship is Hangup, but the hang-up did not go out");
                _log?.Warn(LogCategory, $"{what}: the hang-up did not go out");
                break;
        }
    }

    // Our exit command is on the wire and the line has not dropped yet: whatever
    // the roster does in that moment sends nothing more.
    private bool DropAwaited()
    {
        if (_hungUpAt is not { } at) return false;
        if (_now() - at < DropLimit) return true;
        _hungUpAt = null;
        _log?.Warn(LogCategory,
            $"the line did not drop within {DropLimit.TotalSeconds:0}s of the hang-up: the watch is back on");
        return false;
    }

    // Not answered and not latched: said once per sighting for each reason, since a
    // fight in the room re-issues the roster every round.
    private void Hold(string key, string what, string why)
    {
        if (_heldFor == key) return;
        _heldFor = key;
        Record($"{what} seen {_describeRoom()}: its relationship is Hangup, but {why}");
    }

    private void EndSighting()
    {
        _answered.Clear();
        _heldFor = null;
    }

    private List<RoomEntity> HangupMonsters(RoomEntitiesObservation obs)
    {
        List<RoomEntity> found = new();
        foreach (RoomEntity e in obs.Entities)
        {
            if (e.Kind != EntityKind.Monster || e.MonsterNumber is not int number) continue;
            if (RelationshipOf(number) == MonsterRelationship.Hangup) found.Add(e);
        }
        return found;
    }

    // A record that can't be read (no active set, a malformed override file) is no
    // instruction to hang up, and a throw here would stop the combat handlers queued
    // behind this one. Said once: it would otherwise repeat on every roster.
    private MonsterRelationship? RelationshipOf(int number)
    {
        try { return _resolveOverlay(number)?.Relationship; }
        catch (Exception ex)
        {
            if (!_unreadableWarned)
            {
                _unreadableWarned = true;
                _log?.Warn(LogCategory,
                    $"monster #{number}'s relationship could not be read ({ex.Message}); it is treated as not Hangup. Further failures are not logged");
            }
            return null;
        }
    }

    private void Record(string what)
    {
        LastSighting = $"{_now().ToLocalTime():HH:mm:ss} {what}";
        _log?.Info(LogCategory, what);
    }

    public void Dispose()
    {
        _holdTicks++;
        _classifier.EntitiesObserved -= OnEntitiesObserved;
    }
}

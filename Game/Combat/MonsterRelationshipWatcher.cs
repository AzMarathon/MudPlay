using MudPlay.Game.Health;
using MudPlay.Models.GameData;
using MudPlay.Services;

namespace MudPlay.Game.Combat;

// Answers the two Game Data relationships that are acted on at the sight of the
// monster: Hangup with a hang-up (user, 2026-10-09: "if we see that monster by
// name, it hangs up the client") and Flee with a run (user, 2026-10-09: "yes" to
// seeing one running the character away through the existing flee settings). The
// sight is the trigger, not an attack, so this is built ahead of the combat tracker
// and engine: its handler runs before theirs, and the exit command or the first
// move of the run is on the wire before anything that would start a fight in the
// room, which is what a board charges its hang-up penalty for.
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
// this one displayed again) begins another. Inside one, only a monster that was not
// there when its relationship was answered is answered again. Each relationship
// keeps its own count of what it has answered.
//
// For Hangup, what is asked for is HealthManager's escape, so what stops the low-HP
// hang-up stops this: Disable Hangups, and the all-off rule. So does a roster the
// PvP side is handling, a fight with a player or an Enemy player in the room (user,
// 2026-10-09: "pvp actions win"). Those, and the board's menu, leave the sighting
// open, so the first roster after they lift is answered. With
// the sysop wimpy jump set up the escape is a jump and the session goes on: nothing
// here stops a running loop from walking back to the monster and jumping again,
// just as nothing does after a low-HP jump.
//
// For Flee, what is asked for is HealthManager's flee, the retreat a low-HP run
// makes, so it needs what that needs (a walk or loop to run along, the health
// engine on, a character that is not a party follower) and ends as that ends: the
// walk or loop is picked up again once the run has landed. A walk or loop the user
// has paused is idle and is not run with (user, 2026-10-10). Run backwards, the
// route still leads through the monster's room, so a monster that stands there is
// met again and run from again for as long as it does; run forwards, the route
// goes on from where the run landed. No guard is built for the first (user,
// 2026-10-10: "if we have it on we will just keep bouncing and its something the
// user needs to fix themselves"). No run starts while Auto-All, the master switch,
// is off (user, 2026-10-09: "when i say all auto's off, i mean the master switch
// is off"; "none of our auto systems should respond"). The board's menu, the PvP
// side, the master switch, the health engine being off and a character that is
// down leave the sighting open, as the first two do for Hangup. A run that could
// not start (no walk or loop, a paused one, no way out, a follower) is not tried
// again inside the same sighting: a walk started beside the monster would
// otherwise be turned round by the next roster. No hang-up is sent in a run's
// place.
//
// A Flee monster no run is coming for is fought back when it attacks (user,
// 2026-10-09: "fight back"), as a Hangup monster is when no hang-up is coming:
// NoAnswerComing says so to self-defence. That covers a run that is over with the
// monster still there: its first move refused, or a second Flee monster where it
// landed. It is still never picked on sight.
//
// Hangup outranks Flee. With both on the roster the hang-up is the answer and no
// run is started, unless no hang-up went out and none is owed (Disable Hangups,
// the all-off rule, the minute after a reconnect, no exit command): then the Flee
// monster is answered as if alone, run from if a walk or loop is running, and
// otherwise both are left alone until they attack (user, 2026-10-09: "it would
// only run from the flee monster if a navigation engine was running ... otherwise
// it'd fight, and if it was idle, and disable hangups was on, it would fight
// both").
//
// After a hang-up for a monster seen here, the watch is off for a minute once the
// character is back in the game (user, 2026-10-09: "if a user manually reconnects
// ... the auto-hang feature should be suppressed for a minute with a countdown
// timer visible somewhere"), so a manual reconnect into the same room is not hung
// up on at once. The minute starts at the first game prompt after the reconnect, so
// the login does not eat it, and when it ends the roster is read again. It is for a
// reconnect the user makes: the PvP response's own dial-back cancels it
// (CancelHold), or it would stand the character beside the monster unattended.
public sealed class MonsterRelationshipWatcher : IDisposable
{
    public const string HangupLogCategory = "MonsterHangup";
    public const string FleeLogCategory = "MonsterFlee";

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
    private readonly Func<string, Func<bool>, FleeOutcome> _flee;
    private readonly Func<bool> _fleeInFlight;
    private readonly Func<bool> _masterSwitchOff;
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
    private bool _escapeAnswered;   // the answer was an escape that went out, not one that could not

    // The same for Flee: the records answered in this display, and why no run was
    // started for the last one seen (null once one was).
    private readonly HashSet<int> _fleeAnswered = new();
    private string? _fleeHeldFor;

    // A Flee monster is in the room and the client is not going to run from it, so
    // it is to be fought back if it attacks. False while a run is under way or on
    // its way, and for the holds that are someone else's to answer: the hang-up, the
    // PvP actions, the board's menu, a character that is down.
    private bool _noRunComing;

    // The sighting was answered with a run. Once that run is over (it landed, or
    // its move was refused) and a Flee monster is still on the roster, no other run
    // is coming for it inside this sighting: a second one stood where the run
    // landed, or the game would not let the character leave.
    private bool _answeredByRun;

    private DateTimeOffset? _hungUpAt;      // our exit command went out; the drop is awaited
    private DateTimeOffset? _escapeSeenAt;  // a Hangup monster was seen just after another path's escape went out
    private bool _holdArmed;                // dropped by our hang-up; the minute starts at the next game prompt
    private DateTimeOffset? _holdUntil;     // the minute is running
    private bool _holdSightingLogged;
    private int _holdTicks;                 // outdates the ticks of a hold that has ended
    private bool _unreadableWarned;

    // The last sighting of each relationship and what came of it, for the bug report.
    public string LastHangupSighting { get; private set; } = NoSighting;
    public string LastFleeSighting { get; private set; } = NoSighting;
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
    public bool HangupWatchIsOff => _hangupsDisabled() || _holdArmed || _holdUntil is not null;

    // Whether a monster of this relationship that attacks is to be fought back
    // because its own answer is not coming. Self-defence asks it for the two
    // relationships it otherwise leaves alone. With the master switch off no run is
    // coming either, and whether anything is fought then is self-defence's own gate
    // to say, as it is for a Neutral monster.
    public bool NoAnswerComing(MonsterRelationship relationship) => relationship switch
    {
        MonsterRelationship.Hangup => HangupWatchIsOff,
        MonsterRelationship.Flee => _noRunComing || (_answeredByRun && !_fleeInFlight()),
        _ => false,
    };

    // The hold started, ticked down a second, or ended.
    public event Action? HoldChanged;

    // A line for the terminal when the hold starts and when it ends.
    public event Action<string>? HoldNotice;

    // hangUp is HealthManager.HangUpForMonster and flee is
    // HealthManager.FleeFromMonster; the flee is handed a way to ask whether a Flee
    // monster is still on the roster, for a run that has to wait for a move to
    // land. fleeInFlight is HealthManager.IsFleeInFlight: a flee is moving, or held
    // for a move to land. masterSwitchOff is true while Auto-All has switched every auto off
    // (AutoModeController.KillSwitchEngaged); it is read for the run only.
    // hangupsDisabled is the Disable Hangups switch, read here only for
    // HangupWatchIsOff: whether a hang-up goes out is HealthManager's to say.
    // pvpHandles is true for a roster the PvP side is
    // answering: a fight with a player under way, or an Enemy player on it. This
    // handler runs ahead of the PvP response's, so it has to ask rather than see
    // what the response did. atBoardMenu is true while the character has left the
    // game for the board's menu with the link up, where an exit command would be
    // a menu selection. describeRoom words where the roster was read, for the
    // log. schedule runs a callback after a delay, for the countdown.
    public MonsterRelationshipWatcher(
        RoomEntityClassifier classifier,
        Func<int, MonsterOverlay> resolveOverlay,
        Func<string, EscapeOutcome> hangUp,
        Func<string, Func<bool>, FleeOutcome> flee,
        Func<bool> fleeInFlight,
        Func<bool> masterSwitchOff,
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
        ArgumentNullException.ThrowIfNull(flee);
        ArgumentNullException.ThrowIfNull(fleeInFlight);
        ArgumentNullException.ThrowIfNull(masterSwitchOff);
        ArgumentNullException.ThrowIfNull(hangupsDisabled);
        ArgumentNullException.ThrowIfNull(pvpHandles);
        ArgumentNullException.ThrowIfNull(atBoardMenu);
        ArgumentNullException.ThrowIfNull(describeRoom);
        ArgumentNullException.ThrowIfNull(schedule);
        _classifier = classifier;
        _resolveOverlay = resolveOverlay;
        _hangUp = hangUp;
        _flee = flee;
        _fleeInFlight = fleeInFlight;
        _masterSwitchOff = masterSwitchOff;
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
        _log?.Info(HangupLogCategory,
            $"the line dropped for a Hangup monster: the watch is off until {HoldLength.TotalSeconds:0}s after the next game prompt");
    }

    // The dial-back that follows this drop is automatic (the PvP response's own
    // reconnect). The minute is for a user who came back to look: nobody is looking
    // now, so a Hangup monster still there is answered at once.
    public void CancelHold()
    {
        if (!_holdArmed) return;
        _holdArmed = false;
        _log?.Info(HangupLogCategory, "the reconnect is automatic, so the watch stays on after it");
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
        _log?.Info(HangupLogCategory, note);
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
        LastHangupSighting = NoSighting;
        LastFleeSighting = NoSighting;
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
        _log?.Info(HangupLogCategory, note);
        HoldNotice?.Invoke(note);
        HoldChanged?.Invoke();
        // Whatever stood in the room through the minute is seen now. The minute held
        // back hang-ups only, so a Flee monster's answer in this display stands.
        EndHangupSighting();
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

        ReadRoster(obs, out List<RoomEntity> hangups, out List<RoomEntity> flees);
        bool hangupAnswers = AnswerHangup(obs, hangups);
        AnswerFlee(obs, flees, hangupAnswers);
    }

    // Returns whether the hang-up is this roster's answer: one went out for it, or
    // one is owed as soon as the board's menu or the PvP side lets go. Where none is
    // coming (Disable Hangups, the all-off rule, the minute after a reconnect, no
    // exit command) a Flee monster beside the Hangup one is answered as if alone.
    private bool AnswerHangup(RoomEntitiesObservation obs, List<RoomEntity> hangups)
    {
        if (hangups.Count == 0)
        {
            EndHangupSighting();
            return false;
        }
        if (FirstUnanswered(hangups, _answered) is not { } seen) return _escapeAnswered;

        string what = $"{seen.RawName} (#{seen.MonsterNumber})";

        if (DropAwaited()) return true;
        if (_holdArmed || _holdUntil is not null)
        {
            // Once per hold: a fight in the room re-issues the roster every round.
            if (_holdSightingLogged) return false;
            _holdSightingLogged = true;
            Record($"{what} seen {_describeRoom()}: its relationship is Hangup, but the watch is off after the reconnect ({DescribeHold()})");
            return false;
        }
        if (_atBoardMenu())
        {
            Hold("menu", what, "the character is at the board's menu, not in the game");
            return true;
        }
        if (_pvpHandles(obs))
        {
            // The roster is issued again when the fight ends or the player leaves,
            // and the monster is answered then.
            Hold("pvp", what, "the PvP actions come first (a fight with a player, or an Enemy player in the room)");
            return true;
        }

        EscapeOutcome outcome = _hangUp($"{what} is here, relationship Hangup");
        switch (outcome)
        {
            case EscapeOutcome.HangupsDisabled:
                Hold("disabled", what, "Disable Hangups is on, so no hang-up");
                return false;
            case EscapeOutcome.AllOff:
                Hold("all-off", what,
                    "Auto-Heal and Auto-Rest are off and Allow hangup in all-off mode is not ticked, so no hang-up");
                return false;
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
                _log?.Warn(HangupLogCategory, $"{what}: the hang-up did not go out");
                break;
        }
        _escapeAnswered = outcome is EscapeOutcome.HungUp or EscapeOutcome.Jumped or EscapeOutcome.AlreadyEscaping;
        return _escapeAnswered;
    }

    private void AnswerFlee(RoomEntitiesObservation obs, List<RoomEntity> flees, bool hangupAnswers)
    {
        if (flees.Count == 0)
        {
            EndFleeSighting();
            return;
        }
        if (FirstUnanswered(flees, _fleeAnswered) is not { } seen) return;

        string what = $"{seen.RawName} (#{seen.MonsterNumber})";

        if (hangupAnswers)
        {
            NoRun("hangup", what, "a Hangup monster is here as well, and the hang-up is the answer to this room", fightBack: false);
            return;
        }
        if (_atBoardMenu())
        {
            NoRun("menu", what, "the character is at the board's menu, not in the game", fightBack: false);
            return;
        }
        if (_pvpHandles(obs))
        {
            // Answered when the fight ends or the player leaves, as a Hangup monster is.
            NoRun("pvp", what, "the PvP actions come first (a fight with a player, or an Enemy player in the room)", fightBack: false);
            return;
        }
        if (_masterSwitchOff())
        {
            // Asked here and not of HealthManager, whose other flees are not held
            // to the master switch. An auto turned back on by hand leaves the
            // switch off, so the health engine's own test below does not cover it.
            NoRun("master-off", what, "Auto-All is off (the master switch), so nothing automatic runs", fightBack: true);
            return;
        }

        FleeOutcome outcome = _flee($"{what} is here, relationship Flee", FleeMonsterStillHere);
        switch (outcome)
        {
            case FleeOutcome.EngineOff:
                NoRun("engine-off", what, "Auto-Heal and Auto-Rest are both off, and they run every flee", fightBack: true);
                return;
            case FleeOutcome.Down:
                // Left open: back on its feet, the character is asked again at the
                // next roster and runs or defends itself.
                NoRun("down", what, "the character is down and cannot move", fightBack: false);
                return;
        }

        // Everything from here on is this sighting's one answer, a run or not.
        foreach (RoomEntity e in flees) _fleeAnswered.Add(e.MonsterNumber!.Value);
        switch (outcome)
        {
            case FleeOutcome.Started:
                Running($"{what} seen {_describeRoom()}: its relationship is Flee, running (the Combat tab's run distance and direction)", aRun: true);
                break;
            case FleeOutcome.AlreadyRunning:
                Running($"{what} seen {_describeRoom()}: its relationship is Flee, and a flee was already under way, so nothing more was sent", aRun: true);
                break;
            case FleeOutcome.Escaping:
                Running($"{what} seen {_describeRoom()}: its relationship is Flee, and a hang-up or wimpy jump had just gone out, so no run was started", aRun: false);
                break;
            case FleeOutcome.Follower:
                NoRun("follower", what, "this character is following a party leader, and a follower does not run off alone", fightBack: true);
                break;
            case FleeOutcome.NoEngine:
                NoRun("no-engine", what, "no walk or loop is running, so there is no route to run back along", fightBack: true);
                break;
            case FleeOutcome.Paused:
                NoRun("paused", what, "the walk or loop is paused, which counts as idle", fightBack: true);
                break;
            default:
                // HealthManager has said which route failed in its own line.
                NoRun("no-route", what, "no way out of the room could be found", fightBack: true);
                break;
        }
    }

    // Asked by a run that waited for a move in flight to land: is a Flee monster on
    // the roster of the room the move landed in?
    private bool FleeMonsterStillHere()
    {
        if (_classifier.Current is not { } roster) return false;
        ReadRoster(roster, out _, out List<RoomEntity> flees);
        return flees.Count > 0;
    }

    // Our exit command is on the wire and the line has not dropped yet: whatever
    // the roster does in that moment sends nothing more.
    private bool DropAwaited()
    {
        if (_hungUpAt is not { } at) return false;
        if (_now() - at < DropLimit) return true;
        _hungUpAt = null;
        _log?.Warn(HangupLogCategory,
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

    // No run was started. Said once per sighting for each reason, like Hold.
    // fightBack is whether the monster is then fought back when it attacks
    // (NoAnswerComing): it is, wherever the client itself is not going to run.
    // Self-defence has gates of its own, which the line names: it needs Auto-Combat,
    // and a walk-to in motion goes on walking and does not turn to fight.
    private void NoRun(string key, string what, string why, bool fightBack)
    {
        _noRunComing = fightBack;
        _answeredByRun = false;
        if (_fleeHeldFor == key) return;
        _fleeHeldFor = key;
        RecordFlee($"{what} seen {_describeRoom()}: its relationship is Flee, but {why}"
            + (fightBack ? "; it is fought back if it attacks (with Auto-Combat on, and not by a walk-to in motion)" : ""));
    }

    // A run, or an escape that stands in for one, is the answer: nothing is fought
    // while it lasts. aRun marks a flee, which is asked after once it is over
    // (NoAnswerComing); an escape ends the stay in the room one way or the other.
    private void Running(string what, bool aRun)
    {
        _noRunComing = false;
        _answeredByRun = aRun;
        _fleeHeldFor = null;
        RecordFlee(what);
    }

    private void EndSighting()
    {
        EndHangupSighting();
        EndFleeSighting();
    }

    private void EndHangupSighting()
    {
        _answered.Clear();
        _heldFor = null;
        _escapeAnswered = false;
    }

    private void EndFleeSighting()
    {
        _fleeAnswered.Clear();
        _fleeHeldFor = null;
        _noRunComing = false;
        _answeredByRun = false;
    }

    // The first of these monsters not yet answered in this sighting. One that left
    // and came back inside the same display is seen afresh.
    private static RoomEntity? FirstUnanswered(List<RoomEntity> seen, HashSet<int> answered)
    {
        answered.IntersectWith(seen.Select(e => e.MonsterNumber!.Value));
        foreach (RoomEntity e in seen)
            if (!answered.Contains(e.MonsterNumber!.Value)) return e;
        return null;
    }

    private void ReadRoster(RoomEntitiesObservation obs, out List<RoomEntity> hangups, out List<RoomEntity> flees)
    {
        hangups = new();
        flees = new();
        foreach (RoomEntity e in obs.Entities)
        {
            if (e.Kind != EntityKind.Monster || e.MonsterNumber is not int number) continue;
            switch (RelationshipOf(number))
            {
                case MonsterRelationship.Hangup: hangups.Add(e); break;
                case MonsterRelationship.Flee: flees.Add(e); break;
            }
        }
    }

    // A record that can't be read (no active set, a malformed override file) is no
    // instruction to hang up or run, and a throw here would stop the combat handlers
    // queued behind this one. Said once: it would otherwise repeat on every roster.
    private MonsterRelationship? RelationshipOf(int number)
    {
        try { return _resolveOverlay(number)?.Relationship; }
        catch (Exception ex)
        {
            if (!_unreadableWarned)
            {
                _unreadableWarned = true;
                _log?.Warn(HangupLogCategory,
                    $"monster #{number}'s relationship could not be read ({ex.Message}); it is treated as neither Hangup nor Flee. Further failures are not logged");
            }
            return null;
        }
    }

    private void Record(string what)
    {
        LastHangupSighting = $"{_now().ToLocalTime():HH:mm:ss} {what}";
        _log?.Info(HangupLogCategory, what);
    }

    private void RecordFlee(string what)
    {
        LastFleeSighting = $"{_now().ToLocalTime():HH:mm:ss} {what}";
        _log?.Info(FleeLogCategory, what);
    }

    public void Dispose()
    {
        _holdTicks++;
        _classifier.EntitiesObserved -= OnEntitiesObserved;
    }
}

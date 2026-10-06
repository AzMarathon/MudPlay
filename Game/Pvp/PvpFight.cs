using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Pvp;

// A fight with another player: the Attack and Chase and attack responses, and a
// leader's @kill aimed at a player.
//
// One fight at a time. While it lasts the running walk or loop is stopped and the
// combat engine stands down, so its own pick can't replace the attack on the
// player; both come back when the fight is over.
//
// The attack is a PvP combat spell while its conditions hold, else the combat
// profile's own attack. A PvP between-round spell is cast at the start and again
// each time its duration runs out.
//
// The chase: when they leave we step the way they went at once. Arriving without
// sight of them we track (when that is on) and otherwise guess: at a crossroads
// with a door that just opens, behind the door first, a few rooms in and back;
// else the way they were heading, or the room's only other way out. After the set
// number of rooms without seeing them, or the set wait with no way to follow, the
// chase is over and the stopped run is picked up again.
//
// An attack on a player who hasn't attacked us is an evil deed, and the game
// refuses it while evil warnings are on (GAME_MECHANICS "How your alignment moves
// during play"). Only the PvP tab's opt-in lets the client switch them off. They
// go back on once we are back at what the fight interrupted and it has stayed
// quiet, not the moment the fight ends: the same player may be back in a moment.
public sealed class PvpFight : IDisposable
{
    public const string LogCategory = "PvP";

    private static readonly TimeSpan Round = TimeSpan.FromSeconds(5);

    // How soon after our attack a refusal line is taken to be its answer.
    private static readonly TimeSpan RefusalWindow = TimeSpan.FromSeconds(3);

    // A re-attack on *Combat Off* this soon after the last one would be a burst.
    private static readonly TimeSpan ReattackQuiet = TimeSpan.FromMilliseconds(1500);

    // How long `track` gets to answer before the chase falls back to a guess.
    private static readonly TimeSpan TrackAnswerWait = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan WarningsBackCheck = TimeSpan.FromSeconds(15);

    private readonly RoomEntityClassifier _classifier;
    private readonly Func<bool> _pvpEnabled;
    private readonly Func<string, bool> _inParty;
    private readonly Func<PvpSettings> _readSettings;
    private readonly Func<string, string> _attackCommandFor;
    private readonly Action<string> _send;
    private readonly Func<string, string, bool> _cast;
    private readonly Func<string, PvpSpellInfo?> _spellInfo;
    private readonly Func<int, bool> _manaMeets;
    private readonly Func<Direction, bool> _stepToward;
    private readonly Func<IReadOnlyCollection<PvpChaseExit>> _exitsHere;
    private readonly Func<string, Action> _suspendEngines;
    private readonly Func<bool> _backOnTask;
    private readonly Action<TimeSpan, Action> _schedule;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;
    private readonly List<IDisposable> _subs = new();

    private string? _target;
    private bool _chase;
    private bool _needsAttack;
    private Action? _resumeEngines;

    // The PvP spells in this fight, and the attack last sent.
    private sealed class SpellUse
    {
        public required string Code { get; init; }
        public required PvpSpellSlot Slot { get; init; }
        public PvpSpellInfo? Info { get; init; }
        public int Casts { get; set; }
        public DateTimeOffset NextDue { get; set; }
        public bool Spent { get; set; }
    }
    private readonly List<SpellUse> _spells = new();
    private SpellUse? _attackSpell;
    private string? _attackSent;
    private DateTimeOffset _lastAttackAt = DateTimeOffset.MinValue;
    private int _fight;

    // The chase. _heading is the way they were last known to be going; _stepPending
    // is our own step after them still on its way; _waiting is standing with no way
    // to follow; _awaitingTrack is a `track` not answered yet.
    private Direction? _heading;
    private int _unseenRooms;
    private bool _stepPending;
    private bool _waiting;
    private bool _awaitingTrack;
    private int _seek;

    // Looking behind a door at a crossroads: the steps taken since the junction,
    // the heading we had there, and the ways already tried from it. _wayBack is
    // the walk back to the junction once the look has gone far enough.
    private List<Direction>? _probeTrail;
    private Direction? _junctionHeading;
    private readonly HashSet<Direction> _triedHere = new();
    private Stack<Direction>? _wayBack;

    // Evil warnings we switched off and haven't switched back on. It outlives the
    // fight that did it, so a second fight doesn't send `set warning off` again.
    private bool _warningsOff;
    private DateTimeOffset _fightEndedAt = DateTimeOffset.MinValue;
    private bool _warningsCheckRunning;

    // Order of the *Combat …* lines: an Off answered by an Engaged is our own
    // attack being re-issued, not the fight ending.
    private int _statusSeq;
    private int _engagedSeq;

    public bool IsActive => _target is not null;

    // Told what happened, for the terminal notice.
    public event Action<string>? Reported;

    // A new fight began with this player.
    public event Action<string>? Started;

    // A fight started or ended.
    public event Action? ActiveChanged;

    // The last thing reported, for the bug report.
    public string LastReport { get; private set; } = "(none this session)";

    // spellInfo: a cast code's kind and duration, null when it isn't a spell we know.
    // manaMeets: whether our mana meets a slot's floor. backOnTask: whatever the
    // fight interrupted is running again (true when it interrupted nothing).
    public PvpFight(
        MessageRouter router,
        RoomEntityClassifier classifier,
        Func<bool> pvpEnabled,
        Func<string, bool> inParty,
        Func<PvpSettings> readSettings,
        Func<string, string> attackCommandFor,
        Action<string> send,
        Func<string, string, bool> cast,
        Func<string, PvpSpellInfo?> spellInfo,
        Func<int, bool> manaMeets,
        Func<Direction, bool> stepToward,
        Func<IReadOnlyCollection<PvpChaseExit>> exitsHere,
        Func<string, Action> suspendEngines,
        Func<bool> backOnTask,
        Action<TimeSpan, Action> schedule,
        LogService? log = null,
        Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        _classifier = classifier;
        _pvpEnabled = pvpEnabled;
        _inParty = inParty;
        _readSettings = readSettings;
        _attackCommandFor = attackCommandFor;
        _send = send;
        _cast = cast;
        _spellInfo = spellInfo;
        _manaMeets = manaMeets;
        _stepToward = stepToward;
        _exitsHere = exitsHere;
        _suspendEngines = suspendEngines;
        _backOnTask = backOnTask;
        _schedule = schedule;
        _log = log;
        _now = now ?? (() => DateTimeOffset.Now);

        _classifier.EntitiesObserved += OnEntitiesObserved;
        _subs.Add(router.Subscribe(KnownPatterns.CombatStatus, OnCombatStatus));
        _subs.Add(router.Subscribe(KnownPatterns.TargetNotHere, OnTargetNotHere));
        _subs.Add(router.Subscribe(KnownPatterns.EvilWarningsRefusal, OnWarningsRefusal));
        _subs.Add(router.Subscribe(KnownPatterns.PlayerAttackRefused, OnAttackRefused));
        _subs.Add(router.Subscribe(KnownPatterns.TrackWentFromHere, OnTrackWent));
        _subs.Add(router.Subscribe(KnownPatterns.TrackFailed, OnTrackFailed));
    }

    // Start (or, for the same player, carry on) a fight. False when it can't be:
    // PvP is off, they are in our party, or another fight is under way.
    // warningsOffFirst switches evil warnings off before the first attack.
    public bool Engage(string given, bool chase, string why, bool warningsOffFirst = false)
    {
        if (!_pvpEnabled() || given.Length == 0 || _inParty(given)) return false;
        if (_target is { } current)
        {
            if (!string.Equals(current, given, StringComparison.OrdinalIgnoreCase)) return false;
            _chase |= chase;
            return true;
        }

        PvpSettings settings = _readSettings();
        _fight++;
        _target = given;
        _chase = chase;
        _needsAttack = false;
        ResetChase();
        LoadSpells(settings);

        _resumeEngines = _suspendEngines($"PvP: fighting {given}");
        Report($"{why}: attacking {given}{(chase ? ", and chasing if they run" : "")}");
        ActiveChanged?.Invoke();
        Started?.Invoke(given);

        if (warningsOffFirst && !_warningsOff)
        {
            _warningsOff = true;
            _log?.Warn(LogCategory, "switching evil warnings off before attacking, as set for @kill");
            _send("set warning off");
        }

        // Sent whether or not they are listed: an attacker can be one we can't see,
        // and the game's own "You do not see …" ends the fight if they aren't here.
        if (IsHere(given)) CastDueSpell(given);
        Attack(evenIfUnseen: true);

        int fight = _fight;
        if (_target is not null) _schedule(Round, () => RoundTick(fight));
        return _target is not null;
    }

    // A leader's @kill naming a player who is in the room with us. For us it is an
    // attack on someone who hasn't attacked us, whatever they did to the leader.
    public bool EngageOnOrder(string name)
    {
        string given = PlayerObservation.SplitName(name).Given;
        return IsHere(given)
            && Engage(given, chase: false, "ordered by @kill",
                warningsOffFirst: _readSettings().KillOrderTurnsOffEvilWarnings);
    }

    private void LoadSpells(PvpSettings settings)
    {
        _spells.Clear();
        _attackSpell = null;
        _attackSent = null;
        foreach (PvpSpellSlot slot in new[] { settings.Spell1, settings.Spell2 })
        {
            if (string.IsNullOrWhiteSpace(slot.SpellName)) continue;
            string code = slot.SpellName.Trim();
            PvpSpellInfo? info = _spellInfo(code);
            if (info is { MonsterOnly: true })
            {
                _log?.Warn(LogCategory, $"PvP spell {code} only works on monsters: not used");
                continue;
            }
            _spells.Add(new SpellUse { Code = code, Slot = slot, Info = info, NextDue = DateTimeOffset.MinValue });
        }
    }

    // End the fight. resume false leaves the stopped walk or loop stopped (we died).
    // connected false is the line having dropped: nothing more can be sent.
    public void Stop(string reason, bool resume = true, bool connected = true)
    {
        if (_target is not { } target) return;
        _fight++;
        _target = null;
        _spells.Clear();
        _attackSpell = null;
        _attackSent = null;
        ResetChase();
        _fightEndedAt = _now();

        string warnings = "";
        if (_warningsOff && !connected)
        {
            _warningsOff = false;
            warnings = ". Evil warnings were switched off for it and are still off: `set warning on` puts them back";
        }

        Report($"fight with {target} over: {reason}{warnings}");
        Action? resumeEngines = _resumeEngines;
        _resumeEngines = null;
        if (resume) resumeEngines?.Invoke();
        if (_warningsOff) WatchForWarningsBack();
        ActiveChanged?.Invoke();
    }

    public string Describe() => (_target is { } target
        ? $"fighting {target}{(_chase ? " (chasing)" : "")}"
          + (_unseenRooms > 0 ? $", {_unseenRooms} room(s) without sight of them" : "")
          + (_waiting ? ", waiting with no way to follow" : "")
          + (_probeTrail is not null ? ", looking behind a door" : "")
          + (_attackSent is { } sent ? $", attack `{sent}`" : "")
        : "no fight")
        + (_warningsOff ? "; evil warnings off, to be switched back on" : "");

    private void ResetChase()
    {
        _heading = null;
        _unseenRooms = 0;
        _stepPending = false;
        _waiting = false;
        _awaitingTrack = false;
        _seek++;
        EndProbe();
    }

    private void EndProbe()
    {
        _probeTrail = null;
        _junctionHeading = null;
        _wayBack = null;
        _triedHere.Clear();
    }

    private void Attack(bool evenIfUnseen = false)
    {
        if (_target is not { } target) return;
        if (!evenIfUnseen && !IsHere(target))
        {
            _needsAttack = true;
            OutOfSight();
            return;
        }
        _needsAttack = false;
        _lastAttackAt = _now();
        _attackSent = AttackFor(target);
        _send(_attackSent);
    }

    // A PvP combat spell while its conditions hold (casts left, mana above its
    // floor), else the combat profile's attack.
    private string AttackFor(string target)
    {
        _attackSpell = null;
        foreach (SpellUse use in _spells)
        {
            if (use.Info is not { BetweenRound: false }) continue;
            if (use.Slot.MaxCasts is { } cap && use.Casts >= cap) continue;
            if (!_manaMeets(use.Slot.MinManaPerCast)) continue;
            _attackSpell = use;
            return $"{use.Code} {target}";
        }
        return _attackCommandFor(target);
    }

    // The first between-round PvP spell that is due: at the start of the fight, and
    // again each time its duration has run out. One a round, since the game takes
    // only one between-round cast a round.
    private bool CastDueSpell(string target)
    {
        DateTimeOffset now = _now();
        foreach (SpellUse use in _spells)
        {
            if (use.Info is { BetweenRound: false } || use.Spent || now < use.NextDue) continue;
            if (use.Slot.MaxCasts is { } cap && use.Casts >= cap) continue;
            if (!_manaMeets(use.Slot.MinManaPerCast)) continue;
            if (!_cast(use.Code, target)) return false;

            use.Casts++;
            // A spell with no duration, or one we know nothing about, is cast once.
            if (use.Info is { Duration: var lasts } && lasts > TimeSpan.Zero) use.NextDue = now + lasts;
            else use.Spent = true;
            _log?.Info(LogCategory, $"PvP spell {use.Code} cast at {target}");
            return true;
        }
        return false;
    }

    // Once a round while the fight lasts: a due between-round spell, and the attack
    // again when that cast broke it or when the pick has changed (mana fell under a
    // combat spell's floor, or its casts ran out).
    private void RoundTick(int fight)
    {
        if (fight != _fight || _target is not { } target) return;
        if (IsHere(target) && !_stepPending)
        {
            if (_attackSpell is { } spent) spent.Casts++;
            bool cast = CastDueSpell(target);
            if (cast || AttackFor(target) != _attackSent) Attack();
        }
        _schedule(Round, () => RoundTick(fight));
    }

    private void OnEntitiesObserved(RoomEntitiesObservation obs)
    {
        if (_target is not { } target) return;
        if (IsHere(target))
        {
            _unseenRooms = 0;
            _waiting = false;
            _awaitingTrack = false;
            _seek++;
            EndProbe();
            if (_needsAttack && !_stepPending) Attack();
            return;
        }
        // A departure line is handled where it names the way they went. While our
        // own step is on its way the roster is the room we're leaving.
        if (obs.Source == RoomObservationSource.Departure || _stepPending) return;
        OutOfSight();
    }

    // ----- the chase -----------------------------------------------------

    // They walked out of the room, from RoomDepartureWatcher.
    public void NotePlayerDeparted(string given, string directionWord)
    {
        if (_target is not { } target || !string.Equals(target, given, StringComparison.OrdinalIgnoreCase))
            return;
        if (!_chase)
        {
            Stop($"{target} left");
            return;
        }
        _needsAttack = true;
        if (ParseDirection(directionWord) is { } direction)
        {
            _log?.Info(LogCategory, $"{target} left {direction.ToLongName()}: following");
            Follow(direction);
        }
        else
        {
            OutOfSight();
        }
    }

    // Our step after them arrived (the walker finished). Not seeing them there is
    // the next chase decision.
    public void NoteStepLanded()
    {
        if (!_stepPending || _target is not { } target) return;
        _stepPending = false;
        if (IsHere(target))
        {
            _unseenRooms = 0;
            EndProbe();
            if (_needsAttack) Attack();
            return;
        }
        if (_wayBack is not null)
        {
            StepBack();
            return;
        }
        OutOfSight();
    }

    // Our step after them could not be made.
    public void NoteStepFailed()
    {
        if (!_stepPending || _target is null) return;
        _stepPending = false;
        _wayBack = null;
        Wait();
    }

    // They aren't in the room and we aren't already on our way after them. A plain
    // attack ends there. A chase tracks them when that is on, and guesses otherwise.
    private void OutOfSight()
    {
        if (_target is not { } target) return;
        if (!_chase)
        {
            Stop($"{target} is gone");
            return;
        }
        if (_stepPending || _waiting || _awaitingTrack) return;

        if (_readSettings().TrackEnemies)
        {
            SendTrack();
            return;
        }
        Guess();
    }

    private void SendTrack()
    {
        if (_target is not { } target) return;
        _awaitingTrack = true;
        int seek = ++_seek;
        _send($"track {target}");
        _schedule(TrackAnswerWait, () =>
        {
            if (seek != _seek || !_awaitingTrack) return;
            _awaitingTrack = false;
            Guess();
        });
    }

    private void OnTrackWent(MatchResult match)
    {
        if (_target is not { } target || !_chase || _stepPending || IsHere(target)) return;
        if (match.Groups.Count < 2
            || !string.Equals(match.Groups[0], target, StringComparison.OrdinalIgnoreCase)
            || ParseDirection(match.Groups[1]) is not { } direction)
            return;
        _awaitingTrack = false;
        _log?.Info(LogCategory, $"tracked {target} {direction.ToLongName()}: following");
        Follow(direction);
    }

    private void OnTrackFailed(MatchResult match)
    {
        if (_target is null || !_awaitingTrack) return;
        _awaitingTrack = false;
        Guess();
    }

    // No word on which way they went. At a crossroads with a door that just opens,
    // look behind the door first, a few rooms in, then come back for the other
    // ways. Otherwise carry on the way they were heading, or take the room's only
    // way out other than the one we came in by.
    private void Guess()
    {
        if (_target is null) return;
        PvpSettings settings = _readSettings();
        if (!settings.ChaseGuessDirection)
        {
            Wait();
            return;
        }

        if (_probeTrail is { } trail && trail.Count >= Math.Max(1, settings.ChaseDoorRooms))
        {
            ReturnToJunction(trail);
            return;
        }

        IReadOnlyCollection<PvpChaseExit> exits = _exitsHere();
        Direction? back = (_probeTrail is { Count: > 0 } t ? t[^1] : _heading)?.Opposite();
        // What was tried belongs to the junction, not to the rooms behind its door.
        bool atJunction = _probeTrail is null;
        List<PvpChaseExit> others = exits
            .Where(e => e.Way != back && !(atJunction && _triedHere.Contains(e.Way)))
            .ToList();

        Direction? pick = null;
        bool behindADoor = false;
        if (_probeTrail is null && others.Count > 1 && others.FirstOrDefault(e => e.Door) is { Door: true } door)
        {
            pick = door.Way;
            behindADoor = true;
        }
        else if (_heading is { } heading && others.Any(e => e.Way == heading))
        {
            pick = heading;
        }
        else if (others.Count == 1)
        {
            pick = others[0].Way;
        }

        if (pick is not { } way)
        {
            if (_probeTrail is { Count: > 0 } deadEnd) ReturnToJunction(deadEnd);
            else Wait();
            return;
        }

        if (behindADoor)
        {
            _log?.Info(LogCategory, $"no sight of {_target}: looking behind the door {way.ToLongName()} first");
            _junctionHeading = _heading;
            _triedHere.Add(way);
            _probeTrail = new List<Direction>();
        }
        else
        {
            _log?.Info(LogCategory, $"no sight of {_target}: guessing {way.ToLongName()}");
        }
        Follow(way);
    }

    private void Follow(Direction direction)
    {
        if (_target is not { } target) return;
        int limit = Math.Max(1, _readSettings().ChaseRoomsUnseen);
        if (_unseenRooms >= limit)
        {
            Stop($"lost {target} after {_unseenRooms} room(s) without sight of them");
            return;
        }

        _heading = direction;
        _waiting = false;
        _seek++;
        // Moving on from a junction for good: what was tried there no longer matters.
        if (_probeTrail is null) _triedHere.Clear();
        if (!_stepToward(direction))
        {
            Wait();
            return;
        }
        _probeTrail?.Add(direction);
        _stepPending = true;
        _unseenRooms++;
    }

    // The look behind the door found nothing: walk back to the junction, the way we
    // came, and guess again there without the door.
    private void ReturnToJunction(List<Direction> trail)
    {
        _log?.Info(LogCategory, $"nothing behind the door after {trail.Count} room(s): going back to try another way");
        _wayBack = new Stack<Direction>();
        foreach (Direction step in trail) _wayBack.Push(step.Opposite());
        _probeTrail = null;
        StepBack();
    }

    private void StepBack()
    {
        if (_wayBack is not { } back) return;
        if (back.Count == 0)
        {
            _wayBack = null;
            _heading = _junctionHeading;
            _junctionHeading = null;
            Guess();
            return;
        }
        Direction step = back.Pop();
        if (!_stepToward(step))
        {
            _wayBack = null;
            Wait();
            return;
        }
        _stepPending = true;
    }

    // Nothing to follow. Stand for the set time in case they come back into sight,
    // tracking again on the set interval when that is on.
    private void Wait()
    {
        if (_target is not { } target || _waiting) return;
        _waiting = true;
        int seek = ++_seek;
        PvpSettings settings = _readSettings();
        TimeSpan wait = TimeSpan.FromSeconds(Math.Max(0, settings.ChaseWaitSeconds));
        _log?.Info(LogCategory, $"no way to follow {target}: waiting {wait.TotalSeconds:0}s");
        _schedule(wait, () =>
        {
            if (seek == _seek && _waiting) Stop($"lost {target}");
        });

        if (!settings.TrackEnemies) return;
        TimeSpan every = TimeSpan.FromSeconds(Math.Max(5, settings.TrackEnemiesEverySeconds));
        if (every < wait) _schedule(every, () => TrackWhileWaiting(seek, every));
    }

    private void TrackWhileWaiting(int seek, TimeSpan every)
    {
        if (seek != _seek || !_waiting || _target is not { } target) return;
        _send($"track {target}");
        _schedule(every, () => TrackWhileWaiting(seek, every));
    }

    // The words a departure line and a `track` answer use for a direction: the
    // long names, plus the adverbs the vertical pair takes ("just left upwards").
    private static Direction? ParseDirection(string word)
    {
        string name = word.Trim().ToLowerInvariant() switch
        {
            "upwards" or "above" => "up",
            "downwards" or "below" => "down",
            string other => other,
        };
        return DirectionExtensions.TryFromLongName(name, out Direction direction) ? direction : null;
    }

    // ----- the game's answers ---------------------------------------------

    // Combat dropped with them still here (a cast of ours broke it): attack again.
    private void OnCombatStatus(MatchResult match)
    {
        if (match.Groups.Count == 0) return;
        int seq = ++_statusSeq;
        if (!string.Equals(match.Groups[0], "Off", StringComparison.OrdinalIgnoreCase))
        {
            _engagedSeq = seq;
            return;
        }
        if (_target is not { } target || !IsHere(target)) return;

        TimeSpan since = _now() - _lastAttackAt;
        if (since >= ReattackQuiet)
        {
            Attack();
            return;
        }
        // Too soon after our own attack to send another. If no Engaged follows this
        // Off, it was the end of them (or of the fight): look again once the quiet
        // is over, or a finished fight would stand with nothing left to end it.
        int fight = _fight;
        _schedule(ReattackQuiet - since, () =>
        {
            if (fight == _fight && _engagedSeq < seq) Attack(evenIfUnseen: true);
        });
    }

    private void OnTargetNotHere(MatchResult match)
    {
        if (_target is not { } target || match.Groups.Count == 0) return;
        if (!string.Equals(PlayerObservation.SplitName(match.Groups[0]).Given, target,
                StringComparison.OrdinalIgnoreCase))
            return;
        _needsAttack = true;
        OutOfSight();
    }

    private void OnWarningsRefusal(MatchResult match)
    {
        if (_target is null || _now() - _lastAttackAt > RefusalWindow) return;
        if (_warningsOff || !_readSettings().TurnOffEvilWarningsToAttack)
        {
            Stop("the game refused the attack: evil warnings are on");
            return;
        }
        _warningsOff = true;
        _log?.Warn(LogCategory, "attack refused for evil warnings: switching them off");
        _send("set warning off");
        Attack();
    }

    private void OnAttackRefused(MatchResult match)
    {
        if (_target is null || _now() - _lastAttackAt > RefusalWindow) return;
        Stop($"the game refused the attack: {match.Text.Trim()}");
    }

    // ----- evil warnings back on --------------------------------------------

    // Not at the end of the fight: once what it interrupted is running again and
    // no fight has started for a while.
    private void WatchForWarningsBack()
    {
        if (_warningsCheckRunning) return;
        _warningsCheckRunning = true;
        _schedule(WarningsBackCheck, CheckWarningsBack);
    }

    private void CheckWarningsBack()
    {
        _warningsCheckRunning = false;
        if (!_warningsOff) return;
        TimeSpan quiet = TimeSpan.FromSeconds(Math.Max(0, _readSettings().WarningsBackAfterSeconds));
        if (_target is null && _now() - _fightEndedAt >= quiet && _backOnTask())
        {
            _warningsOff = false;
            _send("set warning on");
            Report("back on task: evil warnings switched back on");
            return;
        }
        WatchForWarningsBack();
    }

    private bool IsHere(string given)
    {
        if (_classifier.Current is not { } obs) return false;
        foreach (RoomEntity e in obs.Entities)
            if (e.Kind == EntityKind.Player
                && string.Equals(PlayerObservation.SplitName(e.ResolvedName).Given, given,
                       StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private void Report(string what)
    {
        LastReport = $"{_now().ToLocalTime():HH:mm:ss} {what}";
        _log?.Warn(LogCategory, what);
        Reported?.Invoke(what);
    }

    public void Dispose()
    {
        _classifier.EntitiesObserved -= OnEntitiesObserved;
        foreach (IDisposable sub in _subs) sub.Dispose();
    }
}

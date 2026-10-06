using MudPlay.Game.Combat;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Pvp;

// A fight with another player: the Attack and Chase and attack responses, hitting
// back at one who attacked us, and a leader's @kill aimed at a player.
//
// One fight at a time. While it lasts the running walk or loop is stopped and the
// combat engine stands down, so its own pick can't replace the attack on the
// player; both come back when the fight is over. The PvP spells go out once each,
// a round apart, and the normal attack is put back after them.
//
// An attack on a player who hasn't attacked us is an evil deed, and the game
// refuses it while evil warnings are on (GAME_MECHANICS "How your alignment moves
// during play"). Only the PvP tab's opt-in lets the client switch them off, and it
// switches them back on when the fight ends. Hitting back needs neither.
public sealed class PvpFight : IDisposable
{
    public const string LogCategory = "PvP";

    private static readonly TimeSpan Round = TimeSpan.FromSeconds(5);

    // How soon after our attack a refusal line is taken to be its answer.
    private static readonly TimeSpan RefusalWindow = TimeSpan.FromSeconds(3);

    // A re-attack on *Combat Off* this soon after the last one would be a burst.
    private static readonly TimeSpan ReattackQuiet = TimeSpan.FromMilliseconds(1500);

    // How long a chase goes on without sight of the player before it is given up.
    public static readonly TimeSpan ChaseGiveUp = TimeSpan.FromMinutes(3);

    // Without tracking, how long to wait in the room we followed them into.
    private static readonly TimeSpan LostSettle = TimeSpan.FromSeconds(20);

    private readonly RoomEntityClassifier _classifier;
    private readonly Func<bool> _pvpEnabled;
    private readonly Func<string, bool> _inParty;
    private readonly Func<PvpSettings> _readSettings;
    private readonly Func<string, string> _attackCommandFor;
    private readonly Action<string> _send;
    private readonly Func<string, string, bool> _cast;
    private readonly Func<string, bool> _stepToward;
    private readonly Func<string, Action> _suspendEngines;
    private readonly Action<TimeSpan, Action> _schedule;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;
    private readonly List<IDisposable> _subs = new();

    private string? _target;
    private bool _chase;
    private bool _warningsOff;
    private bool _needsAttack;
    private bool _spellsRunning;
    private Queue<string> _spellsOwed = new();
    private Action? _resumeEngines;
    private DateTimeOffset _lastAttackAt = DateTimeOffset.MinValue;
    private DateTimeOffset? _lostAt;
    private bool _seeking;
    private int _fight;

    // Order of the *Combat …* lines: an Off answered by an Engaged is our own
    // attack being re-issued, not the fight ending.
    private int _statusSeq;
    private int _engagedSeq;

    public bool IsActive => _target is not null;

    // Told what happened, for the terminal notice.
    public event Action<string>? Reported;

    // The last thing reported, for the bug report.
    public string LastReport { get; private set; } = "(none this session)";

    public PvpFight(
        MessageRouter router,
        RoomEntityClassifier classifier,
        Func<bool> pvpEnabled,
        Func<string, bool> inParty,
        Func<PvpSettings> readSettings,
        Func<string, string> attackCommandFor,
        Action<string> send,
        Func<string, string, bool> cast,
        Func<string, bool> stepToward,
        Func<string, Action> suspendEngines,
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
        _stepToward = stepToward;
        _suspendEngines = suspendEngines;
        _schedule = schedule;
        _log = log;
        _now = now ?? (() => DateTimeOffset.Now);

        _classifier.EntitiesObserved += OnEntitiesObserved;
        _subs.Add(router.Subscribe(KnownPatterns.CombatStatus, OnCombatStatus));
        _subs.Add(router.Subscribe(KnownPatterns.TargetNotHere, OnTargetNotHere));
        _subs.Add(router.Subscribe(KnownPatterns.EvilWarningsRefusal, OnWarningsRefusal));
        _subs.Add(router.Subscribe(KnownPatterns.PlayerAttackRefused, OnAttackRefused));
        _subs.Add(router.Subscribe(KnownPatterns.TrackWentFromHere, OnTrackWent));
    }

    // Start (or, for the same player, carry on) a fight. False when it can't be:
    // PvP is off, they are in our party, or another fight is under way.
    public bool Engage(string given, bool chase, string why)
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
        _warningsOff = false;
        _needsAttack = false;
        _lostAt = null;
        _seeking = false;
        _spellsOwed = new Queue<string>();
        foreach (string? code in new[] { settings.PvpSpell1, settings.PvpSpell2 })
            if (!string.IsNullOrWhiteSpace(code)) _spellsOwed.Enqueue(code.Trim());

        _resumeEngines = _suspendEngines($"PvP: fighting {given}");
        Report($"{why}: attacking {given}{(chase ? ", and chasing if they run" : "")}");

        // Sent whether or not they are listed: an attacker can be one we can't see,
        // and the game's own "You do not see …" ends the fight if they aren't here.
        Attack(evenIfUnseen: true);
        if (_target is not null && _spellsOwed.Count > 0)
        {
            _spellsRunning = true;
            int fight = _fight;
            _schedule(TimeSpan.Zero, () => CastNextSpell(fight));
        }
        return _target is not null;
    }

    // A leader's @kill naming a player who is in the room with us.
    public bool EngageOnOrder(string name)
    {
        string given = PlayerObservation.SplitName(name).Given;
        return IsHere(given) && Engage(given, chase: false, "ordered by @kill");
    }

    // End the fight. resume false leaves the stopped walk or loop stopped (we died).
    // connected false is the line having dropped: nothing more can be sent.
    public void Stop(string reason, bool resume = true, bool connected = true)
    {
        if (_target is not { } target) return;
        _fight++;
        _target = null;
        _spellsRunning = false;
        _spellsOwed.Clear();
        _lostAt = null;
        _seeking = false;

        string warnings = "";
        if (_warningsOff)
        {
            _warningsOff = false;
            if (connected)
            {
                _send("set warning on");
                _log?.Info(LogCategory, "evil warnings switched back on");
            }
            else
            {
                warnings = ". Evil warnings were switched off for it and are still off: `set warning on` puts them back";
            }
        }

        Report($"fight with {target} over: {reason}{warnings}");
        Action? resumeEngines = _resumeEngines;
        _resumeEngines = null;
        if (resume) resumeEngines?.Invoke();
    }

    public string Describe() => _target is { } target
        ? $"fighting {target}{(_chase ? " (chasing)" : "")}"
          + (_lostAt is { } lost ? $", out of sight for {(_now() - lost).TotalSeconds:0}s" : "")
          + (_warningsOff ? ", evil warnings off for it" : "")
          + (_spellsOwed.Count > 0 ? $", {_spellsOwed.Count} PvP spell(s) still to cast" : "")
        : "no fight";

    private void Attack(bool evenIfUnseen = false)
    {
        if (_target is not { } target) return;
        if (!evenIfUnseen && !IsHere(target))
        {
            _needsAttack = true;
            Lost();
            return;
        }
        _needsAttack = false;
        _lastAttackAt = _now();
        _send(_attackCommandFor(target));
    }

    // One PvP spell a round, then the normal attack again: a spell that takes the
    // round would be cut short by an attack sent straight after it.
    private void CastNextSpell(int fight)
    {
        if (fight != _fight || _target is not { } target) return;

        if (_spellsOwed.Count == 0)
        {
            _spellsRunning = false;
            Attack();
            return;
        }

        if (IsHere(target) && _cast(_spellsOwed.Peek(), target))
        {
            string code = _spellsOwed.Dequeue();
            _log?.Info(LogCategory, $"PvP spell {code} cast at {target}");
        }
        _schedule(Round, () => CastNextSpell(fight));
    }

    private void OnEntitiesObserved(RoomEntitiesObservation obs)
    {
        if (_target is not { } target) return;
        if (IsHere(target))
        {
            _lostAt = null;
            _seeking = false;
            if (_needsAttack) Attack();
            return;
        }
        // A departure line is handled where it names the way they went; any other
        // roster without them (a re-display, a new room) just starts the clock.
        if (obs.Source != RoomObservationSource.Departure) Lost();
    }

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
        _lostAt ??= _now();
        if (_stepToward(directionWord))
            _log?.Info(LogCategory, $"{target} left {directionWord}: following");
        else
            Lost();
    }

    // Out of sight with no way known. A plain attack ends there; a chase tracks them
    // when that is on, and otherwise waits a short while where it stands.
    private void Lost()
    {
        if (_target is not { } target) return;
        if (!_chase)
        {
            Stop($"{target} is gone");
            return;
        }
        if (_seeking) return;

        _seeking = true;
        _lostAt ??= _now();
        int fight = _fight;
        if (_readSettings().TrackEnemies)
        {
            _schedule(TimeSpan.Zero, () => Track(fight));
            return;
        }
        _schedule(LostSettle, () =>
        {
            if (fight == _fight && _seeking) Stop($"lost {target}");
        });
    }

    private void Track(int fight)
    {
        if (fight != _fight || !_seeking || _target is not { } target || _lostAt is not { } lost) return;
        if (_now() - lost >= ChaseGiveUp)
        {
            Stop($"lost {target}");
            return;
        }
        _send($"track {target}");
        TimeSpan every = TimeSpan.FromSeconds(Math.Max(5, _readSettings().TrackEnemiesEverySeconds));
        _schedule(every, () => Track(fight));
    }

    private void OnTrackWent(MatchResult match)
    {
        if (_target is not { } target || !_chase || !_seeking) return;
        if (match.Groups.Count < 2
            || !string.Equals(match.Groups[0], target, StringComparison.OrdinalIgnoreCase))
            return;
        _needsAttack = true;
        if (_stepToward(match.Groups[1]))
            _log?.Info(LogCategory, $"tracked {target} {match.Groups[1]}: following");
    }

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
        if (_target is not { } target || _spellsRunning || !IsHere(target)) return;

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
            if (fight == _fight && !_spellsRunning && _engagedSeq < seq) Attack(evenIfUnseen: true);
        });
    }

    private void OnTargetNotHere(MatchResult match)
    {
        if (_target is not { } target || match.Groups.Count == 0) return;
        if (!string.Equals(PlayerObservation.SplitName(match.Groups[0]).Given, target,
                StringComparison.OrdinalIgnoreCase))
            return;
        _needsAttack = true;
        Lost();
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
        _log?.Warn(LogCategory, "attack refused for evil warnings: switching them off for this fight");
        _send("set warning off");
        Attack();
    }

    private void OnAttackRefused(MatchResult match)
    {
        if (_target is null || _now() - _lastAttackAt > RefusalWindow) return;
        Stop($"the game refused the attack: {match.Text.Trim()}");
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

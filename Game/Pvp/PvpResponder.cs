using MudPlay.Game.Combat;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Pvp;

// Carries out the PvP response: what to do when an Enemy is in the room or a
// player we now count as an Enemy attacks us. The response is the player's own
// (Game Data → Players) or, without one, the PvP settings' general action.
//
// Friends and Neutrals never get here: a Neutral who attacks has already been
// marked Enemy by PvpAttackWatcher by the time its Attacked event arrives.
public sealed class PvpResponder : IDisposable
{
    public const string LogCategory = "PvP";

    // One response per player per encounter: how long a player we have just
    // answered is left alone, by what brought them up again.
    private static readonly TimeSpan SightQuiet = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AttackQuiet = TimeSpan.FromSeconds(10);

    // "Flee then hang up" keeps the stopped run away at least this long past the
    // hang-up, so it can't walk us back before the line drops.
    private static readonly TimeSpan PastHangup = TimeSpan.FromSeconds(60);

    private readonly RoomEntityClassifier _classifier;
    private readonly PvpAttackWatcher _attacks;
    private readonly PlayerDatabase _players;
    private readonly Func<bool> _pvpEnabled;
    private readonly Func<string, bool> _inParty;
    private readonly Func<PvpSettings> _readSettings;
    private readonly Func<string, bool> _hangUp;
    private readonly Func<string, int, TimeSpan, Action?, bool> _fleeRooms;
    private readonly Func<RoomRef, TimeSpan?, string, bool> _fleeTo;
    private readonly Func<string, bool, string, bool> _fight;
    private readonly Action<string> _sendGang;
    private readonly Func<string?> _roomName;
    private readonly Action<TimeSpan, Action> _schedule;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;

    private readonly Dictionary<string, DateTimeOffset> _answeredAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _gangToldAt = new(StringComparer.OrdinalIgnoreCase);
    private bool _hangupPending;
    private (TimeSpan Delay, bool EnterRealm)? _reconnect;

    // The last response taken, for the bug report.
    public string LastResponse { get; private set; } = "(none this session)";

    // Told what the response was, for the terminal notice.
    public event Action<string>? Responded;

    public PvpResponder(
        RoomEntityClassifier classifier,
        PvpAttackWatcher attacks,
        PlayerDatabase players,
        Func<bool> pvpEnabled,
        Func<string, bool> inParty,
        Func<PvpSettings> readSettings,
        Func<string, bool> hangUp,
        Func<string, int, TimeSpan, Action?, bool> fleeRooms,
        Func<RoomRef, TimeSpan?, string, bool> fleeTo,
        Func<string, bool, string, bool> fight,
        Action<string> sendGang,
        Func<string?> roomName,
        Action<TimeSpan, Action> schedule,
        LogService? log = null,
        Func<DateTimeOffset>? now = null)
    {
        _classifier = classifier;
        _attacks = attacks;
        _players = players;
        _pvpEnabled = pvpEnabled;
        _inParty = inParty;
        _readSettings = readSettings;
        _hangUp = hangUp;
        _fleeRooms = fleeRooms;
        _fleeTo = fleeTo;
        _fight = fight;
        _sendGang = sendGang;
        _roomName = roomName;
        _schedule = schedule;
        _log = log;
        _now = now ?? (() => DateTimeOffset.Now);

        _classifier.EntitiesObserved += OnEntitiesObserved;
        _attacks.Attacked += OnAttacked;
    }

    // The dial-back a PvP hang-up asked for, and whether it goes on into the realm
    // or stops at the menu. Read once, by the disconnect it caused.
    public (TimeSpan Delay, bool EnterRealm)? TakeReconnect()
    {
        (TimeSpan, bool)? reconnect = _reconnect;
        _reconnect = null;
        return reconnect;
    }

    // A fight with a player began (ours to start, or a leader's @kill).
    public void NoteWeAttack(string given) => TellGang(_readSettings(), given, "attacking", _now());

    private void OnAttacked(PvpAttack attack)
    {
        if (attack.Relationship != PlayerRelationship.Enemy) return;
        Respond(attack.Player, attacked: true);
    }

    private void OnEntitiesObserved(RoomEntitiesObservation obs)
    {
        if (EnemyOn(obs) is { } given) Respond(given, attacked: false);
    }

    // Whether this roster is the PvP response's to answer: the realm has PvP on and
    // an Enemy outside the party is on it. It reads nothing but the roster, so it
    // can be asked ahead of the response (MonsterRelationshipWatcher stands down on it)
    // without using up the response's quiet time. True inside that quiet time too:
    // an Enemy answered a moment ago is still the PvP actions' business.
    public bool IsAnswering(RoomEntitiesObservation obs) => EnemyOn(obs) is not null;

    // The first Enemy on the roster the response applies to, by given name.
    private string? EnemyOn(RoomEntitiesObservation obs)
    {
        if (!_pvpEnabled()) return null;
        foreach (RoomEntity e in obs.Entities)
        {
            if (e.Kind != EntityKind.Player) continue;
            string given = PlayerObservation.SplitName(e.ResolvedName).Given;
            if (given.Length == 0 || _inParty(given)) continue;
            if (_players.Find(given)?.Relationship != PlayerRelationship.Enemy) continue;
            return given;
        }
        return null;
    }

    private void Respond(string given, bool attacked)
    {
        if (!_pvpEnabled() || _hangupPending) return;

        DateTimeOffset now = _now();
        if (_answeredAt.TryGetValue(given, out DateTimeOffset last)
            && now - last < (attacked ? AttackQuiet : SightQuiet))
            return;
        _answeredAt[given] = now;

        PvpSettings settings = _readSettings();
        PvpAction action = _players.Find(given)?.PvpResponse ?? settings.Action;
        string why = attacked ? $"{given} attacked us" : $"{given} is here";

        switch (action)
        {
            case PvpAction.HangUp:
                TellGang(settings, given, Sighting(attacked), now);
                Report($"{why}: hanging up");
                HangUp(settings, why);
                break;

            case PvpAction.FleeThenHangUp:
            {
                TellGang(settings, given, Sighting(attacked), now);
                TimeSpan delay = TimeSpan.FromSeconds(Math.Max(0, settings.FleeHangupDelaySeconds));
                bool hungUp = false;
                void HangUpOnce()
                {
                    if (hungUp) return;
                    hungUp = true;
                    _hangupPending = false;
                    HangUp(settings, why);
                }

                // Running back along the walk or loop, the hang-up comes as soon as
                // the set number of rooms is behind us; the delay is the latest it
                // can be. At a flee room it is the delay.
                if (!StartFlee(settings, why, comeBackAfter: null, stayAway: delay + PastHangup, HangUpOnce))
                {
                    Report($"{why}: nowhere to flee, hanging up now");
                    HangUp(settings, why);
                    break;
                }
                Report($"{why}: fleeing, then hanging up");
                _hangupPending = true;
                _schedule(delay, HangUpOnce);
                break;
            }

            case PvpAction.Flee:
            {
                TellGang(settings, given, Sighting(attacked), now);
                TimeSpan away = TimeSpan.FromSeconds(Math.Max(0, settings.ComeBackAfterSeconds));
                Report(StartFlee(settings, why, comeBackAfter: away, stayAway: away, onRoomsLanded: null)
                    ? $"{why}: fleeing, back in {away.TotalSeconds:0}s"
                    : $"{why}: nowhere to flee (no walk or loop is running and no Flee to room is set)");
                break;
            }

            case PvpAction.Attack:
            case PvpAction.ChaseAttack:
                TellGang(settings, given, Sighting(attacked), now);
                if (!_fight(given, action == PvpAction.ChaseAttack, why))
                    _log?.Info(LogCategory, $"{why}: not attacked (another fight is under way)");
                break;

            default:
                _log?.Warn(LogCategory, $"{why}: the saved response ({(int)action}) isn't one of the choices, so nothing is done");
                break;
        }
    }

    // A Flee to room when one is set and can be reached; otherwise back along the
    // running walk or loop.
    private bool StartFlee(
        PvpSettings settings, string why, TimeSpan? comeBackAfter, TimeSpan stayAway, Action? onRoomsLanded)
    {
        if (settings.FleeTo is { } room && _fleeTo(room, comeBackAfter, why)) return true;
        return _fleeRooms(why, Math.Max(1, settings.RoomsToFlee), stayAway, onRoomsLanded);
    }

    private void HangUp(PvpSettings settings, string why)
    {
        _reconnect = settings.ReconnectAfterPvp
            ? (TimeSpan.FromMinutes(Math.Max(1, settings.ReconnectAfterPvpMinutes)), settings.ReconnectEntersRealm)
            : null;
        if (_hangUp(why)) return;
        _reconnect = null;
        _log?.Warn(LogCategory, $"{why}: the hang-up did not go out (hang-ups are disabled, or no exit command is set)");
    }

    private static string Sighting(bool attacked) => attacked ? "attacked me" : "is here";

    // One line per event the user ticked (seen, attacked us, we attack), and the
    // same line about the same player no sooner than the set time after the last.
    private void TellGang(PvpSettings settings, string given, string what, DateTimeOffset now)
    {
        if (!settings.NotifyGang) return;
        bool wanted = what switch
        {
            "is here" => settings.GangTellSeen,
            "attacked me" => settings.GangTellAttacked,
            _ => settings.GangTellWeAttack,
        };
        if (!wanted) return;

        string key = $"{given}|{what}";
        TimeSpan quiet = TimeSpan.FromSeconds(Math.Max(0, settings.GangRepeatSeconds));
        if (_gangToldAt.TryGetValue(key, out DateTimeOffset told) && now - told < quiet) return;
        _gangToldAt[key] = now;
        string where = _roomName() is { Length: > 0 } room ? $" at {room}" : "";
        _sendGang(what == "attacking" ? $"PvP: attacking {given}{where}" : $"PvP: {given} {what}{where}");
    }

    private void Report(string what)
    {
        LastResponse = $"{_now().ToLocalTime():HH:mm:ss} {what}";
        _log?.Warn(LogCategory, what);
        Responded?.Invoke(what);
    }

    public void Dispose()
    {
        _classifier.EntitiesObserved -= OnEntitiesObserved;
        _attacks.Attacked -= OnAttacked;
    }
}

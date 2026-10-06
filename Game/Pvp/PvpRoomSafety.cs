using MudPlay.Game.Combat;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Pvp;

// Room attacks on a realm with PvP on. A room attack or room debuff hits every
// player in the room who isn't in the caster's party (GAME_MECHANICS "Damage
// lines"), so this answers two questions about the room we stand in:
//
//   - RoomAttackHeldBy: who our own room attack would hit. The combat engine
//     holds its room spells, and breaks one already running, while it names
//     someone.
//   - LeaveRoomReason: whether another player is room-attacking in a room that
//     was theirs before we walked in. A running walk or loop then carries on to
//     its next room instead of fighting here.
//
// "Theirs" means they were listed when we arrived. A player who walks in on us
// and starts a room attack is attacking us, which is not this class's business.
public sealed class PvpRoomSafety : IDisposable
{
    public const string LogCategory = "PvP";

    // The monster count at which a Stock player of a room-attack class is taken to
    // be rooming. Stock prints nothing on entry to say so; Paradigm does.
    public const int StockRoomingMonsterCount = 3;

    private readonly RoomEntityClassifier _classifier;
    private readonly Func<bool> _pvpEnabled;
    private readonly Func<string, bool> _inParty;
    private readonly Func<string, string?> _classOf;
    private readonly Func<string, int?> _levelOf;
    private readonly Func<string, int?> _roomAttackFromLevel;
    private readonly Func<RealmType> _realm;
    private readonly Func<DateTimeOffset?> _lastMoveSentAt;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;
    private readonly IDisposable _poisedSub;
    private readonly IDisposable _announceSub;

    // Players listed when we arrived, and the move that arrival belongs to. A room
    // display after the same move (a re-display, a look) doesn't add to them.
    private readonly HashSet<string> _residents = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _residentsMoveAt;
    private bool _residentsRead;

    // When each player was last seen to commit a room attack. One from before our
    // last move was in another room.
    private readonly Dictionary<string, DateTimeOffset> _roomAttackSeenAt =
        new(StringComparer.OrdinalIgnoreCase);

    // When each player's arrival line was seen. One from before our last move was
    // an arrival into the room we have since left.
    private readonly Dictionary<string, DateTimeOffset> _arrivedAt =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, int?> _fromLevelByClass =
        new(StringComparer.OrdinalIgnoreCase);

    private string? _loggedHeldBy;
    private string? _loggedLeaveReason;

    // Another player's room attack was just seen, so the answer to LeaveRoomReason
    // may have changed with no room observation to carry it to the combat gate.
    public event Action? RoomAttackSeen;

    public PvpRoomSafety(
        MessageRouter router,
        RoomEntityClassifier classifier,
        Func<bool> pvpEnabled,
        Func<string, bool> inParty,
        Func<string, string?> classOf,
        Func<string, int?> levelOf,
        Func<string, int?> roomAttackFromLevel,
        Func<RealmType> realm,
        Func<DateTimeOffset?> lastMoveSentAt,
        LogService? log = null,
        Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(classifier);
        _classifier = classifier;
        _pvpEnabled = pvpEnabled;
        _inParty = inParty;
        _classOf = classOf;
        _levelOf = levelOf;
        _roomAttackFromLevel = roomAttackFromLevel;
        _realm = realm;
        _lastMoveSentAt = lastMoveSentAt;
        _log = log;
        _now = now ?? (() => DateTimeOffset.Now);

        _classifier.EntitiesObserved += OnEntitiesObserved;
        _poisedSub = router.Subscribe(KnownPatterns.PartyRoomPoised, OnRoomPoised);
        _announceSub = router.Subscribe(KnownPatterns.PartyAttackAnnounce, OnAttackAnnounce);
    }

    // The game data changed under us: a class's spells may differ in the new set.
    public void ResetClassCache() => _fromLevelByClass.Clear();

    // The first player here our room attack would hit and mustn't: anyone outside
    // our party, an Enemy included. What is done about an Enemy is their PvP
    // response, aimed at them; it is never left to a room spell. Null when nobody
    // is here, or PvP is off for the realm.
    public string? RoomAttackHeldBy()
    {
        string? heldBy = null;
        if (_pvpEnabled())
        {
            foreach (string given in PlayersHere())
            {
                if (!IsBystander(given)) continue;
                heldBy = given;
                break;
            }
        }

        if (!string.Equals(heldBy, _loggedHeldBy, StringComparison.OrdinalIgnoreCase))
        {
            _loggedHeldBy = heldBy;
            _log?.Info(LogCategory, heldBy is null
                ? "room attacks released: nobody outside our party is here"
                : $"room attacks held: {heldBy} is here and not in our party");
        }
        return heldBy;
    }

    // Why to carry on out of this room rather than fight in it, or null. Paradigm
    // says outright that someone is rooming; on Stock it is a guess from their
    // class and the monster count.
    public string? LeaveRoomReason()
    {
        string? reason = _pvpEnabled() ? FindLeaveReason() : null;
        if (!string.Equals(reason, _loggedLeaveReason, StringComparison.Ordinal))
        {
            _loggedLeaveReason = reason;
            if (reason is not null) _log?.Info(LogCategory, $"not fighting in this room: {reason}");
        }
        return reason;
    }

    // A player's arrival line, from RoomEntryWatcher.
    public void NoteArrival(RoomEntryArrivalEvent arrival)
    {
        if (arrival.Kind != EntityKind.Player) return;
        string given = PlayerObservation.SplitName(arrival.Name).Given;
        if (given.Length > 0) _arrivedAt[given] = arrival.At;
    }

    // Whether we watched them come into the room we were already standing in. Only
    // an arrival line counts: marking someone an Enemy over a room attack needs
    // better evidence than their not having been listed when we walked in.
    public bool ArrivedAfterUs(string given) =>
        _arrivedAt.TryGetValue(given, out DateTimeOffset at)
        && (_lastMoveSentAt() is not { } moveAt || at >= moveAt);

    // For the bug report.
    public string Describe()
    {
        if (!_pvpEnabled()) return "off (PvP isn't enabled for this realm)";
        List<string> here = PlayersHere();
        if (here.Count == 0) return "no players here";
        List<string> parts = new(here.Count);
        foreach (string given in here)
        {
            string standing = _inParty(given) ? "party" : "outside the party";
            string where = _residents.Contains(given) ? "here before us" : "arrived after us";
            string rooming = IsRoomAttacking(given) ? ", room-attacking" : "";
            parts.Add($"{given} ({standing}, {_classOf(given) ?? "class unknown"}, {where}{rooming})");
        }
        return string.Join("; ", parts);
    }

    private string? FindLeaveReason()
    {
        if (_classifier.Current is not { } obs) return null;

        int monsters = 0;
        foreach (RoomEntity e in obs.Entities)
            if (e.Kind == EntityKind.Monster) monsters++;

        foreach (string given in PlayersHere())
        {
            if (!IsBystander(given) || !_residents.Contains(given)) continue;
            if (IsRoomAttacking(given)) return $"{given} is room-attacking here";
            if (_realm() == RealmType.Stock
                && monsters >= StockRoomingMonsterCount
                && MayRoomAttack(given, out string what))
                return $"{given} ({what}) is here with {monsters} monsters";
        }
        return null;
    }

    private bool IsBystander(string given) => !_inParty(given);

    private bool IsRoomAttacking(string given) =>
        _roomAttackSeenAt.TryGetValue(given, out DateTimeOffset at)
        && (_lastMoveSentAt() is not { } moveAt || at >= moveAt);

    // A class we can't name, or one the game data doesn't list, counts as able: the
    // cost of guessing wrong is standing in a room attack.
    private bool MayRoomAttack(string given, out string what)
    {
        if (_classOf(given) is not { Length: > 0 } cls)
        {
            what = "class unknown";
            return true;
        }
        what = cls;
        if (!_fromLevelByClass.TryGetValue(cls, out int? fromLevel))
            _fromLevelByClass[cls] = fromLevel = _roomAttackFromLevel(cls);
        if (fromLevel is not { } from) return true;
        if (from <= 0) return false;
        return _levelOf(given) is not { } level || level >= from;
    }

    private List<string> PlayersHere()
    {
        List<string> names = new();
        if (_classifier.Current is not { } obs) return names;
        foreach (RoomEntity e in obs.Entities)
        {
            if (e.Kind != EntityKind.Player) continue;
            string given = PlayerObservation.SplitName(e.ResolvedName).Given;
            if (given.Length > 0) names.Add(given);
        }
        return names;
    }

    // The new room's "Also here:" is parsed before the tracker confirms the move
    // (the classifier keeps it across the confirmation), so a room change can't be
    // told from the observation's source alone. The move's send time can: the first
    // roster read after it is who was here when we arrived.
    private void OnEntitiesObserved(RoomEntitiesObservation obs)
    {
        DateTimeOffset? moveAt = _lastMoveSentAt();
        if (moveAt != _residentsMoveAt)
        {
            _residentsMoveAt = moveAt;
            _residents.Clear();
            _residentsRead = false;
        }

        if (_residentsRead) return;
        if (moveAt is { } sent && obs.At < sent) return;
        if (obs.Source == RoomObservationSource.RoomChange)
        {
            // The room was empty when we arrived: whoever turns up now came in after us.
            _residentsRead = true;
            return;
        }
        if (obs.Source != RoomObservationSource.AlsoHere) return;

        foreach (RoomEntity e in obs.Entities)
        {
            if (e.Kind != EntityKind.Player) continue;
            string given = PlayerObservation.SplitName(e.ResolvedName).Given;
            if (given.Length > 0) _residents.Add(given);
        }
        _residentsRead = true;
    }

    // "<player> is poised to assault the room!" is only shown on entering a room
    // where they are already rooming, so the line itself says they were here first.
    private void OnRoomPoised(MatchResult match)
    {
        if (match.Groups.Count == 0 || match.Groups[0].Length == 0) return;
        string given = match.Groups[0];
        _residents.Add(given);
        NoteRoomAttack(given);
    }

    private void OnAttackAnnounce(MatchResult match)
    {
        if (match.Groups.Count < 2 || match.Groups[0].Length == 0) return;
        if (!string.Equals(match.Groups[1], CombatManager.RoomWildcardTarget, StringComparison.OrdinalIgnoreCase))
            return;
        NoteRoomAttack(match.Groups[0]);
    }

    private void NoteRoomAttack(string given)
    {
        _roomAttackSeenAt[given] = _now();
        if (!_pvpEnabled() || _inParty(given)) return;
        RoomAttackSeen?.Invoke();
    }

    public void Dispose()
    {
        _classifier.EntitiesObserved -= OnEntitiesObserved;
        _poisedSub.Dispose();
        _announceSub.Dispose();
    }
}

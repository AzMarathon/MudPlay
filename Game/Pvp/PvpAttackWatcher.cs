using System.Collections.Specialized;
using System.Text.RegularExpressions;
using MudPlay.Game.Combat;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Pvp;

// Recognises another player attacking us and keeps the relationship in step: a
// Neutral who attacks becomes an Enemy, saved on their record (GAME_MECHANICS
// "Damage lines", Client policy). Three signs count:
//
//   - "<player> moves to attack you!";
//   - a damage line naming a player (the backstop for a missed announce);
//   - a room attack started by a player who came into the room after us.
//
// A room attack is never aimed, so it only counts from a player we watched walk in
// on us: not one cast in a room that was theirs first, and not one from someone who
// was in our party until a moment ago (a teleport broke the party up and the spell
// went out before they rejoined).
public sealed partial class PvpAttackWatcher : IDisposable
{
    public const string LogCategory = "PvP";

    // How long after leaving our party a player's room attack still isn't read as
    // aimed at us: long enough to cover a party re-forming after a teleport.
    public static readonly TimeSpan PartySplitGrace = TimeSpan.FromMinutes(2);

    private const int RecentCap = 12;
    private static readonly TimeSpan RepeatQuiet = TimeSpan.FromSeconds(10);

    private readonly RoomEntityClassifier _classifier;
    private readonly PvpRoomSafety _room;
    private readonly PlayerDatabase _players;
    private readonly PartyState _party;
    private readonly Func<bool> _pvpEnabled;
    private readonly Func<bool> _flipFriends;
    private readonly Func<string?> _ownGivenName;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;
    private readonly List<IDisposable> _subs = new();

    private readonly HashSet<string> _partyNow = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _leftPartyAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PvpAttack> _recent = new();
    private bool _warnedPvpOff;
    private bool _disposed;

    // Every recognised attack on us, after any change to the relationship.
    public event Action<PvpAttack>? Attacked;

    // The latest attacks, oldest first, for the bug report.
    public IReadOnlyList<PvpAttack> Recent => _recent;

    public PvpAttackWatcher(
        MessageRouter router,
        RoomEntityClassifier classifier,
        PvpRoomSafety room,
        PlayerDatabase players,
        PartyState party,
        Func<bool> pvpEnabled,
        Func<bool> flipFriends,
        Func<string?> ownGivenName,
        LogService? log = null,
        Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        _classifier = classifier;
        _room = room;
        _players = players;
        _party = party;
        _pvpEnabled = pvpEnabled;
        _flipFriends = flipFriends;
        _ownGivenName = ownGivenName;
        _log = log;
        _now = now ?? (() => DateTimeOffset.Now);

        foreach (PartyMember m in _party.Members) _partyNow.Add(GivenOf(m.Name));
        _party.Members.CollectionChanged += OnPartyChanged;

        _subs.Add(router.Subscribe(KnownPatterns.PlayerAttacksYou, OnAttacksYou));
        _subs.Add(router.Subscribe(KnownPatterns.IncomingDamage, OnIncomingDamage));
        _subs.Add(router.Subscribe(KnownPatterns.PartyAttackAnnounce, OnAttackAnnounce));
    }

    private void OnAttacksYou(MatchResult match)
    {
        if (match.Groups.Count == 0 || match.Groups[0].Length == 0) return;
        Note(match.Groups[0], PvpAttackKind.Announced);
    }

    // "<Name> [critically] <verb> you for N damage!" with the name a player's. A
    // monster's line leads with "The", and a room spell's names nobody.
    [GeneratedRegex(@"^(?<player>[A-Z][A-Za-z]+) (?:[a-z]+ ){1,3}you for \d+ damage!")]
    private static partial Regex PlayerDamageLine();

    private void OnIncomingDamage(MatchResult match)
    {
        Match m = PlayerDamageLine().Match(match.Text);
        if (!m.Success) return;
        string name = m.Groups["player"].Value;
        if (_classifier.Classify(name).Kind != EntityKind.Player) return;
        Note(name, PvpAttackKind.Damage);
    }

    private void OnAttackAnnounce(MatchResult match)
    {
        if (match.Groups.Count < 2 || match.Groups[0].Length == 0) return;
        if (!string.Equals(match.Groups[1], CombatManager.RoomWildcardTarget, StringComparison.OrdinalIgnoreCase))
            return;
        string name = match.Groups[0];
        if (!_pvpEnabled() || IsSelfOrParty(name)) return;

        if (!_room.ArrivedAfterUs(name))
        {
            _log?.Info(LogCategory,
                $"{name}'s room attack isn't taken as an attack on us: they weren't seen coming in after us");
            return;
        }
        if (_leftPartyAt.TryGetValue(name, out DateTimeOffset left) && _now() - left < PartySplitGrace)
        {
            _log?.Info(LogCategory,
                $"{name}'s room attack isn't taken as an attack on us: they were in our party "
                + $"{(_now() - left).TotalSeconds:0}s ago");
            return;
        }
        Note(name, PvpAttackKind.RoomAttack);
    }

    private void Note(string name, PvpAttackKind kind)
    {
        if (_disposed || IsSelfOrParty(name)) return;
        if (!_pvpEnabled())
        {
            if (_warnedPvpOff) return;
            _warnedPvpOff = true;
            _log?.Warn(LogCategory,
                $"{name} attacked us, but PvP isn't enabled for this realm (Settings → BBS), so nothing is done about it");
            return;
        }

        string given = GivenOf(name);
        if (_players.Find(given) is null) _players.AddManual(given, string.Empty, DateTime.UtcNow);
        PlayerRecord? record = _players.Find(given);
        PlayerRelationship was = record?.Relationship ?? PlayerRelationship.Neutral;

        bool flip = was == PlayerRelationship.Neutral
                    || (was == PlayerRelationship.Friend && _flipFriends());
        if (flip) flip = _players.SetRelationship(given, PlayerRelationship.Enemy, record?.PvpResponse);
        PlayerRelationship now = flip ? PlayerRelationship.Enemy : was;

        PvpAttack attack = new(given, kind, now, flip, _now());

        // A fight prints an announce and then a damage line a round: one entry per
        // attacker per burst is enough for the log and the bug report.
        bool sameBurst = !flip
            && _recent.Count > 0
            && string.Equals(_recent[^1].Player, given, StringComparison.OrdinalIgnoreCase)
            && attack.At - _recent[^1].At < RepeatQuiet;
        if (!sameBurst)
        {
            _recent.Add(attack);
            if (_recent.Count > RecentCap) _recent.RemoveAt(0);
            if (flip)
                _log?.Warn(LogCategory, $"{given} attacked us ({Describe(kind)}): was {was}, now marked Enemy");
            else
                _log?.Info(LogCategory, $"{given} attacked us ({Describe(kind)}); they are {was}"
                    + (was == PlayerRelationship.Friend ? " and stay one" : ""));
        }
        Attacked?.Invoke(attack);
    }

    private static string Describe(PvpAttackKind kind) => kind switch
    {
        PvpAttackKind.Announced => "moved to attack",
        PvpAttackKind.Damage => "hit us",
        _ => "walked in and room-attacked",
    };

    private bool IsSelfOrParty(string name)
    {
        string given = GivenOf(name);
        return given.Length == 0
            || string.Equals(given, GivenOf(_ownGivenName() ?? string.Empty), StringComparison.OrdinalIgnoreCase)
            || _party.HasMember(given);
    }

    private static string GivenOf(string name) => PlayerObservation.SplitName(name).Given;

    // The roster is rebuilt wholesale at times (a Reset carries no old items), so
    // departures are found by comparing against our own copy of it.
    private void OnPartyChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        HashSet<string> current = new(StringComparer.OrdinalIgnoreCase);
        foreach (PartyMember m in _party.Members) current.Add(GivenOf(m.Name));

        DateTimeOffset now = _now();
        foreach (string was in _partyNow)
            if (!current.Contains(was)) _leftPartyAt[was] = now;

        _partyNow.Clear();
        _partyNow.UnionWith(current);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _party.Members.CollectionChanged -= OnPartyChanged;
        foreach (IDisposable sub in _subs) sub.Dispose();
    }
}

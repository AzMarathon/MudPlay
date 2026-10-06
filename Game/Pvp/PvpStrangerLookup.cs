using MudPlay.Game.Combat;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Pvp;

// A player we have no record of is just an unplaced name to the room reader: they
// hold no room attack and draw no response. `who` lists everyone online, so on a
// PvP realm an unplaced name that looks like a player's, or someone entering the
// realm we don't know, sends one `who` to fill the records in.
//
// Kept from spamming: at most one `who` per MinGap however many names turn up (a
// fresh realm knows nobody), and a name already asked about isn't asked about again
// for NameRetry. Until that `who` has been read, the name counts as a player for
// the room-attack hold; if the list didn't carry it, it wasn't one.
public sealed class PvpStrangerLookup : IDisposable
{
    public const string LogCategory = "PvP";

    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan NameRetry = TimeSpan.FromMinutes(5);

    private readonly RoomEntityClassifier _classifier;
    private readonly PlayerDatabase _players;
    private readonly Func<bool> _pvpEnabled;
    private readonly Action _sendWho;
    private readonly Action<TimeSpan, Action> _schedule;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;
    private readonly IDisposable _entersSub;

    private readonly Dictionary<string, DateTimeOffset> _askedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastWhoAt = DateTimeOffset.MinValue;
    private bool _whoQueued;

    // A `who` is on its way and its list hasn't been read: it answers for every
    // name that turns up meanwhile (a room of strangers is one `who`, not several).
    private bool _whoOutstanding;

    public PvpStrangerLookup(
        MessageRouter router,
        RoomEntityClassifier classifier,
        PlayerDatabase players,
        Func<bool> pvpEnabled,
        Action sendWho,
        Action<TimeSpan, Action> schedule,
        LogService? log = null,
        Func<DateTimeOffset>? now = null)
    {
        _classifier = classifier;
        _players = players;
        _pvpEnabled = pvpEnabled;
        _sendWho = sendWho;
        _schedule = schedule;
        _log = log;
        _now = now ?? (() => DateTimeOffset.Now);

        _classifier.EntitiesObserved += OnEntitiesObserved;
        _players.ObservationRecorded += OnPlayerRecorded;
        _entersSub = router.Subscribe(KnownPatterns.PlayerEnters, OnPlayerEnters);
    }

    // An unplaced name in the room that may be a player: asked about, and `who`
    // hasn't answered yet.
    public bool MayBePlayer(string rawName) =>
        _pending.Contains(PlayerObservation.SplitName(rawName).Given);

    private void OnEntitiesObserved(RoomEntitiesObservation obs)
    {
        if (!_pvpEnabled()) return;
        foreach (RoomEntity e in obs.Entities)
        {
            if (e.Kind != EntityKind.Unknown || !LooksLikeAPlayer(e.RawName)) continue;
            Ask(PlayerObservation.SplitName(e.RawName).Given, inTheRoom: true);
        }
    }

    private void OnPlayerEnters(MatchResult match)
    {
        if (!_pvpEnabled() || match.Groups.Count == 0 || match.Groups[0].Length == 0) return;
        if (_players.Find(match.Groups[0]) is null) Ask(match.Groups[0], inTheRoom: false);
    }

    private void Ask(string given, bool inTheRoom)
    {
        if (given.Length == 0) return;
        DateTimeOffset now = _now();
        if (_askedAt.TryGetValue(given, out DateTimeOffset asked) && now - asked < NameRetry) return;
        _askedAt[given] = now;
        if (inTheRoom) _pending.Add(given);

        if (_whoQueued) return;
        if (_whoOutstanding && now - _lastWhoAt < MinGap) return;
        TimeSpan wait = _lastWhoAt + MinGap - now;
        if (wait <= TimeSpan.Zero)
        {
            SendWho(given);
            return;
        }
        _whoQueued = true;
        _schedule(wait, () =>
        {
            _whoQueued = false;
            SendWho(given);
        });
    }

    private void SendWho(string given)
    {
        _lastWhoAt = _now();
        _whoOutstanding = true;
        _log?.Info(LogCategory, $"no record of {given}: sending who");
        _sendWho();
    }

    // `who` has listed everyone online (from WhoListParser.ListRead). A pending name
    // it gave a record is a player now; one it didn't isn't, and stops counting as
    // one.
    public void NoteWhoRead()
    {
        _whoOutstanding = false;
        if (_pending.Count == 0) return;
        _pending.Clear();
        _classifier.ReclassifyUnknown();
    }

    // A record from anywhere else (the top list, a look) settles a pending name too.
    private void OnPlayerRecorded(string given)
    {
        if (_pending.Remove(given)) _classifier.ReclassifyUnknown();
    }

    // Player names are capitalised, one or two words of letters; monsters are listed
    // in lower case.
    private static bool LooksLikeAPlayer(string rawName)
    {
        string[] words = rawName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 1 or > 2 || !char.IsUpper(words[0][0])) return false;
        foreach (string word in words)
            foreach (char c in word)
                if (!char.IsLetter(c) && c != '\'' && c != '-') return false;
        return true;
    }

    public void Dispose()
    {
        _classifier.EntitiesObserved -= OnEntitiesObserved;
        _players.ObservationRecorded -= OnPlayerRecorded;
        _entersSub.Dispose();
    }
}

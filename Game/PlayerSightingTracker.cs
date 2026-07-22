using System.Linq;
using FujinTerm.Game.Combat;
using FujinTerm.Game.Map;
using FujinTerm.Models.GameData;
using FujinTerm.Models.Profile;
using FujinTerm.Services;

namespace FujinTerm.Game;

// Per-character log of players seen in the world, surfaced by the Session Stats
// → Players Seen window. A sighting is recorded two ways, matching how the user
// experiences "seeing" someone:
//
//  • a known player matched in the current room's "Also here:" listing
//    (RoomEntityClassifier.EntitiesObserved, EntityKind.Player) — the same hook
//    GreetManager greets off, and
//  • a player entering the current room via RoomEntryWatcher.ArrivalObserved —
//    the same hook PlayerLookManager look-at's off. That event covers both an
//    open walk-in ("<name> walks into the room …") AND a failed sneak we
//    perceive ("You notice <name> sneaking in from the <dir>."); the watcher
//    classifies the sneaker as EntityKind.Player, so both reach NoteArrival.
//
// The also-here path is deduped per room-visit: standing still in a room (or
// re-pressing Enter to redisplay it) re-fires the listing, but a present player
// is counted once per visit, not once per redisplay. Leaving and re-entering the
// room starts a fresh visit, so a genuine re-encounter counts again. A walk-in
// always counts — it's an explicit fresh arrival — and marks the player counted
// for the current visit so the room redisplay that follows doesn't double-count.
//
// Rows aggregate by GIVEN name (the stable identity across train-stats family
// renames). The in-memory dictionary is authoritative during a session; it
// hydrates from CharacterProfile.PlayersSeen on profile load and snapshots back
// on profile save (ProfileSaving) — the same write-on-next-save persistence
// RoomTracker.NoteDeath uses for the death history, so per-sighting disk churn
// is avoided. The user-driven Clear button saves immediately so the wipe sticks.
//
// Owns no room-source subscriptions — AppServices wires RoomClassifier /
// RoomEntry events to NoteAlsoHere / NoteArrival, matching TransactionHistory-
// Tracker and keeping the dedup logic testable behind an injectable clock and a
// current-room provider. It DOES own its ProfileService subscriptions (like
// PlayerDatabase) so hydrate / snapshot follow the loaded character.
//
// Only our own character is filtered out (the "Also here:" line excludes the
// local player by game convention anyway, and we never see ourselves walk in);
// party members ARE logged — by the user's definition they're players we see in
// the room, and excluding them would silently drop rows.
public sealed class PlayerSightingTracker : IDisposable
{
    private readonly Func<Room?> _currentRoom;
    private readonly ProfileService? _profile;
    private readonly Func<string?> _selfNameProvider;
    private readonly Func<DateTimeOffset> _clock;

    // Authoritative in-session store, keyed on given name (case-insensitive).
    private readonly Dictionary<string, PlayerSighting> _sightings =
        new(StringComparer.OrdinalIgnoreCase);

    // Players already counted for the current room-visit — reset when the current
    // room changes so a stationary occupant (or repeated room redisplays) counts
    // once per visit rather than once per "Also here:" re-fire.
    private readonly HashSet<string> _countedThisVisit =
        new(StringComparer.OrdinalIgnoreCase);
    private RoomKey? _visitRoom;

    private bool _disposed;

    // Raised after any record or clear, so the Players Seen VM rebuilds. Fires on
    // the dispatch thread (all sources marshal upstream).
    public event Action? Changed;

    public PlayerSightingTracker(
        Func<Room?> currentRoom,
        ProfileService? profile,
        Func<string?> selfNameProvider,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(currentRoom);
        ArgumentNullException.ThrowIfNull(selfNameProvider);
        _currentRoom = currentRoom;
        _profile = profile;
        _selfNameProvider = selfNameProvider;
        _clock = clock ?? (static () => DateTimeOffset.Now);

        if (_profile is not null)
        {
            _profile.ProfileLoaded += OnProfileLoaded;
            _profile.ProfileClosed += OnProfileClosed;
            _profile.ProfileSaving += OnProfileSaving;
            Hydrate(_profile.Current);
        }
    }

    // Point-in-time copy of the logged players.
    public IReadOnlyList<PlayerSighting> Snapshot() => _sightings.Values.ToArray();

    // ----- Source forwarders (wired by AppServices) ---------------------

    // "Also here:" room listing. Only the real listing feeds the room-presence
    // path; the Arrival re-fire is handled by NoteArrival (once per walk-in) and
    // the death / departure / room-change re-fires never add a player.
    public void NoteAlsoHere(RoomEntitiesObservation obs)
    {
        if (obs.Source != RoomObservationSource.AlsoHere) return;
        if (obs.Entities is null || obs.Entities.Count == 0) return;

        RollVisit();

        bool any = false;
        foreach (RoomEntity e in obs.Entities)
        {
            if (e.Kind != EntityKind.Player) continue;
            (string given, _) = PlayerObservation.SplitName(e.ResolvedName);
            if (string.IsNullOrEmpty(given)) continue;
            if (IsSelf(given)) continue;
            if (!_countedThisVisit.Add(given)) continue; // already counted this visit
            Record(given);
            any = true;
        }
        if (any) Changed?.Invoke();
    }

    // A player entering the current room — an open walk-in or a failed sneak we
    // perceive ("You notice <name> sneaking in …"). RoomEntryWatcher tags the
    // sneaker EntityKind.Player, so the one Kind check below admits both.
    public void NoteArrival(RoomEntryArrivalEvent e)
    {
        if (e.Kind != EntityKind.Player) return;
        (string given, _) = PlayerObservation.SplitName(e.Name);
        if (string.IsNullOrEmpty(given)) return;
        if (IsSelf(given)) return;

        RollVisit();
        // A walk-in is always a fresh sighting; mark them counted for this visit
        // so the room redisplay that follows the arrival doesn't count them twice.
        _countedThisVisit.Add(given);
        Record(given);
        Changed?.Invoke();
    }

    // User-driven wipe — the Players Seen window's Clear button. Saves the empty
    // list immediately (matching the Transaction window's Clear) so the wipe
    // survives a restart even if no later profile save happens.
    public void Clear()
    {
        _sightings.Clear();
        _countedThisVisit.Clear();
        _visitRoom = null;
        if (_profile?.Current is not null)
        {
            _profile.Current.PlayersSeen = null;
            _profile.Save();
        }
        Changed?.Invoke();
    }

    // ----- Recording ----------------------------------------------------

    // Start a new per-visit dedup window whenever the current room changes.
    private void RollVisit()
    {
        RoomKey? key = _currentRoom()?.Key;
        if (Nullable.Equals(key, _visitRoom)) return;
        _visitRoom = key;
        _countedThisVisit.Clear();
    }

    private void Record(string given)
    {
        Room? room = _currentRoom();
        DateTimeOffset now = _clock();
        string? roomName = room?.Name is { Length: > 0 } n ? n : null;

        if (_sightings.TryGetValue(given, out PlayerSighting? existing))
        {
            existing.LastSeen = now;
            existing.TimesSeen++;
            existing.Map = room?.Key.Map ?? 0;
            existing.Room = room?.Key.Room ?? 0;
            existing.RoomName = roomName;
        }
        else
        {
            _sightings[given] = new PlayerSighting
            {
                Name = given,
                LastSeen = now,
                TimesSeen = 1,
                Map = room?.Key.Map ?? 0,
                Room = room?.Key.Room ?? 0,
                RoomName = roomName,
            };
        }
    }

    private bool IsSelf(string given)
    {
        (string selfGiven, _) = PlayerObservation.SplitName(_selfNameProvider());
        return !string.IsNullOrEmpty(selfGiven)
            && selfGiven.Equals(given, StringComparison.OrdinalIgnoreCase);
    }

    // ----- Persistence --------------------------------------------------

    private void OnProfileLoaded(CharacterProfile profile) => Hydrate(profile);

    private void OnProfileClosed()
    {
        _sightings.Clear();
        _countedThisVisit.Clear();
        _visitRoom = null;
        Changed?.Invoke();
    }

    // Snapshot the in-memory rows onto the profile just before it's written —
    // the same next-save persistence the death history uses, so a busy area
    // doesn't force a whole-profile write per sighting.
    private void OnProfileSaving(CharacterProfile profile)
    {
        profile.PlayersSeen = _sightings.Count == 0
            ? null
            : _sightings.Values.OrderByDescending(s => s.LastSeen).ToList();
    }

    private void Hydrate(CharacterProfile? profile)
    {
        _sightings.Clear();
        _countedThisVisit.Clear();
        _visitRoom = null;
        if (profile?.PlayersSeen is { } rows)
        {
            foreach (PlayerSighting s in rows)
            {
                if (string.IsNullOrWhiteSpace(s.Name)) continue;
                (string given, _) = PlayerObservation.SplitName(s.Name);
                if (string.IsNullOrEmpty(given)) continue;
                // Collapse any duplicate rows (older data) by given name — keep
                // the newer LastSeen / location and sum the counts.
                if (_sightings.TryGetValue(given, out PlayerSighting? existing))
                {
                    existing.TimesSeen += s.TimesSeen;
                    if (s.LastSeen > existing.LastSeen)
                    {
                        existing.LastSeen = s.LastSeen;
                        existing.Map = s.Map;
                        existing.Room = s.Room;
                        existing.RoomName = s.RoomName;
                    }
                }
                else
                {
                    _sightings[given] = new PlayerSighting
                    {
                        Name = given,
                        LastSeen = s.LastSeen,
                        Map = s.Map,
                        Room = s.Room,
                        RoomName = s.RoomName,
                        TimesSeen = s.TimesSeen,
                    };
                }
            }
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_profile is not null)
        {
            _profile.ProfileLoaded -= OnProfileLoaded;
            _profile.ProfileClosed -= OnProfileClosed;
            _profile.ProfileSaving -= OnProfileSaving;
        }
    }
}

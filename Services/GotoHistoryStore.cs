using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;

namespace MudPlay.Services;

// Per-character recent walk-to destinations for the Navigation goto button. Mirrors
// FavoritesStore's ProfileLoaded / ProfileClosed wiring: hydrates from
// CharacterProfile.GotoHistory on profile load, writes back on every Record.
// Newest first, deduped, capped at 10. Persisted via ProfileService.Save.
//
// Singleton in AppServices. The Navigation view-model calls Record when a walk-to
// actually starts and subscribes to Changed to refresh the dropdown.
public sealed class GotoHistoryStore
{
    private const int MaxEntries = 10;

    private readonly ProfileService _profile;
    private readonly List<RoomKey> _history = new();   // newest first

    // Fires after Record or a profile swap.
    public event Action? Changed;

    public GotoHistoryStore(ProfileService profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _profile.ProfileLoaded += OnProfileLoaded;
        _profile.ProfileClosed += OnProfileClosed;
        if (_profile.Current is { } current) OnProfileLoaded(current);
    }

    // Recent destinations, newest first. Live view — read, don't mutate.
    public IReadOnlyList<RoomKey> All => _history;

    // Record a walk-to destination: promote it to the front, drop any earlier copy,
    // cap to 10, and persist. No-op with no profile loaded.
    public void Record(RoomKey key)
    {
        if (_profile.Current is not { } current) return;
        _history.RemoveAll(k => k.Equals(key));
        _history.Insert(0, key);
        if (_history.Count > MaxEntries) _history.RemoveRange(MaxEntries, _history.Count - MaxEntries);

        current.GotoHistory = _history.Select(k => $"{k.Map}/{k.Room}").ToList();
        _profile.Save();
        Changed?.Invoke();
    }

    private void OnProfileLoaded(CharacterProfile profile)
    {
        _history.Clear();
        if (profile.GotoHistory is { } list)
        {
            foreach (string s in list)
            {
                (int? map, int? room) = RoomSearchService.TryParseCoordinate(s);
                if (map is int m && room is int r && !_history.Any(k => k.Map == m && k.Room == r))
                    _history.Add(new RoomKey(m, r));
                if (_history.Count >= MaxEntries) break;
            }
        }
        Changed?.Invoke();
    }

    private void OnProfileClosed()
    {
        bool had = _history.Count > 0;
        _history.Clear();
        if (had) Changed?.Invoke();
    }
}

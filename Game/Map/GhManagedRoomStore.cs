using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Which gang-house rooms THIS character actively manages — the subset Start Sweep
// / Start Inventory actually visit. Per-CHARACTER, deliberately: the room labels
// themselves are shared per-BBS (see GhRoomLabelStore), but a BBS can hold members
// of several different gang houses, so each character picks which of the shared
// labels belong to the house they sweep. That keeps Roomba from routing across
// houses (or into one the character lacks the emblem for) just because another
// character on the BBS labeled it or a @roomba sync adopted it.
//
// Presence in the set = actively managed. A room isn't managed until the character
// deliberately opts in: the hand-add paths (Add Room box, map right-click) mark the
// room managed for the current character; a @roomba sync adopts the LABEL only and
// never touches this set, so synced rooms arrive unmanaged. Mirrors GotoHistoryStore
// / FavoritesStore's per-character ProfileLoaded / ProfileClosed persistence shape.
public sealed class GhManagedRoomStore
{
    private readonly ProfileService _profile;
    private readonly LogService? _log;
    private readonly HashSet<RoomKey> _managed = new();

    // Fires on a profile swap (load/close) — i.e. the whole managed set changed
    // out from under observers. NOT fired on a single SetManaged toggle: the tab's
    // checkbox already carries that, and firing here would rebuild the very row list
    // the checkbox lives in mid-click.
    public event Action? Changed;

    public GhManagedRoomStore(ProfileService profile, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        _log = log;
        _profile.ProfileLoaded += OnProfileLoaded;
        _profile.ProfileClosed += OnProfileClosed;
        if (_profile.Current is { } current) Hydrate(current);
    }

    public bool IsManaged(RoomKey key) => _managed.Contains(key);

    public int Count => _managed.Count;

    public bool Any => _managed.Count > 0;

    // Opt a room in / out of this character's sweep. Persists immediately; no-op with
    // no profile loaded, or when already in the requested state.
    public void SetManaged(RoomKey key, bool managed)
    {
        if (_profile.Current is not { } current) return;
        bool changed = managed ? _managed.Add(key) : _managed.Remove(key);
        if (!changed) return;

        current.GhManagedRooms = _managed.Select(k => $"{k.Map}/{k.Room}").ToList();
        _profile.Save();
        _log?.Info("GhSweep", $"{key} actively-managed = {managed} (this character)");
    }

    private void OnProfileLoaded(CharacterProfile profile)
    {
        Hydrate(profile);
        Changed?.Invoke();
    }

    private void OnProfileClosed()
    {
        bool had = _managed.Count > 0;
        _managed.Clear();
        if (had) Changed?.Invoke();
    }

    private void Hydrate(CharacterProfile profile)
    {
        _managed.Clear();
        if (profile.GhManagedRooms is not { } list) return;
        foreach (string s in list)
        {
            (int? map, int? room) = RoomSearchService.TryParseCoordinate(s);
            if (map is int m && room is int r) _managed.Add(new RoomKey(m, r));
        }
    }
}
